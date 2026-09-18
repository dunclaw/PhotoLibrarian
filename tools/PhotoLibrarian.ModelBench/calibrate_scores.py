"""Offline, experimental calibration of explicitly reviewed displayed candidates.

python calibrate_scores.py --results results.json --scores human-scores.csv --output new-folder
Optional --groups CSV must map every results image exactly once using Image,Group.
No images, model assets, or network resources are opened. Python standard library only.

Scores CSV requires Image,Model,QualityScore0To5,CorrectPredictionRanks,Notes.
Quality is an integer 0..5 or blank; checked ranks are semicolon-separated.
Legacy CSV: blank quality excludes the row; checked-unrated rows are rejected.
Optional ReviewedPredictionRanks explicitly lists reviewed tags, independently of
quality. Correct ranks must be a subset; all other displayed ranks stay pending.
load_inputs(results_path, scores_path=None) validates prediction-only input.
JSON thresholds retain exact source numbers; null (blank in CSV) means abstain.
calibration.json separates outOfFold evaluation, experimentalFullDataFit parameters,
and resubstitutionOnly evaluation. Per-label/unseen fallback uses that fit's global
threshold, never a threshold learned from held-out rows.
annotation-conflicts.csv flags conflicting cross-model marks for reconciliation;
model-specific judgments are preserved, not merged or overridden.
"""

import argparse
import csv
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation
from fractions import Fraction
import hashlib
import html
import io
import json
from pathlib import Path
import re
import sys

TARGETS = (Decimal("0.60"), Decimal("0.75"), Decimal("0.90"))
FOLDS, MIN_ACCEPTED, MIN_APPEARANCES, MIN_POSITIVES = 5, 5, 10, 5
KINDS = {"confidence", "sigmoid", "cosine", "binary"}
TASKS = {"MultiLabelTagging", "ZeroShotTagging", "Classification", "ImageClassification"}
REVIEW_FIELDS = ["Image", "Model", "QualityScore0To5", "CorrectPredictionRanks", "Notes"]
REVIEWED_RANKS_FIELD = "ReviewedPredictionRanks"
PARTIAL_REVIEW_FIELDS = REVIEW_FIELDS + [REVIEWED_RANKS_FIELD]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fields(obj, names, context):
    require(isinstance(obj, dict), f"{context}: expected an object")
    require(set(names) <= obj.keys(), f"{context}: missing required fields {names}")


def text(value, context):
    require(isinstance(value, str) and bool(value.strip()), f"{context}: expected nonempty text")
    return value


def number(value, kind, context):
    require(isinstance(value, (int, float, Decimal)) and not isinstance(value, bool),
            f"{context}: expected a numeric score")
    try:
        result = Decimal(str(value))
    except InvalidOperation as error:
        raise ValueError(f"{context}: malformed score") from error
    low = -1 if kind == "cosine" else 0
    require(result.is_finite() and low <= result <= 1,
            f"{context}: score must be finite and in [{low}, 1]")
    return result


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"JSON: duplicate key {key!r}")
        result[key] = value
    return result


def invalid_constant(value):
    raise ValueError(f"JSON: nonfinite number {value}")


def read_csv(raw, expected, context, optional=()):
    reader = csv.DictReader(io.StringIO(raw.decode("utf-8-sig"), newline=""), strict=True)
    require(reader.fieldnames is not None and len(reader.fieldnames) == len(set(reader.fieldnames))
            and set(expected) <= set(reader.fieldnames) <= set(expected) | set(optional),
            f"{context}: fields must be exactly {','.join(expected)}; optional {','.join(optional)} (no duplicates)")
    rows = list(reader)
    for row in rows:
        require(None not in row and all(value is not None for value in row.values()),
                f"{context}: malformed or missing row fields")
    return rows


@dataclass(frozen=True)
class Candidate:
    image: str
    model: str
    rank: int
    label: str
    score: Decimal
    checked: object = None


@dataclass
class Review:
    image: str
    model: str
    quality: object
    status: str
    candidates: list
    notes: str = ""


def reviewed_candidates(review):
    return [p for p in review.candidates if p.checked is not None]


def tag_review_status(review):
    count = len(reviewed_candidates(review))
    if 0 < count < len(review.candidates):
        return "partial"
    return "reviewed" if count or (not review.candidates and review.quality is not None) else "pending"


def is_reviewed_row(review):
    return tag_review_status(review) != "pending"


