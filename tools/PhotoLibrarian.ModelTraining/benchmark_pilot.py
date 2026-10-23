"""Real-photo pilot CPU/DirectML diagnostics. No production approval or silent provider retry."""

from __future__ import annotations

import argparse
from collections import Counter
import importlib.metadata
import json
from pathlib import Path
import platform
import subprocess
import threading
import time

import numpy as np
import onnxruntime as ort
import psutil

from pilot_inference import infer, load_bundle, prepare_tensor, validate_session, write_json
from prepare_dataset import file_hash


def placement(events: list[dict]) -> dict:
    nodes = [event for event in events if event.get("cat") == "Node"
             and event.get("args", {}).get("provider")]
    if not nodes:
        raise ValueError("Runtime profile contains no provider node evidence")
    unique = {(event["name"], event["args"]["provider"]) for event in nodes}
    return {"uniqueNodesByProvider": dict(Counter(provider for _, provider in unique)),
            "nodeExecutionsByProvider": dict(Counter(event["args"]["provider"] for event in nodes)),
            "nodeDurationUsByProvider": {
                provider: sum(event["dur"] for event in nodes if event["args"]["provider"] == provider)
                for provider in sorted({event["args"]["provider"] for event in nodes})},
            "cpuNodes": sorted(name for name, provider in unique if provider == "CPUExecutionProvider")}


def session_options(threads: int, strict: bool = False):
    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.enable_mem_pattern = False
    if strict:
        options.add_session_config_entry("session.disable_cpu_ep_fallback", "1")
    return options


