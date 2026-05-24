#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
from typing import Any

from PIL import Image


TASKS = [
    "curb_ramp_present",
    "curb_ramp_absent",
    "obstacle_present",
    "surface_problem_present",
    "crosswalk_present",
]

SKIP_DECISIONS = {"", "skip", "ignore", "reject", "unusable"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Merge reviewed accessibility vision labels into a training dataset copy.")
    parser.add_argument("--dataset-root", type=Path, required=True)
    parser.add_argument("--reviews", type=Path, required=True, help="CSV produced by build_accessibility_vision_review_queue.py after human review.")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--target-split", default="train")
    parser.add_argument("--sample-weight", type=float, default=3.0)
    parser.add_argument("--forbidden-source-splits", default="test,holdout")
    parser.add_argument("--allow-forbidden-source-splits", action="store_true")
    parser.add_argument("--jpeg-quality", type=int, default=92)
    return parser.parse_args()


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows = []
    if not path.exists():
        return rows
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if line.strip():
                rows.append(json.loads(line))
    return rows


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")


def absolute_image_path(dataset_root: Path, image_value: str) -> str:
    path = Path(image_value)
    if path.is_absolute():
        return str(path)
    return str((dataset_root / path).resolve())


def load_reviews(path: Path) -> list[dict[str, str]]:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return list(csv.DictReader(handle))


def source_image_path(review: dict[str, str], reviews_path: Path) -> Path:
    for key in ("review_image", "image"):
        value = (review.get(key) or "").strip()
        if not value:
            continue
        path = Path(value)
        if path.is_absolute():
            return path
        if key == "review_image":
            return reviews_path.parent / path
        return path
    raise ValueError(f"Review row has no image path: {review}")


def reviewed_target(review: dict[str, str]) -> int | None:
    decision = (review.get("review_decision") or "").strip().lower()
    if decision in SKIP_DECISIONS:
        return None
    value = (review.get("reviewed_target") or "").strip()
    if value == "":
        return None
    if value not in {"0", "1"}:
        raise ValueError(f"reviewed_target must be 0 or 1 for {review.get('review_id')}: {value}")
    return int(value)


def normalize_forbidden_splits(value: str) -> set[str]:
    return {item.strip().lower() for item in value.split(",") if item.strip()}


def copy_review_image(source_path: Path, output_dir: Path, target_split: str, task: str, index: int, quality: int) -> str:
    relative_path = Path("images") / target_split / task / f"human_review_{index:07d}.jpg"
    target_path = output_dir / relative_path
    target_path.parent.mkdir(parents=True, exist_ok=True)
    image = Image.open(source_path).convert("RGB")
    image.save(target_path, format="JPEG", quality=quality, optimize=True)
    return str(relative_path)


def base_rows_for_output(dataset_root: Path, split: str) -> list[dict[str, Any]]:
    rows = read_jsonl(dataset_root / f"{split}.jsonl")
    for row in rows:
        row["image"] = absolute_image_path(dataset_root, row["image"])
    return rows


def merged_review_rows(args: argparse.Namespace, forbidden_splits: set[str]) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    rows = []
    skipped = 0
    rejected_forbidden = []
    for review in load_reviews(args.reviews):
        target = reviewed_target(review)
        if target is None:
            skipped += 1
            continue

        source_split = (review.get("source_split") or "").strip().lower()
        if source_split in forbidden_splits and not args.allow_forbidden_source_splits:
            rejected_forbidden.append(review.get("review_id") or "")
            continue

        task = (review.get("task") or "").strip()
        if task not in TASKS:
            raise ValueError(f"Unknown task for {review.get('review_id')}: {task}")

        image_path = source_image_path(review, args.reviews)
        if not image_path.exists():
            raise FileNotFoundError(f"Review image not found for {review.get('review_id')}: {image_path}")

        relative_image = copy_review_image(image_path, args.output_dir, args.target_split, task, len(rows), args.jpeg_quality)
        rows.append(
            {
                "image": relative_image,
                "task_id": TASKS.index(task),
                "task": task,
                "target": target,
                "source_dataset": "accesscity-human-review",
                "source_kind": "human_review",
                "split": args.target_split,
                "sample_weight": args.sample_weight,
                "metadata": {
                    "review_id": review.get("review_id"),
                    "review_decision": review.get("review_decision"),
                    "reviewer": review.get("reviewer"),
                    "notes": review.get("notes"),
                    "original_target": review.get("original_target"),
                    "model_score": review.get("model_score"),
                    "model_threshold": review.get("model_threshold"),
                    "model_predicted": review.get("model_predicted"),
                    "source_split": review.get("source_split"),
                    "source_kind": review.get("source_kind"),
                    "source_dataset": review.get("source_dataset"),
                },
            }
        )

    summary = {
        "review_rows_added": len(rows),
        "review_rows_skipped": skipped,
        "review_rows_rejected_forbidden_split": len(rejected_forbidden),
        "forbidden_review_ids": rejected_forbidden[:50],
    }
    if rejected_forbidden:
        raise ValueError(
            f"Rejected {len(rejected_forbidden)} reviewed rows from forbidden source splits. "
            "Use --allow-forbidden-source-splits only for throwaway experiments, never final model validation."
        )
    return rows, summary


def summarize(rows_by_split: dict[str, list[dict[str, Any]]]) -> dict[str, Any]:
    summary: dict[str, Any] = {}
    for split, rows in rows_by_split.items():
        split_summary: dict[str, Any] = {"rows": len(rows), "tasks": {}}
        for task in TASKS:
            task_rows = [row for row in rows if row.get("task") == task]
            positives = sum(1 for row in task_rows if int(row.get("target", 0)) == 1)
            split_summary["tasks"][task] = {
                "rows": len(task_rows),
                "positive": positives,
                "negative": len(task_rows) - positives,
                "reviewed": sum(1 for row in task_rows if row.get("source_kind") == "human_review"),
            }
        summary[split] = split_summary
    return summary


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    forbidden_splits = normalize_forbidden_splits(args.forbidden_source_splits)

    rows_by_split = {
        "train": base_rows_for_output(args.dataset_root, "train"),
        "validation": base_rows_for_output(args.dataset_root, "validation"),
        "test": base_rows_for_output(args.dataset_root, "test"),
    }
    review_rows, review_summary = merged_review_rows(args, forbidden_splits)
    rows_by_split.setdefault(args.target_split, [])
    rows_by_split[args.target_split].extend(review_rows)

    for split, rows in rows_by_split.items():
        write_jsonl(args.output_dir / f"{split}.jsonl", rows)

    base_metadata = {}
    metadata_path = args.dataset_root / "metadata.json"
    if metadata_path.exists():
        base_metadata = json.loads(metadata_path.read_text(encoding="utf-8"))

    metadata = {
        "base_dataset_root": str(args.dataset_root),
        "reviews": str(args.reviews),
        "target_split": args.target_split,
        "sample_weight": args.sample_weight,
        "forbidden_source_splits": sorted(forbidden_splits),
        "allow_forbidden_source_splits": args.allow_forbidden_source_splits,
        "review_summary": review_summary,
        "base_metadata": base_metadata,
        "summary": summarize(rows_by_split),
    }
    (args.output_dir / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps(metadata["summary"], indent=2))


if __name__ == "__main__":
    main()