def review_to_csv_row(review):
    return {"Image": review.image, "Model": review.model,
            "QualityScore0To5": review.quality if review.quality is not None else "",
            "CorrectPredictionRanks": ";".join(str(p.rank) for p in sorted(review.candidates, key=lambda p: p.rank) if p.checked),
            "Notes": review.notes,
            REVIEWED_RANKS_FIELD: ";".join(str(p.rank) for p in sorted(reviewed_candidates(review), key=lambda p: p.rank))}


def parse_ranks(value, known_ranks, context):
    value = value.strip()
    require(not value or re.fullmatch(r"[1-9]\d*(;[1-9]\d*)*", value),
            f"{context}: malformed ranks; use semicolon-separated positive integers")
    ranks = list(map(int, value.split(";"))) if value else []
    require(len(ranks) == len(set(ranks)), f"{context}: duplicate ranks")
    require(set(ranks) <= known_ranks, f"{context}: unknown rank")
    return set(ranks)


def digest_fold(group):
    payload = ("PhotoLibrarian.ModelBench.calibration.v1\0" + group).encode("utf-8")
    return int.from_bytes(hashlib.sha256(payload).digest(), "big") % FOLDS


def load_inputs(results_path, scores_path=None, groups_path=None):
    provenance = {}

    def read(path, role):
        path = Path(path)
        raw = path.read_bytes()
        provenance[role] = {"fileName": path.name, "sha256": hashlib.sha256(raw).hexdigest()}
        return raw

    run = json.loads(read(results_path, "results").decode("utf-8-sig"),
                     parse_float=Decimal, parse_constant=invalid_constant,
                     object_pairs_hook=unique_object)
    fields(run, ["Models", "Results"], "BenchmarkRun")
    require(isinstance(run["Models"], list) and bool(run["Models"]), "Models: expected nonempty array")
    require(isinstance(run["Results"], list) and bool(run["Results"]), "Results: expected nonempty array")
    models = {}
    for entry in run["Models"]:
        fields(entry, ["ModelId", "Provider", "ScoreKind", "Threshold"], "Model")
        model = text(entry["ModelId"], "ModelId")
        require(model not in models, f"duplicate model {model!r}")
        require(entry.get("Error") in (None, ""), f"{model}: failed model: {entry.get('Error')}")
        require(type(entry.get("FailedImages", 0)) is int and entry.get("FailedImages", 0) == 0,
                f"{model}: FailedImages must be zero; failed images are unsupported")
        provider = text(entry["Provider"], f"{model} Provider")
        require(provider.casefold() == "cpu", f"{model}: only Cpu runs are supported")
        kind = text(entry["ScoreKind"], f"{model} ScoreKind").lower()
        require(kind in KINDS, f"{model}: unsupported ScoreKind {kind}")
        metadata = {"modelId": model, "provider": provider, "scoreKind": kind,
                    "baselineThreshold": number(entry["Threshold"], kind, f"{model} Threshold")}
        if "Task" in entry:
            require(isinstance(entry["Task"], str) and entry["Task"] in TASKS,
                    f"{model}: unsupported metadata task")
            metadata["task"] = entry["Task"]
        for source, dest in (("ModelName", "modelName"), ("Precision", "precision"),
                             ("ModelSha256", "modelSha256"), ("VocabularySha256", "vocabularySha256")):
            value = entry.get(source)
            if value is not None:
                text(value, f"{model} {source}")
                if source.endswith("Sha256"):
                    require(re.fullmatch(r"[0-9a-fA-F]{64}", value), f"{model}: malformed {source}")
            metadata[dest] = value
        models[model] = metadata
    results = {}
    for entry in run["Results"]:
        fields(entry, ["RelativeImagePath", "ModelId", "Provider", "Task", "ScoreKind",
                       "Threshold", "Predictions"], "Result")
        image, model = text(entry["RelativeImagePath"], "Image"), text(entry["ModelId"], "ModelId")
        key = image, model
        require(model in models, f"unknown result model {model!r}")
        require(key not in results, f"duplicate result key {key!r}")
        require(entry.get("Error") in (None, ""), f"{key}: failed result: {entry.get('Error')}")
        meta = models[model]
        require(isinstance(entry["Task"], str) and entry["Task"] in TASKS,
                f"{key}: unsupported task (detection is not calibration)")
        require(meta.setdefault("task", entry["Task"]) == entry["Task"], f"{model}: inconsistent tasks")
        require(entry["Provider"] == meta["provider"] and entry["ScoreKind"] == meta["scoreKind"],
                f"{key}: inconsistent provider/score kind")
        require(number(entry["Threshold"], meta["scoreKind"], f"{key} Threshold")
                == meta["baselineThreshold"], f"{model}: inconsistent baseline thresholds")
        require(isinstance(entry["Predictions"], list), f"{key}: Predictions must be an array")
        ranks, labels, candidates = set(), set(), []
        for prediction in entry["Predictions"]:
            fields(prediction, ["Rank", "Label", "Confidence", "AboveThreshold"], f"{key} Prediction")
            rank, label = prediction["Rank"], text(prediction["Label"], f"{key} Label")
            require(type(rank) is int and rank > 0 and rank not in ranks, f"{key}: invalid/duplicate rank")
            require(label not in labels, f"{key}: duplicate label {label!r}; detection is unsupported")
            require(all(prediction.get(axis) is None for axis in ("X", "Y", "Width", "Height")),
                    f"{key}: detection coordinates are unsupported")
            score = number(prediction["Confidence"], meta["scoreKind"], f"{key} rank {rank}")
            if meta["scoreKind"] == "binary":
                require(score == 1, f"{key}: binary candidates must be detected tags, never zero-score padding")
            require(type(prediction["AboveThreshold"]) is bool
                    and prediction["AboveThreshold"] == (score >= meta["baselineThreshold"]),
                    f"{key}: AboveThreshold must match inclusive baseline threshold")
            candidates.append(Candidate(image, model, rank, label, score))
            ranks.add(rank)
            labels.add(label)
        results[key] = sorted(candidates, key=lambda p: p.rank)
    require(set(models) == {model for _, model in results}, "metadata model without results")
    for entry in run["Models"]:
        if "SuccessfulImages" in entry:
            require(type(entry["SuccessfulImages"]) is int and entry["SuccessfulImages"]
                    == sum(model == entry["ModelId"] for _, model in results),
                    f"{entry['ModelId']}: SuccessfulImages does not match results")
    scores = {}
    score_rows = (read_csv(read(scores_path, "scores"), REVIEW_FIELDS, "scores", [REVIEWED_RANKS_FIELD])
                  if scores_path is not None else [])
    for row in score_rows:
        key = text(row["Image"], "scores Image"), text(row["Model"], "scores Model")
        require(key in results, f"unknown review key {key!r}")
        require(key not in scores, f"duplicate review key {key!r}")
        quality, ranks_text = row["QualityScore0To5"].strip(), row["CorrectPredictionRanks"].strip()
        require(not quality or re.fullmatch(r"[0-5]", quality), f"{key}: quality must be an integer 0..5 or blank")
        explicit = REVIEWED_RANKS_FIELD in row
        require(explicit or not ranks_text or quality, f"{key}: checked ranks on an unrated row are ambiguous")
        known_ranks = {p.rank for p in results[key]}
        ranks = parse_ranks(ranks_text, known_ranks, f"{key} checked")
        reviewed_ranks = (parse_ranks(row[REVIEWED_RANKS_FIELD], known_ranks, f"{key} reviewed") if explicit
                          else known_ranks if quality else set())
        require(ranks <= reviewed_ranks, f"{key}: correct ranks must be a subset of reviewed ranks")
        scores[key] = (int(quality) if quality else None, ranks, reviewed_ranks, row["Notes"])
    reviews = []
    for (image, model), candidates in sorted(results.items()):
        quality, ranks, reviewed_ranks, notes = scores.get((image, model), (None, set(), set(), ""))
        status = "missing" if (image, model) not in scores else "unrated" if quality is None else "rated"
        reviewed = [Candidate(image, model, p.rank, p.label, p.score,
                              p.rank in ranks if p.rank in reviewed_ranks else None) for p in candidates]
        reviews.append(Review(image, model, quality, status, reviewed, notes))
    require(scores_path is None or any(is_reviewed_row(r) for r in reviews), "no explicitly reviewed tag rows")
    images = {r.image for r in reviews}
    groups = {image: image for image in images}
    if groups_path is not None:
        groups = {}
        for row in read_csv(read(groups_path, "groups"), ["Image", "Group"], "groups"):
            image, group = text(row["Image"], "groups Image"), text(row["Group"], "groups Group")
            require(image in images and image not in groups, f"unknown/duplicate group image {image!r}")
            groups[image] = group
        require(set(groups) == images, "groups: every results image must have exactly one mapping")
    folds = {image: digest_fold(group) for image, group in groups.items()}
    return {"models": models, "reviews": reviews, "groups": groups, "folds": folds,
            "provenance": provenance, "explicitGroups": groups_path is not None}