def hardware_info() -> dict:
    if platform.system() != "Windows":
        return {"processor": platform.processor(), "driverInventory": "Windows CIM query not applicable"}
    command = (
        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); "
        "@{ cpu=@(Get-CimInstance Win32_Processor | Select-Object Name); "
        "videoControllers=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion) } "
        "| ConvertTo-Json -Depth 4 -Compress"
    )
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                            check=True, capture_output=True, text=True, encoding="utf-8")
    return json.loads(result.stdout)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--images", type=Path, required=True, help="Private/local images; EXIF orientation applied")
    parser.add_argument("--output", type=Path, required=True, help="New external diagnostic directory")
    parser.add_argument("--provider", choices=["cpu", "directml"], required=True)
    parser.add_argument("--limit", type=int, default=156)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--device-id", type=int, default=0, help="Explicit DirectML adapter index (not CUDA index)")
    args = parser.parse_args()
    if (args.output.exists() or not 1 <= args.limit <= 10000 or not 1 <= args.threads <= 32
            or not 0 <= args.device_id <= 15
            or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2])):
        parser.error("New external output, limit 1..10000 and threads 1..32 required")
    model, bundle = load_bundle(args.bundle)
    images = sorted(path for path in args.images.rglob("*")
                    if path.suffix.lower() in {".jpg", ".jpeg", ".png"})
    images = images[:args.limit]
    if not images:
        raise ValueError("No supported images")
    provider = "DmlExecutionProvider" if args.provider == "directml" else "CPUExecutionProvider"
    if provider not in ort.get_available_providers():
        raise RuntimeError(f"Requested {provider} unavailable: {ort.get_available_providers()}")
    hardware = hardware_info()
    providers = ([provider] if args.provider == "cpu"
                 else [(provider, {"device_id": args.device_id}), "CPUExecutionProvider"])
    args.output.mkdir(parents=True, exist_ok=False)
    process = psutil.Process()
    baseline = process.memory_info().rss
    peak = [baseline]
    stop = threading.Event()

    def sample_memory():
        while not stop.wait(0.005):
            peak[0] = max(peak[0], process.memory_info().rss)

    monitor = threading.Thread(target=sample_memory, daemon=True)
    monitor.start()
    try:
        start = time.perf_counter()
        session = ort.InferenceSession(str(model), sess_options=session_options(args.threads),
                                       providers=providers)
        session.disable_fallback()
        if provider not in session.get_providers():
            raise RuntimeError("Requested provider failed initialization; refusing a fallback-only run")
        load_ms = (time.perf_counter() - start) * 1000
        validate_session(session, bundle["labels"])
        first = prepare_tensor(images[0])
        warmup_ms = []
        for _ in range(5):
            start = time.perf_counter()
            infer(session, first, len(bundle["labels"]))
            warmup_ms.append((time.perf_counter() - start) * 1000)
        scores, records, preprocess, inference, total = [], [], [], [], []
        for path in images:
            identity = {"image": path.relative_to(args.images).as_posix(), "sha256": file_hash(path)}
            start = time.perf_counter()
            tensor = prepare_tensor(path)
            prepared = time.perf_counter()
            score = infer(session, tensor, len(bundle["labels"]))
            completed = time.perf_counter()
            preprocess.append((prepared - start) * 1000)
            inference.append((completed - prepared) * 1000)
            total.append((completed - start) * 1000)
            scores.append(score)
            records.append(identity)
        peak[0] = max(peak[0], process.memory_info().rss)
    finally:
        stop.set()
        monitor.join()
    measured_peak = peak[0]
    # Profiling and a second strict session are outside the timed/memory interval.
    profile_options = session_options(args.threads)
    profile_options.enable_profiling = True
    profile_options.profile_file_prefix = str(args.output / "node-profile")
    profile = ort.InferenceSession(str(model), sess_options=profile_options,
                                  providers=providers)
    profile.disable_fallback()
    if provider not in profile.get_providers():
        raise RuntimeError("Requested provider missing from profiling session")
    infer(profile, first, len(bundle["labels"]))
    profile_path = Path(profile.end_profiling())
    nodes = placement(json.loads(profile_path.read_text(encoding="utf-8")))
    strict = {"attempted": args.provider == "directml"}
    if args.provider == "directml":
        try:
            strict_session = ort.InferenceSession(
                str(model), sess_options=session_options(args.threads, strict=True),
                providers=[(provider, {"device_id": args.device_id})])
            strict_session.disable_fallback()
            if provider not in strict_session.get_providers():
                raise RuntimeError("Strict provider initialization unexpectedly fell back")
            infer(strict_session, first, len(bundle["labels"]))
            strict["passed"] = True
        except (ort.capi.onnxruntime_pybind11_state.Fail,
                ort.capi.onnxruntime_pybind11_state.RuntimeException) as error:
            strict.update(passed=False, error=str(error))
    report = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "modelSha256": bundle["modelSha256"], "modelBytes": bundle["modelBytes"], "labels": bundle["labels"],
        "codeSha256": {name: file_hash(Path(__file__).with_name(name)) for name in
                       ["benchmark_pilot.py", "pilot_inference.py", "requirements-evaluation.txt"]},
        "platform": platform.platform(), "python": platform.python_version(),
        "hardware": hardware, "ortVersion": ort.__version__,
        "environment": dict(sorted((d.metadata["Name"], d.version) for d in importlib.metadata.distributions())),
        "requestedProvider": provider, "availableProviders": ort.get_available_providers(),
        "requestedDirectMLDeviceId": args.device_id if args.provider == "directml" else None,
        "sessionProviders": session.get_providers(), "pythonRuntimeFallbackDisabled": True,
        "nodePlacement": nodes, "strictNoCpuFallback": strict, "intraOpThreads": args.threads,
        "warmups": 5, "images": records, "imageCount": len(records), "loadMs": load_ms,
        "firstInferenceMs": warmup_ms[0], "warmupInferenceMs": warmup_ms,
        "preprocessing": "PIL EXIF transpose, RGB, full-image bilinear 224; ImageNet normalization",
        "timingMs": {name: {"p50": float(np.percentile(values, 50)),
                           "p95": float(np.percentile(values, 95))}
                     for name, values in [("decodePreprocess", preprocess), ("inference", inference),
                                          ("decodePreprocessInference", total)]},
        "memory": {"baselineRssBytes": baseline, "sampledPeakRssBytes": measured_peak,
                   "deltaRssBytes": measured_peak - baseline, "samplingIntervalMs": 5,
                   "scope": "host process RSS only; not GPU VRAM, system peak or app memory"},
        "limitations": [
            "One sequential local run, after five warmups; file cache is uncontrolled.",
            "Host/Python diagnostic, not Windows app end-to-end latency.",
            "Hashing, result writing and provider profiling excluded from timed interval.",
            "Private regression images are development evidence, not an untouched final test.",
        ],
    }
    np.savez_compressed(args.output / "scores.npz", scores=np.asarray(scores))
    report["scoresSha256"] = file_hash(args.output / "scores.npz")
    write_json(args.output / "benchmark.json", report)
    summary = {key: report[key] for key in
                      ["requestedProvider", "imageCount", "timingMs", "memory", "nodePlacement",
                       "strictNoCpuFallback"]}
    summary["nodePlacement"] = {"uniqueNodesByProvider": nodes["uniqueNodesByProvider"]}
    print(json.dumps(summary, indent=2), flush=True)


if __name__ == "__main__":
    main()
