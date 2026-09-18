"""Compile a complete curated model taxonomy into shared runtime vocabulary assets.

The taxonomy is independent of photo scores. Eligibility is semantic policy, not
measured accuracy. --check verifies that checked-in generated assets reproduce.
"""

import argparse
import copy
import hashlib
import json
from pathlib import Path


def normalize(label):
    return " ".join(label.split(",", 1)[0].replace("_", " ").replace("/", " ").split()).lower()


def json_text(value):
    return json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def assemble(directory):
    inventory = json.loads((directory / "inventory.json").read_text(encoding="utf-8"))
    entries = {}
    for index in range(1, 7):
        assigned = json.loads((directory / f"part-{index}-input.json").read_text(encoding="utf-8"))
        curated = json.loads((directory / f"part-{index}-curated.json").read_text(encoding="utf-8"))
        require(set(curated) == set(assigned), f"Part {index} has missing or unexpected labels")
        require(not entries.keys() & curated.keys(), f"Part {index} has duplicate ownership")
        entries.update(curated)
    require(set(entries) == set(inventory["labels"]), "Incomplete full vocabulary curation")
    return {
        "schemaVersion": 2,
        "name": "full-model-general-photo-taxonomy-v2",
        "source": {
            "model": "RAM++ Swin-Large", "labelCount": len(inventory["ramLabels"]),
            "labelAssetSha256": inventory["ramSha256"],
            "project": "https://github.com/xinyu1205/recognize-anything",
        },
        "ramLabels": inventory["ramLabels"],
        "previousTinyClipLabels": inventory["tinyLabels"],
        "stablePaths": inventory["previousPaths"],
        "entries": dict(sorted(entries.items())),
        "warning": "Semantic curation only, not model accuracy calibration. Sensitive personal inferences and non-visual concepts are not automatic candidates.",
    }