def evidence(candidates):
    reviewed = [p for p in candidates if p.checked is not None]
    checked = [p for p in reviewed if p.checked]
    result = {"reviewedAppearances": len(reviewed), "checkedPositives": len(checked),
              "uncheckedCandidates": len(reviewed) - len(checked)}
    for name, values in (("Reviewed", reviewed), ("Checked", checked),
                         ("Unchecked", [p for p in reviewed if not p.checked])):
        scores = [p.score for p in values]
        result["min" + name + "Score"] = min(scores, default=None)
        result["max" + name + "Score"] = max(scores, default=None)
    return result


def threshold_sweep(candidates):
    by_score = {}
    for candidate in candidates:
        if candidate.checked is not None:
            counts = by_score.setdefault(candidate.score, [0, 0])
            counts[0 if candidate.checked else 1] += 1
    checked = sum(counts[0] for counts in by_score.values())
    tp = fp = 0
    sweep = []
    for score, (positives, negatives) in sorted(by_score.items(), reverse=True):
        tp, fp = tp + positives, fp + negatives
        sweep.append({"threshold": score, "tp": tp, "fp": fp, "accepted": tp + fp,
                      "checked": checked, "precision": tp / (tp + fp),
                      "checkedCandidateRetention": tp / checked if checked else None})
    return sweep


