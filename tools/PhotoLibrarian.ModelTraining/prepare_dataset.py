"""Read-only local photo audit and deterministic grouped partial-label splits.

No image decoder, network access, metadata writes, or third-party dependencies.
See README.md for the deliberately explicit input and label-review contract.
"""

from __future__ import annotations

import argparse
from collections import Counter
import csv
import hashlib
import json
from pathlib import Path


SPLITS = ("train", "validation", "calibration", "test")
STATES = {"positive": 1, "negative": 0, "unknown": -1}


def fingerprint(value: object) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def text(value: object, field: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{field} must be a nonempty string")
    return value


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def vocabulary(path: Path) -> tuple[list[dict], dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        expected = {"label", "category", "parent", "description", "exclude", "aliases"}
        if len(reader.fieldnames or []) != len(expected) or set(reader.fieldnames or []) != expected:
            raise ValueError(f"Vocabulary columns must be {sorted(expected)}")
        concepts = list(reader)
    if not concepts:
        raise ValueError("Vocabulary must not be empty")
    aliases: dict[str, str] = {}
    labels: set[str] = set()
    for item in concepts:
        if set(item) != expected or any(not isinstance(value, str) for value in item.values()):
            raise ValueError("Each vocabulary row must contain exactly the declared columns")
        for field in ("label", "category", "description", "exclude"):
            text(item[field], field)
        label = item["label"]
        if label in labels:
            raise ValueError(f"Duplicate canonical label: {label}")
        labels.add(label)
        for alias in [label, *filter(None, item["aliases"].split("|"))]:
            if alias in aliases:
                raise ValueError(f"Duplicate vocabulary spelling: {alias}")
            aliases[alias] = label
    parents = {item["label"]: item["parent"] for item in concepts}
    for label in labels:
        visited: set[str] = set()
        current = label
        while current:
            if current not in parents:
                raise ValueError(f"Unknown parent: {current}")
            if current in visited:
                raise ValueError(f"Hierarchy cycle at: {current}")
            visited.add(current)
            current = parents[current]
    return concepts, aliases


def load_records(path: Path, images_root: Path, aliases: dict[str, str]) -> list[dict]:
    root = images_root.resolve(strict=True)
    records = []
    ids: set[str] = set()
    with path.open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            row = json.loads(line, object_pairs_hook=unique_object)
            if not isinstance(row, dict):
                raise ValueError(f"Line {line_number}: record must be an object")
            required = {"id", "path", "source", "sourceId", "groups", "priorEvaluation",
                        "labels", "provenance"}
            if set(row) != required:
                raise ValueError(f"Line {line_number}: expected fields {sorted(required)}")
            for field in ("id", "path", "source", "sourceId"):
                text(row[field], field)
            if row["id"] in ids:
                raise ValueError(f"Duplicate record id: {row['id']}")
            ids.add(row["id"])
            if type(row["priorEvaluation"]) is not bool:
                raise ValueError("priorEvaluation must explicitly be true or false")
            if not isinstance(row["groups"], list) or not row["groups"]:
                raise ValueError("groups must contain reviewed event/near-duplicate group IDs")
            for group in row["groups"]:
                text(group, "group")
            provenance = row["provenance"]
            if not isinstance(provenance, dict):
                raise ValueError("provenance must be an object")
            for field in ("imageLicense", "annotationLicense", "attribution"):
                text(provenance.get(field), f"provenance.{field}")
            if not isinstance(row["labels"], dict):
                raise ValueError("labels must be an object")
            normalized: dict[str, str] = {}
            for label, state in row["labels"].items():
                if label not in aliases:
                    raise ValueError(f"Unmapped label: {label}; review mapping before preparation")
                if not isinstance(state, str) or state not in STATES:
                    raise ValueError(f"Invalid label state: {state}")
                canonical = aliases[label]
                if canonical in normalized and normalized[canonical] != state:
                    raise ValueError(f"Conflicting aliases for {canonical}")
                normalized[canonical] = state
            relative = Path(row["path"])
            if relative.is_absolute():
                raise ValueError("Photo paths must be relative to --images-root")
            photo = (root / relative).resolve(strict=True)
            if not photo.is_relative_to(root) or not photo.is_file():
                raise ValueError("Photo path must resolve to a file inside --images-root")
            records.append({**row, "path": str(photo), "labels": normalized,
                            "groups": sorted(set(row["groups"])), "sha256": file_hash(photo)})
    if not records:
        raise ValueError("Dataset must not be empty")
    return sorted(records, key=lambda row: row["id"])


def prepare(records: list[dict], concepts: list[dict], seed: str,
            test_sources: set[str] | None = None) -> tuple[list[dict], dict]:
    """Union all overlap constraints before assigning any component to a split."""
    test_sources = test_sources or set()
    sources = {row["source"] for row in records}
    if not test_sources <= sources:
        raise ValueError("Every held-out source must occur in the dataset")
    parents = list(range(len(records)))

    def find(index: int) -> int:
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    seen: dict[tuple[str, str], int] = {}
    duplicate_states: dict[tuple[int, str], str] = {}
    hierarchy = {item["label"]: item["parent"] for item in concepts}
    for index, row in enumerate(records):
        constraints = [("bytes", row["sha256"]), ("source-id", fingerprint(
            [row["source"], row["sourceId"]]))]
        for key in constraints:
            if key in seen:
                parents[find(index)] = find(seen[key])
            else:
                seen[key] = index
    # Only duplicate identities must agree; different photos in an event need not.
    for index, row in enumerate(records):
        for label, state in row["labels"].items():
            if state == "unknown":
                continue
            key = find(index), label
            if key in duplicate_states and duplicate_states[key] != state:
                raise ValueError(f"Contradictory annotations on duplicate image identities for {label}")
            duplicate_states[key] = state
    for (identity, label), state in duplicate_states.items():
        if state != "positive":
            continue
        parent = hierarchy[label]
        while parent:
            if duplicate_states.get((identity, parent)) == "negative":
                raise ValueError(f"Positive {label} contradicts negative ancestor {parent}")
            parent = hierarchy[parent]
    for index, row in enumerate(records):
        for group in row["groups"]:
            key = "group", group
            if key in seen:
                parents[find(index)] = find(seen[key])
            else:
                seen[key] = index

    components: dict[int, list[dict]] = {}
    for index, row in enumerate(records):
        components.setdefault(find(index), []).append(row)
    labels = [item["label"] for item in concepts]
    output = []
    group_counts: Counter = Counter()
    contaminated = 0
    for members in components.values():
        group_id = fingerprint(sorted(row["id"] for row in members))
        used_before = any(row["priorEvaluation"] for row in members)
        held_out = any(row["source"] in test_sources for row in members)
        if held_out and used_before:
            raise ValueError("Held-out source overlaps prior evaluation; choose a fresh test set")
        value = int(fingerprint([seed, group_id]), 16) / (1 << 256)
        if used_before:
            split = "train"
            contaminated += 1
        elif held_out:
            split = "test"
        elif test_sources:
            split = "train" if value < 0.8 else ("validation" if value < 0.9 else "calibration")
        else:
            split = ("train" if value < 0.7 else "validation" if value < 0.8
                     else "calibration" if value < 0.9 else "test")
        group_counts[split] += 1
        for row in members:
            output.append({**row, "groupId": group_id, "split": split,
                           "targets": [STATES[row["labels"].get(label, "unknown")] for label in labels]})
    output.sort(key=lambda row: row["id"])
    counts: dict[str, dict] = {}
    for position, label in enumerate(labels):
        per_split = {}
        for split in SPLITS:
            subset = [row for row in output if row["split"] == split]
            states = Counter(row["targets"][position] for row in subset)
            per_split[split] = {
                "positive": states[1], "negative": states[0], "unknown": states[-1],
                "positiveGroups": len({row["groupId"] for row in subset if row["targets"][position] == 1}),
                "negativeGroups": len({row["groupId"] for row in subset if row["targets"][position] == 0}),
            }
        counts[label] = per_split
    warnings = [
        "Exact byte matches and declared groups only: near-duplicate/event review is still required.",
        "Annotation counts do not establish annotation correctness or population coverage.",
        "Do not train or select thresholds on test; priorEvaluation must include vocabulary/model reviews.",
    ]
    for split in SPLITS:
        if not group_counts[split]:
            warnings.append(f"No independent groups in {split}; this split is not ready.")
    for label, counts_by_split in counts.items():
        if not counts_by_split["train"]["positiveGroups"]:
            warnings.append(f"No independent training positives for {label}.")
        if not counts_by_split["train"]["negativeGroups"]:
            warnings.append(f"No independent reviewed training negatives for {label}.")
    report = {
        "schemaVersion": 1, "seed": seed, "vocabularySha256": fingerprint(concepts),
        "datasetSha256": fingerprint(output), "labels": labels,
        "testSources": sorted(test_sources), "records": len(output),
        "exactUniqueImages": len({row["sha256"] for row in output}),
        "independentGroups": len(components), "priorEvaluationGroupsForcedToTrain": contaminated,
        "splitGroups": {split: group_counts[split] for split in SPLITS},
        "splitImages": {split: sum(row["split"] == split for row in output) for split in SPLITS},
        "knownLabelFraction": sum(target != -1 for row in output for target in row["targets"])
                              / (len(output) * len(labels)),
        "perLabel": counts, "warnings": warnings,
        "trainingReady": False, "productionApproved": False,
    }
    return output, report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="Reviewed local JSONL inventory")
    parser.add_argument("--images-root", type=Path, required=True)
    parser.add_argument("--vocabulary", type=Path,
                        default=Path(__file__).with_name("pilot-vocabulary.csv"))
    parser.add_argument("--output", type=Path, required=True, help="New private output directory")
    parser.add_argument("--seed", default="compact-pilot-v1")
    parser.add_argument("--test-source", action="append", default=[])
    args = parser.parse_args()
    if args.output.exists():
        parser.error("--output must not already exist; preserve earlier audits/splits")
    if args.output.resolve().is_relative_to(args.images_root.resolve()):
        parser.error("--output must be outside --images-root")
    concepts, aliases = vocabulary(args.vocabulary)
    records = load_records(args.input, args.images_root, aliases)
    rows, report = prepare(records, concepts, args.seed, set(args.test_source))
    report["inputSha256"] = file_hash(args.input)
    report["vocabularyFileSha256"] = file_hash(args.vocabulary)
    args.output.mkdir(parents=True, exist_ok=False)
    with (args.output / "manifest.jsonl").open("w", encoding="utf-8", newline="\n") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
    (args.output / "audit.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Prepared {len(rows)} local records in {len(set(row['groupId'] for row in rows))} groups.")
    print("Research audit only; review audit.json warnings. No training or release approval.")


if __name__ == "__main__":
    main()
