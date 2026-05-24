#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import json
import shutil
from pathlib import Path
from typing import Any


FIELDNAMES = [
    "review_id",
    "task",
    "bucket",
    "image",
    "review_image",
    "source_split",
    "source_kind",
    "source_dataset",
    "model_score",
    "model_threshold",
    "model_predicted",
    "original_target",
    "reviewed_target",
    "review_decision",
    "reviewer",
    "notes",
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build a CSV review queue from mined accessibility vision hard examples.")
    parser.add_argument("--hard-examples", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--dataset-root", type=Path, default=None, help="Optional fallback root for relative image paths.")
    parser.add_argument("--limit-per-task-bucket", type=int, default=0)
    parser.add_argument("--copy-images", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--review-image-dir-name", default="review_images")
    return parser.parse_args()


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if line.strip():
                rows.append(json.loads(line))
    return rows


def resolve_image_path(row: dict[str, Any], dataset_root: Path | None) -> Path:
    image_path = row.get("image_path")
    if image_path:
        return Path(image_path)
    if dataset_root is None:
        raise ValueError(f"Row has no image_path and no dataset root was provided: {row}")
    return dataset_root / row["image"]


def selected_rows(rows: list[dict[str, Any]], limit_per_task_bucket: int) -> list[dict[str, Any]]:
    if limit_per_task_bucket <= 0:
        return rows

    selected = []
    counts: dict[tuple[str, str], int] = {}
    for row in rows:
        key = (row["task"], row.get("bucket") or "unknown")
        count = counts.get(key, 0)
        if count >= limit_per_task_bucket:
            continue
        counts[key] = count + 1
        selected.append(row)
    return selected


def copy_review_image(
    source_path: Path,
    output_dir: Path,
    review_dir_name: str,
    review_id: str,
    task: str,
    bucket: str,
    enabled: bool,
) -> str:
    if not enabled:
        return str(source_path)
    suffix = source_path.suffix or ".jpg"
    relative_path = Path(review_dir_name) / task / bucket / f"{review_id}{suffix}"
    target_path = output_dir / relative_path
    target_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source_path, target_path)
    return str(relative_path)


def build_rows(args: argparse.Namespace) -> list[dict[str, str]]:
    source_rows = selected_rows(read_jsonl(args.hard_examples), args.limit_per_task_bucket)
    review_rows = []
    for index, row in enumerate(source_rows):
        task = row["task"]
        bucket = row.get("bucket") or "unknown"
        review_id = f"{index:06d}_{task}_{bucket}"
        source_path = resolve_image_path(row, args.dataset_root)
        review_image = copy_review_image(
            source_path,
            args.output_dir,
            args.review_image_dir_name,
            review_id,
            task,
            bucket,
            args.copy_images,
        )
        review_rows.append(
            {
                "review_id": review_id,
                "task": task,
                "bucket": bucket,
                "image": str(source_path),
                "review_image": review_image,
                "source_split": str(row.get("split") or ""),
                "source_kind": str(row.get("source_kind") or ""),
                "source_dataset": str(row.get("source_dataset") or ""),
                "model_score": f"{float(row.get('score', 0.0)):.6f}",
                "model_threshold": f"{float(row.get('threshold', 0.5)):.6f}",
                "model_predicted": str(int(row.get("predicted", 0))),
                "original_target": str(int(row.get("target", 0))),
                "reviewed_target": "",
                "review_decision": "",
                "reviewer": "",
                "notes": "",
            }
        )
    return review_rows


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=FIELDNAMES)
        writer.writeheader()
        writer.writerows(rows)


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    rows = build_rows(args)
    write_csv(args.output_dir / "review_queue.csv", rows)
    metadata = {
        "hard_examples": str(args.hard_examples),
        "rows": len(rows),
        "copy_images": args.copy_images,
        "review_image_dir_name": args.review_image_dir_name,
    }
    (args.output_dir / "review_queue_manifest.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    main()