def fit_threshold(candidates, target):
    eligible = [point for point in threshold_sweep(candidates)
                if point["accepted"] >= MIN_ACCEPTED and point["tp"] > 0
                and Fraction(point["tp"], point["accepted"]) >= Fraction(target)]
    best = max(eligible, key=lambda p: (p["tp"], Fraction(p["tp"], p["accepted"]),
                                       -p["accepted"]), default=None)
    return {**evidence(candidates), "threshold": best["threshold"] if best else None,
            "status": "fitted" if best else "abstain_no_eligible_threshold"}


def fit_policy(candidates, target):
    candidates = [p for p in candidates if p.checked is not None]
    global_fit = fit_threshold(candidates, target)
    grouped = {}
    for candidate in candidates:
        grouped.setdefault(candidate.label, []).append(candidate)
    labels = {}
    for label, samples in sorted(grouped.items()):
        stats = evidence(samples)
        if stats["reviewedAppearances"] >= MIN_APPEARANCES and stats["checkedPositives"] >= MIN_POSITIVES:
            labels[label] = fit_threshold(samples, target)
        else:
            labels[label] = {**stats, "threshold": global_fit["threshold"],
                             "status": "global_fallback_insufficient_evidence"}
    return {"global": global_fit, "labels": labels,
            "unseenLabelPolicy": "global_fallback", "thresholdComparison": ">="}


def label_choice(policy, label):
    return policy["labels"].get(label, {**evidence([]), "threshold": policy["global"]["threshold"],
                                       "status": "global_fallback_unseen_label"})


def accepts(candidate, threshold):
    return threshold is not None and candidate.score >= threshold


def metrics(reviews, choose_threshold):
    reviews = [r for r in reviews if is_reviewed_row(r)]
    candidates = [p for r in reviews for p in reviewed_candidates(r)]
    accepted = [p for p in candidates if accepts(p, choose_threshold(p))]
    tp, checked = sum(p.checked for p in accepted), sum(p.checked for p in candidates)
    images, covered = len({r.image for r in reviews}), len({p.image for p in accepted})
    return {"tp": tp, "fp": len(accepted) - tp, "accepted": len(accepted), "checked": checked,
            "reviewedCandidates": len(candidates), "precision": tp / len(accepted) if accepted else None,
            "checkedCandidateRetention": tp / checked if checked else None,
            "imagesWithAccepted": covered, "imagesReviewed": images,
            "coverage": covered / images if images else None,
            "acceptedPerImage": len(accepted) / images if images else None}


