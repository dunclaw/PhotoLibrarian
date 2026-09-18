"""Create an incremental, local tag review from saved predictions and human decisions.

Prior result/score pairs are ordered oldest to newest. The latest explicitly
reviewed image/tag wins; disagreement within that review remains pending.
Exact labels are shared across models, never synonyms or bounding-box judgments.
"""

import argparse
import csv
import hashlib
import json
import ntpath
from pathlib import Path
import shutil

import calibrate_scores as cal

EXPORT_FIELDS = cal.PARTIAL_REVIEW_FIELDS
IDENTITY_WARNING = (
    "Older results have no photo content hashes. Reuse assumes a photo at the same "
    "absolute path has not been replaced. Different paths or recorded hashes do not match."
)


def load_run(path, scores=None):
    data = cal.load_inputs(path, scores)
    raw = Path(path).read_bytes()
    cal.require(hashlib.sha256(raw).hexdigest() == data["provenance"]["results"]["sha256"],
                "Predictions changed while loading; retry with a stable saved report.")
    run = json.loads(raw.decode("utf-8-sig"))
    identities, image_paths = {}, {}
    for result in run["Results"]:
        relative = result["RelativeImagePath"]
        image_path = cal.text(result.get("ImagePath"), "ImagePath")
        cal.require(ntpath.isabs(image_path) and bool(ntpath.splitdrive(image_path)[0]),
                    f"{relative}: an absolute Windows ImagePath is required")
        canonical = ntpath.normcase(ntpath.normpath(image_path))
        image_hash = result.get("ImageSha256")
        if image_hash is not None:
            cal.require(isinstance(image_hash, str) and len(image_hash) == 64
                        and all(c in "0123456789abcdefABCDEF" for c in image_hash),
                        f"{relative}: invalid ImageSha256")
            image_hash = image_hash.lower()
        identity = canonical, image_hash
        cal.require(relative not in identities or identities[relative] == identity,
                    f"{relative}: inconsistent photo identity between models")
        identities[relative] = identity
        image_paths[relative] = image_path
    cal.require(len(set(identities.values())) == len(identities),
                "Different image names resolve to the same photo identity")
    return data, identities, image_paths, raw


def thumbnail_path(results_path, image_path):
    stem = ntpath.splitext(ntpath.basename(image_path))[0]
    stem = "".join("_" if c in '<>:"/\\|?*' or ord(c) < 32 else c for c in stem)
    checksum = hashlib.sha256(image_path.encode("utf-8")).hexdigest()[:12].upper()
    return Path(results_path).parent / "thumbnails" / f"{stem}-{checksum}.jpg"


def build_review(results_path, prior_pairs, label_aliases=None):
    cal.require(bool(prior_pairs), "At least one prior results/scores pair is required")
    label_aliases = {} if label_aliases is None else label_aliases
    cal.require(isinstance(label_aliases, dict), "Label aliases must be an object")
    for old, new in label_aliases.items():
        cal.require(isinstance(old, str) and old.strip() == old and bool(old)
                    and isinstance(new, str) and new.strip() == new and bool(new),
                    "Label aliases must contain nonempty, trimmed strings")
        cal.require(new not in label_aliases, "Label aliases cannot contain cycles, self-maps or chains")
    target, identities, image_paths, raw = load_run(results_path)
    history, known = [], {}
    for prior_results, prior_scores in prior_pairs:
        previous, previous_ids, _, _ = load_run(prior_results, prior_scores)
        history.append(previous["provenance"])
        layer = {}
        for review in previous["reviews"]:
            for candidate in review.candidates:
                if candidate.checked is not None:
                    key = previous_ids[review.image], label_aliases.get(candidate.label, candidate.label)
                    layer.setdefault(key, []).append({
                        "model": review.model, "correct": candidate.checked,
                        "source": Path(prior_scores).name,
                    })
        known.update(layer)

    photos, items, rows = [], [], []
    photo_lookup, item_lookup = {}, {}
    for review in target["reviews"]:
        if review.image not in photo_lookup:
            photo_id = len(photos)
            photo_lookup[review.image] = photo_id
            photos.append({"id": photo_id, "image": review.image,
                           "thumbnail": f"thumbnails/{photo_id}.jpg", "items": []})
        photo = photos[photo_lookup[review.image]]
        row = {"image": review.image, "model": review.model, "predictions": []}
        for candidate in review.candidates:
            key = identities[review.image], candidate.label
            if key not in item_lookup:
                evidence = known.get(key, [])
                votes = {entry["correct"] for entry in evidence}
                decision = ("correct" if True in votes else "incorrect") if len(votes) == 1 else None
                item_id = len(items)
                item_lookup[key] = item_id
                photo["items"].append(item_id)
                items.append({
                    "id": item_id, "photo": photo["id"], "label": candidate.label,
                    "initialDecision": decision,
                    "reason": "reused" if decision is not None else "conflict" if votes else "new",
                    "evidence": evidence, "occurrences": [],
                })
            item_id = item_lookup[key]
            items[item_id]["occurrences"].append({
                "model": review.model, "rank": candidate.rank, "score": candidate.score,
                "scoreKind": target["models"][review.model]["scoreKind"],
            })
            row["predictions"].append({"rank": candidate.rank, "item": item_id})
        rows.append(row)

    source = {"target": target["provenance"]["results"], "prior": history}
    if label_aliases:
        source["labelAliases"] = label_aliases
    fingerprint = hashlib.sha256(cal.json_text({"version": 1, **source}).encode("utf-8")).hexdigest()
    data = {
        "schemaVersion": 1, "fingerprint": fingerprint, "identityWarning": IDENTITY_WARNING,
        "provenance": source, "photos": photos, "items": items, "rows": rows,
        "exportFields": EXPORT_FIELDS,
    }
    total = sum(len(row["predictions"]) for row in rows)
    pending = [item for item in items if item["initialDecision"] is None]
    reused_occurrences = sum(len(item["occurrences"]) for item in items if item["initialDecision"] is not None)
    data["summary"] = {
        "photos": len(photos), "modelRows": len(rows), "modelPredictions": total,
        "uniquePhotoTags": len(items), "reusedUniqueDecisions": len(items) - len(pending),
        "reusedModelPredictions": reused_occurrences,
        "pendingUniqueTags": len(pending),
        "newUniqueTags": sum(item["reason"] == "new" for item in pending),
        "conflictingUniqueTags": sum(item["reason"] == "conflict" for item in pending),
        "photosNeedingReview": len({item["photo"] for item in pending}),
        "overallQualityRatingsCopied": 0,
    }
    thumbnails = {
        photo["thumbnail"]: thumbnail_path(results_path, image_paths[photo["image"]])
        for photo in photos
    }
    for thumbnail in thumbnails.values():
        cal.require(thumbnail.is_file(), f"Missing cached thumbnail: {thumbnail}")
    return data, thumbnails, raw