def compile_taxonomy(taxonomy):
    require(taxonomy["schemaVersion"] == 2, "Unsupported taxonomy version")
    entries = taxonomy["entries"]
    required = {normalize(label) for label in taxonomy["ramLabels"]}
    required.update(taxonomy["previousTinyClipLabels"])
    required.update(taxonomy["stablePaths"])
    required.update(taxonomy.get("additionalLabels", []))
    require(set(entries) == required, "Taxonomy must cover the complete source vocabularies and skeleton")
    for label, entry in entries.items():
        require(label and label == normalize(label) and "\\" not in label and not any(ord(c) < 32 for c in label),
                f"Invalid normalized label: {label!r}")
        require(isinstance(entry.get("automatic"), bool), f"Missing eligibility for {label}")
        require(entry["automatic"] or isinstance(entry.get("reason"), str) and entry["reason"].strip(),
                f"Excluded label needs an explanation: {label}")
        require(("parent" in entry) != ("canonical" in entry), f"Expected one relationship for {label}")
        target = entry.get("parent") if "parent" in entry else entry["canonical"]
        require(target is None and "parent" in entry or isinstance(target, str) and target in entries,
                f"Unknown target for {label}: {target}")
        require(target != label, f"Self-reference for {label}")

    paths, active = {}, []

    def resolve(label):
        if label in paths:
            return paths[label]
        require(label not in active, "Taxonomy cycle: " + " -> ".join(active + [label]))
        active.append(label)
        entry = entries[label]
        if "canonical" in entry:
            path = resolve(entry["canonical"])
        elif entry["parent"] is None:
            path = label
        else:
            path = resolve(entry["parent"]) + "/" + label
        active.pop()
        parts = path.split("/")
        require(len(parts) == len(set(parts)) and parts[0] != "auto", f"Invalid hierarchy path: {path}")
        paths[label] = path
        return path

    for label in entries:
        resolve(label)
    for label, expected in taxonomy["stablePaths"].items():
        require(paths[label] == expected, f"Stable hierarchy changed for {label}: {paths[label]} != {expected}")
    for path in paths.values():
        parts = path.split("/")
        for index, label in enumerate(parts):
            require(paths[label] == "/".join(parts[:index + 1]), f"Inconsistent ancestor: {path}")

    canonical = {label: path.rsplit("/", 1)[-1] for label, path in paths.items()}
    eligible = set()
    excluded = {}
    for label, entry in entries.items():
        target = canonical[label]
        if entry["automatic"] and entries[target]["automatic"]:
            eligible.add(target)
        else:
            excluded[label] = entry.get("reason") or (
                f"Canonical label '{target}' is excluded: " + entries[target]["reason"])
    require(eligible, "No general-purpose candidates were selected")
    labels = sorted(eligible)
    fingerprint = hashlib.sha256(json.dumps(
        labels, ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest()
    ram_labels = [normalize(label) for label in taxonomy["ramLabels"]]
    ram_mappings = {
        label: canonical[label] for label in ram_labels
        if label not in excluded and canonical[label] in eligible
    }
    vocabulary = {
        "schemaVersion": 1, "name": taxonomy.get("vocabularyName", "general-home-photo-v1"),
        "description": "Broad general-purpose vocabulary curated from the complete RAM++ model vocabulary, not from sample-photo predictions.",
        "productionApproved": False,
        "prompts": {"siglip2": "This is a photo of {label}.", "tinyclip": "a photo of {label}"},
        "labels": labels,
        "taxonomyVersion": taxonomy.get("hierarchyVersion", "hierarchy-v2"),
        "excludedLabels": dict(sorted(excluded.items())),
        "warning": "Semantic eligibility is not evidence of model accuracy. Review new predictions and recalibrate for this candidate vocabulary.",
    }
    profile = {
        "schemaVersion": 1, "taxonomyVersion": taxonomy.get("hierarchyVersion", "hierarchy-v2"),
        "ramLabelAssetSha256": taxonomy["source"]["labelAssetSha256"],
        "ramLabels": ram_labels, "vocabularySha256": fingerprint,
        "labels": labels, "ramLabelMappings": dict(sorted(ram_mappings.items())),
        "excludedLabels": dict(sorted(excluded.items())),
    }
    mapping_file = {str(index): ram_mappings.get(label, "") for index, label in enumerate(ram_labels)}
    return dict(sorted(paths.items())), vocabulary, profile, mapping_file


def apply_overrides(taxonomy, overrides):
    revised = copy.deepcopy(taxonomy)
    revised["hierarchyVersion"] = overrides["hierarchyVersion"]
    revised["vocabularyName"] = overrides["vocabularyName"]
    require(revised["hierarchyVersion"] != taxonomy.get("hierarchyVersion", "hierarchy-v2"),
            "Vocabulary overrides require a new hierarchy version")
    entries = revised["entries"]
    for label, reason in overrides.get("exclude", {}).items():
        require(label in entries and isinstance(reason, str) and reason.strip(),
                f"Invalid exclusion: {label}")
        entries[label].update(automatic=False, reason=reason)
    for old, new in overrides.get("rename", {}).items():
        require(old in entries and new not in entries and new == normalize(new),
                f"Invalid or conflicting rename: {old} -> {new}")
        require(old not in revised["stablePaths"], f"Rename would change a stable ancestor: {old}")
        require("parent" in entries[old], f"Rename expects a canonical source label: {old}")
        entries[new] = dict(entries[old])
        entries[old] = {"canonical": new, "automatic": entries[new]["automatic"]}
        if not entries[old]["automatic"]:
            entries[old]["reason"] = entries[new]["reason"]
        revised.setdefault("additionalLabels", []).append(new)
    return revised


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--taxonomy", required=True, type=Path)
    parser.add_argument("--assemble", type=Path, help="Curated part directory; create the portable taxonomy input")
    parser.add_argument("--overrides", type=Path, help="Versioned exclusions and explicit canonical-label corrections")
    parser.add_argument("--hierarchy", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--profile", required=True, type=Path)
    parser.add_argument("--ram-mapping", type=Path, help="Optional indexed mapping for --labels mapped benchmark runs")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    try:
        require(not args.check or args.assemble is None, "--check cannot assemble new taxonomy inputs")
        taxonomy = assemble(args.assemble) if args.assemble else json.loads(args.taxonomy.read_text(encoding="utf-8"))
        if args.overrides:
            require(not args.assemble, "Apply overrides to an existing portable taxonomy")
            taxonomy = apply_overrides(taxonomy, json.loads(args.overrides.read_text(encoding="utf-8")))
        hierarchy, vocabulary, profile, mapping = compile_taxonomy(taxonomy)
        outputs = {args.hierarchy: hierarchy, args.vocabulary: vocabulary, args.profile: profile}
        if args.assemble:
            outputs[args.taxonomy] = taxonomy
        if args.ram_mapping:
            outputs[args.ram_mapping] = mapping
        for path, content in outputs.items():
            text = json_text(content)
            if args.check:
                require(path.read_text(encoding="utf-8") == text, f"Generated artifact differs: {path}")
            else:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(text, encoding="utf-8", newline="\n")
    except (ValueError, KeyError, TypeError, OSError, RecursionError) as error:
        parser.exit(2, f"Taxonomy compilation failed: {error}\n")
    print(f"{len(profile['ramLabels'])} RAM++ outputs; {len(hierarchy)} hierarchy entries; "
          f"{len(vocabulary['labels'])} shared candidates; {len(profile['ramLabelMappings'])} eligible RAM++ labels")
    print(f"Vocabulary SHA256: {profile['vocabularySha256']}")


if __name__ == "__main__":
    main()