def annotation_conflicts(data):
    pairs = {}
    for review in data["reviews"]:
        for candidate in review.candidates:
            pairs.setdefault((candidate.image, candidate.label), []).append(candidate)
    shared_displayed = shared_reviewed = conflicts = 0
    rows = []
    for (image, label), candidates in sorted(pairs.items()):
        reviewed = [p for p in candidates if p.checked is not None]
        shared_displayed += len({p.model for p in candidates}) > 1
        shared_reviewed += len({p.model for p in reviewed}) > 1
        if {p.checked for p in reviewed} == {True, False}:
            conflicts += 1
            rows.extend({"image": image, "label": label, "model": p.model, "checked": p.checked}
                        for p in sorted(candidates, key=lambda p: p.model))
    summary = {"sharedDisplayedPairs": shared_displayed, "sharedReviewedPairs": shared_reviewed,
               "conflictingPairs": conflicts, "exportedAnnotations": len(rows),
               "reviewedConflictingAnnotations": sum(row["checked"] is not None for row in rows),
               "matching": "exact image and label; only reviewed tag marks can conflict, independently of quality",
               "policy": "retain model-specific judgments unchanged",
               "artifact": "annotation-conflicts.csv"}
    return summary, rows


def analyze(data):
    require(any(is_reviewed_row(r) for r in data["reviews"]), "no explicitly reviewed tag rows")
    conflicts, _ = annotation_conflicts(data)
    warnings = [
        "Exploratory requested precision targets are not promises or production defaults.",
        "CheckedCandidateRetention is retention of checked displayed candidates, NOT full image-tag recall.",
        "Pending tags and unseen labels supply no negative evidence. Quality ratings and notes do not change explicit tag marks.",
        "Partial tag reviews may be selective; precision and retention describe reviewed candidates only.",
        "Absent positives do not make rare labels unsafe and do not justify deleting general-purpose vocabulary.",
        "Only supplied CPU model versions, vocabulary, and displayed candidate rankings are in scope.",
        "Full-data fit metrics are resubstitution only; use out-of-fold metrics for exploratory comparison.",
        "Thresholds are inclusive; null means abstain, including when global fallback abstains.",
    ]
    if conflicts["conflictingPairs"]:
        warnings.append(f"Cross-model annotation uncertainty: {conflicts['conflictingPairs']} of "
                        f"{conflicts['sharedReviewedPairs']} image/exact-label pairs reviewed by multiple models "
                        "have conflicting checked/unchecked judgments. Model-specific marks are preserved; "
                        "this disagreement can affect threshold calibration. Reconcile annotation-conflicts.csv.")
    warnings.append("Explicit event/near-duplicate groups used; residual event/subject leakage is still possible."
                    if data["explicitGroups"] else
                    "Image-only grouping: remaining event/near-duplicate/subject leakage is possible; use --groups.")
    if len(set(data["folds"].values())) < FOLDS:
        warnings.append("One or more digest-assigned folds are empty; five-fold evidence is limited.")
    report = {"schemaVersion": 1, "productionApproved": False, "profileStatus": "experimental",
              "scope": {"provider": "Cpu", "modelVersions": "supplied inputs only",
                        "candidateUniverse": "displayed descriptive candidates; rankings unchanged"},
              "provenance": data["provenance"], "requestedPrecisionTargets": TARGETS,
              "minimumAccepted": MIN_ACCEPTED, "minimumLabelAppearances": MIN_APPEARANCES,
              "minimumLabelCheckedPositives": MIN_POSITIVES, "warnings": warnings,
              "annotationConflicts": conflicts,
              "metricDefinitions": {
                  "precision": "TP / accepted reviewed candidates; null when none accepted",
                  "checkedCandidateRetention": "TP / checked displayed candidates; NOT full image-tag recall; null when no checked candidates",
                  "coverage": "imagesWithAccepted / imagesReviewed; null when no reviewed images; includes quality-rated empty rows",
                  "acceptedPerImage": "accepted reviewed candidates / imagesReviewed; null when no reviewed images"},
              "validation": {"foldCount": FOLDS, "explicitGroups": data["explicitGroups"],
                             "assignment": "big-endian SHA256(UTF8('PhotoLibrarian.ModelBench.calibration.v1\\0' + Group)) mod 5",
                             "imageFolds": [{"image": image, "group": data["groups"][image], "fold": fold}
                                            for image, fold in sorted(data["folds"].items())]}, "models": {}}
    for model, metadata in sorted(data["models"].items()):
        reviews = [r for r in data["reviews"] if r.model == model]
        rated = [r for r in reviews if r.quality is not None]
        reviewed = [r for r in reviews if is_reviewed_row(r)]
        candidates = [p for r in reviewed for p in reviewed_candidates(r)]
        if len({p.score for p in candidates}) <= 2:
            warnings.append(f"{model}: scores have at most two distinct values; global calibration has little resolution.")
        if metadata["modelSha256"] is None or metadata["vocabularySha256"] is None:
            warnings.append(f"{model}: missing source model/vocabulary hash; exact asset identity cannot be verified.")
        result = {"metadata": metadata,
                  "rankingQuality": {"ratedRows": len(rated),
                                     "missingReviewRows": sum(r.status == "missing" for r in reviews),
                                     "unratedRows": sum(r.status == "unrated" for r in reviews),
                                     "reviewedRows": len(reviewed),
                                     "fullyReviewedRows": sum(tag_review_status(r) == "reviewed" for r in reviews),
                                     "partiallyReviewedRows": sum(tag_review_status(r) == "partial" for r in reviews),
                                     "pendingRows": len(reviews) - len(reviewed),
                                     "reviewedCandidates": len(candidates),
                                     "pendingCandidates": sum(len(r.candidates) for r in reviews) - len(candidates),
                                     "averageQualityScore0To5": sum(r.quality for r in rated) / len(rated) if rated else None,
                                     "checkedPositives": sum(p.checked for p in candidates)},
                  "baseline": {"threshold": metadata["baselineThreshold"],
                               "evaluation": metrics(reviews, lambda p: metadata["baselineThreshold"])},
                  "operatingPoints": []}
        for target in TARGETS:
            full = fit_policy(candidates, target)
            fits = []
            for fold in range(FOLDS):
                training = [r for r in reviewed if data["folds"][r.image] != fold]
                fits.append({"heldOutFold": fold, "trainingImages": len(training),
                             "policy": fit_policy([p for r in training for p in r.candidates], target)})
            oof, resubstitution = {}, {}
            for name in ("global", "perLabel"):
                def threshold(candidate, policy_name=name, out_of_fold=True):
                    policy = fits[data["folds"][candidate.image]]["policy"] if out_of_fold else full
                    return (policy["global"] if policy_name == "global"
                            else label_choice(policy, candidate.label))["threshold"]
                oof[name] = metrics(reviews, threshold)
                resubstitution[name] = metrics(reviews, lambda p: threshold(p, out_of_fold=False))
            result["operatingPoints"].append({"requestedPrecision": target, "outOfFold": oof,
                                              "experimentalFullDataFit": full, "foldFits": fits,
                                              "resubstitutionOnly": resubstitution})
        report["models"][model] = result
    return report