def initial_score_rows(data):
    for row in data["rows"]:
        correct, reviewed = [], []
        for prediction in row["predictions"]:
            decision = data["items"][prediction["item"]]["initialDecision"]
            if decision is not None:
                reviewed.append(str(prediction["rank"]))
                if decision == "correct":
                    correct.append(str(prediction["rank"]))
        yield [row["image"], row["model"], "", ";".join(correct), "", ";".join(reviewed)]


def write_report(output, data, thumbnails, results_bytes):
    output = Path(output)
    cal.require(not output.exists(), f"Output already exists: {output}")
    directory = Path(__file__).parent
    template = (directory / "review_changes.html").read_text(encoding="utf-8")
    cal.require(template.count("@@REVIEW_DATA@@") == 1, "Invalid incremental review template")
    # The payload is inert JSON, but a label must not terminate its script element.
    payload = cal.json_text(data).replace("&", "\\u0026").replace("<", "\\u003c").replace(">", "\\u003e")
    payload = payload.replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
    page = template.replace("@@REVIEW_DATA@@", payload)
    script = (directory / "review_changes.js").read_text(encoding="utf-8")
    output.mkdir(parents=True, exist_ok=False)
    (output / "thumbnails").mkdir()
    (output / "report.html").write_text(page, encoding="utf-8")
    (output / "review.js").write_text(script, encoding="utf-8")
    (output / "results.json").write_bytes(results_bytes)
    (output / "review-items.json").write_text(cal.json_text(data) + "\n", encoding="utf-8")
    (output / "review-summary.json").write_text(cal.json_text(data["summary"]) + "\n", encoding="utf-8")
    with (output / "human-scores.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(EXPORT_FIELDS)
        writer.writerows(initial_score_rows(data))
    for name, source in thumbnails.items():
        shutil.copyfile(source, output / name)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", required=True, type=Path, help="Target results.json with cached thumbnails")
    parser.add_argument("--prior-results", action="append", required=True, type=Path)
    parser.add_argument("--prior-scores", action="append", required=True, type=Path)
    parser.add_argument("--label-aliases", type=Path,
                        help="Explicit spelling-correction JSON map, or a vocabulary override file with reviewAliases")
    parser.add_argument("--output", required=True, type=Path, help="New report directory; existing reports are preserved")
    args = parser.parse_args(argv)
    try:
        cal.require(len(args.prior_results) == len(args.prior_scores),
                    "Pair each --prior-results with one --prior-scores, oldest to newest")
        cal.require(not args.output.exists(), f"Output already exists: {args.output}")
        aliases = None
        if args.label_aliases:
            aliases = json.loads(args.label_aliases.read_text(encoding="utf-8-sig"))
            if isinstance(aliases, dict) and "reviewAliases" in aliases:
                aliases = aliases["reviewAliases"]
        data, thumbnails, raw = build_review(args.results, list(zip(args.prior_results, args.prior_scores)), aliases)
        write_report(args.output, data, thumbnails, raw)
    except (ValueError, OSError, csv.Error, UnicodeError) as error:
        parser.exit(2, f"Incremental review error: {error}\n")
    print(json.dumps(data["summary"], indent=2))
    print("WARNING: " + IDENTITY_WARNING)
    print(f"Review: {args.output / 'report.html'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