def json_text(value):
    """Keep source Decimal thresholds as exact JSON numbers, not rounded floats."""
    if isinstance(value, Decimal):
        require(value.is_finite(), "cannot serialize nonfinite decimal")
        return str(value)
    if isinstance(value, dict):
        return "{" + ",".join(json.dumps(key) + ":" + json_text(item)
                              for key, item in sorted(value.items())) + "}"
    if isinstance(value, (list, tuple)):
        return "[" + ",".join(map(json_text, value)) + "]"
    return json.dumps(value, ensure_ascii=False, allow_nan=False)


def csv_rows(data, report):
    sweeps, labels, predictions = [], [], []
    for model, result in report["models"].items():
        reviews = [r for r in data["reviews"] if r.model == model]
        all_labels = sorted({p.label for r in reviews for p in r.candidates})
        for fold in [None, *range(FOLDS)]:
            training = [p for r in reviews if fold is None or data["folds"][r.image] != fold
                        for p in reviewed_candidates(r)]
            scope = {"Model": model, "FitScope": "full_data_resubstitution" if fold is None else "fold_training_resubstitution",
                     "HeldOutFold": fold}
            for point in threshold_sweep(training):
                eligibility = {"eligibleAt" + str(t): point["accepted"] >= MIN_ACCEPTED and point["tp"] > 0
                               and Fraction(point["tp"], point["accepted"]) >= Fraction(t) for t in TARGETS}
                sweeps.append({**scope, **point, **eligibility})
            for operating in result["operatingPoints"]:
                policy = (operating["experimentalFullDataFit"] if fold is None
                          else operating["foldFits"][fold]["policy"])
                for label in all_labels:
                    labels.append({**scope, "Label": label, "RequestedPrecision": operating["requestedPrecision"],
                                   **label_choice(policy, label)})
        for review in reviews:
            fold = data["folds"][review.image]
            for p in review.candidates:
                for operating in result["operatingPoints"]:
                    policy = operating["foldFits"][fold]["policy"]
                    global_threshold = policy["global"]["threshold"]
                    label = label_choice(policy, p.label)
                    predictions.append({"Image": p.image, "Model": model, "Rank": p.rank, "Label": p.label,
                                        "Score": p.score, "ReviewStatus": review.status, "Checked": p.checked,
                                        "TagReviewStatus": tag_review_status(review),
                                        "CandidateReviewStatus": "pending" if p.checked is None else "reviewed",
                                        "QualityScore0To5": review.quality, "Group": data["groups"][p.image],
                                        "Fold": fold, "BaselineAccepted": accepts(p, result["baseline"]["threshold"]) if p.checked is not None else None,
                                        "RequestedPrecision": operating["requestedPrecision"],
                                        "GlobalThreshold": global_threshold, "GlobalStatus": policy["global"]["status"],
                                        "GlobalOOFAccepted": accepts(p, global_threshold) if p.checked is not None else None,
                                        "LabelThreshold": label["threshold"], "LabelStatus": label["status"],
                                        "PerLabelOOFAccepted": accepts(p, label["threshold"]) if p.checked is not None else None})
    return sweeps, labels, predictions


def render_html(report):
    escape = lambda value: html.escape(str(value), quote=True)

    def display(value):
        return "n/a" if value is None else f"{value:.3f}"

    rows, ranking = [], []
    for model, result in report["models"].items():
        quality = result["rankingQuality"]
        ranking.append(f"<li>{escape(model)}: {quality['ratedRows']} rated images; average ranking quality "
                       f"{display(quality['averageQualityScore0To5'])}/5; {quality['checkedPositives']} checked positives; "
                       f"{quality['reviewedRows']} tag-reviewed rows ({quality['partiallyReviewedRows']} partial); "
                       f"{quality['pendingCandidates']} pending candidates.</li>")
        evaluations = [(f"baseline ≥ {result['baseline']['threshold']} (fixed)", "", result["baseline"]["evaluation"])]
        for operating in result["operatingPoints"]:
            evaluations.extend((name + " (OOF)", operating["requestedPrecision"], value)
                               for name, value in operating["outOfFold"].items())
        for name, target, value in evaluations:
            cells = [model, name, target, value["tp"], value["fp"], value["accepted"], value["checked"],
                     display(value["precision"]), display(value["checkedCandidateRetention"]),
                     f"{value['imagesWithAccepted']}/{value['imagesReviewed']}", display(value["acceptedPerImage"])]
            rows.append("<tr>" + "".join("<td>" + escape(cell) + "</td>" for cell in cells) + "</tr>")
    return ("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">"
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">"
            "<title>Experimental candidate calibration</title><style>body{font:15px system-ui;max-width:1200px;"
            "margin:2em auto;padding:1em}table{border-collapse:collapse}th,td{padding:.45em;border:1px solid #999;"
            "text-align:left}th{background:#eee}code{overflow-wrap:anywhere}</style><body>"
            "<h1>Experimental candidate calibration — not production approved</h1>"
            "<p>No application defaults changed. Local, self-contained report: no images, scripts, uploads, or remote assets.</p>"
            "<ul>" + "".join("<li>" + escape(w) + "</li>" for w in report["warnings"]) + "</ul>"
            "<h2>Raw ranking quality</h2><ul>" + "".join(ranking) + "</ul>"
            "<h2>Baseline and held-out policy comparison</h2><p>Precision and checked-candidate retention are fractions. "
            "Coverage is images with accepted reviewed candidates / reviewed images. Only reviewed tags enter candidate metrics, "
            "independently of quality ratings; quality-rated empty rows remain in image coverage. Quality averages use only "
            "manually supplied values. Missing ratings are never inferred from tags.</p>"
            "<table><thead><tr>" + "".join("<th scope=\"col\">" + h + "</th>" for h in
            ["Model", "Policy", "Requested precision", "TP", "FP", "Accepted", "Checked", "Precision",
             "Checked-candidate retention", "Image coverage", "Accepted/image"]) + "</tr></thead><tbody>"
            + "".join(rows) + "</tbody></table><h2>Artifacts and fitting rules</h2>"
            "<p>calibration.json contains exact experimental full-data parameters, separate resubstitution-only metrics, "
            "fold training fits, and input/model provenance. threshold-sweep.csv covers global full-data and fold-training "
            "score ties. label-evidence.csv includes full-data and fold-training counts, ranges and choices. "
            "predictions-calibrated.csv has one source candidate per target, including pending candidates with "
            "blank Checked and acceptance values. ReviewStatus is missing/unrated/rated quality status; TagReviewStatus "
            "is pending/partial/reviewed row status; CandidateReviewStatus is pending/reviewed. annotation-conflicts.csv "
            "lists all displayed model marks for conflicting image/exact-label pairs; blank checked values are unreviewed, "
            "not negative. Model-specific judgments remain unchanged.</p>"
            "<p>Cross-model annotation consistency: "
            + str(report["annotationConflicts"]["sharedDisplayedPairs"]) + " shared displayed pairs; "
            + str(report["annotationConflicts"]["sharedReviewedPairs"]) + " shared reviewed pairs; "
            + str(report["annotationConflicts"]["conflictingPairs"]) + " conflicting pairs.</p>"
            "<p>Five deterministic group folds; each fit excludes the held-out group fold. Maximize checked-positive "
            "retention subject to requested precision and at least five accepted candidates; ties prefer higher precision, "
            "then fewer accepted. Labels need ten reviewed appearances and five checked positives, otherwise use the "
            "training global fallback. Supported labels with no eligible threshold abstain. Unseen labels use global fallback. "
            "CSV blank thresholds and JSON null mean abstain. Exact tied scores are never split.</p>"
            "<h2>Input SHA256 provenance</h2><pre><code>" + escape(json_text(report["provenance"])) + "</code></pre></body></html>")


def write_outputs(output, data, report):
    output = Path(output)
    require(not output.exists(), f"output already exists: {output}")
    sweeps, labels, predictions = csv_rows(data, report)
    _, conflicts = annotation_conflicts(data)
    contents = {"calibration.json": json_text(report) + "\n", "calibration.html": render_html(report)}
    scope_fields = ["Model", "FitScope", "HeldOutFold"]
    for name, rows, header in (
        ("threshold-sweep.csv", sweeps, scope_fields + ["threshold", "tp", "fp", "accepted", "checked",
         "precision", "checkedCandidateRetention"] + ["eligibleAt" + str(t) for t in TARGETS]),
        ("label-evidence.csv", labels, scope_fields + ["Label", "RequestedPrecision"]
         + list(evidence([])) + ["threshold", "status"]),
        ("predictions-calibrated.csv", predictions, ["Image", "Model", "Rank", "Label", "Score", "ReviewStatus",
         "TagReviewStatus", "CandidateReviewStatus", "Checked", "QualityScore0To5", "Group", "Fold", "BaselineAccepted", "RequestedPrecision",
         "GlobalThreshold", "GlobalStatus", "GlobalOOFAccepted", "LabelThreshold", "LabelStatus", "PerLabelOOFAccepted"]),
        ("annotation-conflicts.csv", conflicts, ["image", "label", "model", "checked"])):
        buffer = io.StringIO(newline="")
        writer = csv.DictWriter(buffer, fieldnames=header)
        writer.writeheader()
        writer.writerows(rows)
        contents[name] = buffer.getvalue()
    output.mkdir(parents=True, exist_ok=False)
    for name, content in contents.items():
        with (output / name).open("x", encoding="utf-8", newline="") as stream:
            stream.write(content)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    for flag in ("results", "scores", "output"):
        parser.add_argument("--" + flag, required=True, type=Path)
    parser.add_argument("--groups", type=Path, help="Complete Image,Group CSV for event/near-duplicate grouping")
    args = parser.parse_args(argv)
    try:
        require(not args.output.exists(), f"output already exists: {args.output}")
        data = load_inputs(args.results, args.scores, args.groups)
        report = analyze(data)
        write_outputs(args.output, data, report)
    except (ValueError, OSError, csv.Error, UnicodeError) as error:
        parser.exit(2, f"calibration error: {error}\n")
    for warning in report["warnings"]:
        print("WARNING: " + warning, file=sys.stderr)
    print(f"Wrote experimental calibration artifacts to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
