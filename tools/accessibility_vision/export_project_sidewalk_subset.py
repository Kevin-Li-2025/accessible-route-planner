#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from datasets import load_dataset, load_dataset_builder
from PIL import Image
from tqdm import tqdm


TASKS = [
    "curb_ramp_present",
    "curb_ramp_absent",
    "obstacle_present",
    "surface_problem_present",
    "crosswalk_present",
]

VALIDATOR_DATASET_TASKS = {
    "projectsidewalk/sidewalk-validator-ai-dataset-curbramp": "curb_ramp_present",
    "projectsidewalk/sidewalk-validator-ai-dataset-nocurbramp": "curb_ramp_absent",
    "projectsidewalk/sidewalk-validator-ai-dataset-obstacle": "obstacle_present",
    "projectsidewalk/sidewalk-validator-ai-dataset-surfaceproblem": "surface_problem_present",
    "projectsidewalk/sidewalk-validator-ai-dataset-crosswalk": "crosswalk_present",
}

RAMPNET_CROP_DATASET = "projectsidewalk/rampnet-crop-model-dataset"
RAMPNET_PANORAMA_DATASET = "projectsidewalk/rampnet-dataset"


@dataclass(frozen=True)
class SplitPlan:
    name: str
    rows_per_task: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export Project Sidewalk and RampNet data into AccessCity's offline vision-training format."
    )
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--train-per-task", type=int, default=1200)
    parser.add_argument("--validation-per-task", type=int, default=300)
    parser.add_argument("--test-per-task", type=int, default=300)
    parser.add_argument("--jpeg-quality", type=int, default=92)
    parser.add_argument("--max-image-side", type=int, default=1024)
    parser.add_argument("--balanced", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--hf-token", default=os.getenv("HF_TOKEN"), help="Optional Hugging Face token for authenticated exports.")
    parser.add_argument("--include-rampnet-crop", action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument("--rampnet-crop-train", type=int, default=0, help="0 means all rows available in the split.")
    parser.add_argument("--rampnet-crop-validation", type=int, default=0, help="0 means all rows available in the split.")
    parser.add_argument("--rampnet-crop-test", type=int, default=0, help="0 means all rows available in the split.")
    parser.add_argument("--include-rampnet-panorama", action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument("--rampnet-panorama-train", type=int, default=0)
    parser.add_argument("--rampnet-panorama-validation", type=int, default=0)
    parser.add_argument("--rampnet-panorama-test", type=int, default=0)
    return parser.parse_args()


def split_plans(args: argparse.Namespace) -> list[SplitPlan]:
    return [
        SplitPlan("train", args.train_per_task),
        SplitPlan("validation", args.validation_per_task),
        SplitPlan("test", args.test_per_task),
    ]


def positive_label(dataset_name: str, hf_token: str | None) -> int:
    builder = load_dataset_builder(dataset_name, token=hf_token)
    label_names = getattr(builder.info.features["label"], "names", None) or []
    return label_names.index("correct") if "correct" in label_names else 0


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")


def open_image(example: dict[str, Any]) -> Image.Image:
    image = example["image"]
    if not isinstance(image, Image.Image):
        image = Image.open(image)
    return image.convert("RGB")


def save_image(image: Image.Image, path: Path, jpeg_quality: int, max_image_side: int) -> tuple[int, int]:
    path.parent.mkdir(parents=True, exist_ok=True)
    if max_image_side > 0 and max(image.size) > max_image_side:
        image = image.copy()
        image.thumbnail((max_image_side, max_image_side), Image.Resampling.BICUBIC)
    image.save(path, format="JPEG", quality=jpeg_quality, optimize=True)
    return image.size


def append_row(
    rows: list[dict[str, Any]],
    output_dir: Path,
    split: str,
    task_name: str,
    target: int,
    source_dataset: str,
    source_kind: str,
    image: Image.Image,
    counters: dict[str, int],
    jpeg_quality: int,
    max_image_side: int,
    metadata: dict[str, Any] | None = None,
) -> None:
    task_id = TASKS.index(task_name)
    key = f"{split}:{task_name}:{source_kind}"
    index = counters.get(key, 0)
    counters[key] = index + 1

    relative_path = Path("images") / split / task_name / f"{source_kind}_{index:07d}.jpg"
    width, height = save_image(image, output_dir / relative_path, jpeg_quality, max_image_side)
    row = {
        "image": str(relative_path),
        "task_id": task_id,
        "task": task_name,
        "target": int(target),
        "source_dataset": source_dataset,
        "source_kind": source_kind,
        "split": split,
        "width": width,
        "height": height,
    }
    if metadata:
        row["metadata"] = metadata
    rows.append(row)


def load_stream(dataset_name: str, split: str, hf_token: str | None):
    try:
        return load_dataset(dataset_name, split=split, streaming=True, token=hf_token)
    except ValueError:
        if split == "validation":
            return load_dataset(dataset_name, split="val", streaming=True, token=hf_token)
        raise


def export_validator_split(
    output_dir: Path,
    split: str,
    per_task: int,
    jpeg_quality: int,
    max_image_side: int,
    balanced: bool,
    hf_token: str | None,
    rows: list[dict[str, Any]],
    counters: dict[str, int],
) -> None:
    for dataset_name, task_name in VALIDATOR_DATASET_TASKS.items():
        correct_label = positive_label(dataset_name, hf_token)
        stream = load_stream(dataset_name, split, hf_token)

        count = 0
        positive_count = 0
        negative_count = 0
        positive_target = per_task // 2 if balanced else per_task
        negative_target = per_task - positive_target if balanced else 0
        progress = tqdm(total=per_task, desc=f"{split} validator {task_name}")
        for example in stream:
            target = 1 if int(example["label"]) == correct_label else 0
            if balanced:
                if target == 1 and positive_count >= positive_target:
                    continue
                if target == 0 and negative_count >= negative_target:
                    continue

            append_row(
                rows,
                output_dir,
                split,
                task_name,
                target,
                dataset_name,
                "validator",
                open_image(example),
                counters,
                jpeg_quality,
                max_image_side,
            )
            count += 1
            positive_count += 1 if target == 1 else 0
            negative_count += 1 if target == 0 else 0
            progress.update(1)
            if count >= per_task:
                break
        progress.close()
        if count < per_task:
            raise RuntimeError(f"Only exported {count}/{per_task} rows for {dataset_name} {split}.")


def rampnet_limit(args: argparse.Namespace, split: str, prefix: str) -> int:
    normalized = "validation" if split == "validation" else split
    return int(getattr(args, f"{prefix}_{normalized}".replace("-", "_")))


def export_rampnet_crop_split(
    output_dir: Path,
    split: str,
    limit: int,
    jpeg_quality: int,
    max_image_side: int,
    hf_token: str | None,
    rows: list[dict[str, Any]],
    counters: dict[str, int],
) -> None:
    stream = load_stream(RAMPNET_CROP_DATASET, split, hf_token)
    progress = tqdm(total=None if limit == 0 else limit, desc=f"{split} rampnet crop")
    count = 0
    for example in stream:
        append_row(
            rows,
            output_dir,
            split,
            "curb_ramp_present",
            1,
            RAMPNET_CROP_DATASET,
            "rampnet_crop",
            open_image(example),
            counters,
            jpeg_quality,
            max_image_side,
        )
        count += 1
        progress.update(1)
        if limit > 0 and count >= limit:
            break
    progress.close()


def export_rampnet_panorama_split(
    output_dir: Path,
    split: str,
    limit: int,
    jpeg_quality: int,
    max_image_side: int,
    hf_token: str | None,
    balanced: bool,
    rows: list[dict[str, Any]],
    counters: dict[str, int],
) -> None:
    if limit <= 0:
        return

    stream = load_stream(RAMPNET_PANORAMA_DATASET, split, hf_token)
    count = 0
    positive_count = 0
    negative_count = 0
    positive_target = limit // 2 if balanced else limit
    negative_target = limit - positive_target if balanced else 0
    progress = tqdm(total=limit, desc=f"{split} rampnet panorama")
    for example in stream:
        point_count = len(example.get("curb_ramp_points_normalized") or [])
        target = 1 if point_count > 0 else 0
        if balanced:
            if target == 1 and positive_count >= positive_target:
                continue
            if target == 0 and negative_count >= negative_target:
                continue

        append_row(
            rows,
            output_dir,
            split,
            "curb_ramp_present",
            target,
            RAMPNET_PANORAMA_DATASET,
            "rampnet_panorama",
            open_image(example),
            counters,
            jpeg_quality,
            max_image_side,
            {
                "pano_id": example.get("pano_id"),
                "curb_ramp_point_count": point_count,
            },
        )
        count += 1
        positive_count += 1 if target == 1 else 0
        negative_count += 1 if target == 0 else 0
        progress.update(1)
        if count >= limit:
            break
    progress.close()
    if count < limit:
        raise RuntimeError(f"Only exported {count}/{limit} RampNet panorama rows for {split}.")


def export_split(output_dir: Path, split_plan: SplitPlan, args: argparse.Namespace, counters: dict[str, int]) -> list[dict[str, Any]]:
    split = split_plan.name
    rows: list[dict[str, Any]] = []
    export_validator_split(
        output_dir,
        split,
        split_plan.rows_per_task,
        args.jpeg_quality,
        args.max_image_side,
        args.balanced,
        args.hf_token,
        rows,
        counters,
    )

    if args.include_rampnet_crop:
        export_rampnet_crop_split(
            output_dir,
            split,
            rampnet_limit(args, split, "rampnet_crop"),
            args.jpeg_quality,
            args.max_image_side,
            args.hf_token,
            rows,
            counters,
        )

    if args.include_rampnet_panorama:
        export_rampnet_panorama_split(
            output_dir,
            split,
            rampnet_limit(args, split, "rampnet_panorama"),
            args.jpeg_quality,
            args.max_image_side,
            args.hf_token,
            args.balanced,
            rows,
            counters,
        )

    write_jsonl(output_dir / f"{split}.jsonl", rows)
    return rows


def summarize(rows_by_split: dict[str, list[dict[str, Any]]]) -> dict[str, Any]:
    summary: dict[str, Any] = {}
    for split, rows in rows_by_split.items():
        split_summary: dict[str, Any] = {"rows": len(rows), "tasks": {}}
        for task_name in TASKS:
            task_rows = [row for row in rows if row["task"] == task_name]
            positives = sum(1 for row in task_rows if int(row["target"]) == 1)
            split_summary["tasks"][task_name] = {
                "rows": len(task_rows),
                "positive": positives,
                "negative": len(task_rows) - positives,
                "positive_rate": round(positives / len(task_rows), 4) if task_rows else 0.0,
            }
        summary[split] = split_summary
    return summary


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    counters: dict[str, int] = {}
    rows_by_split: dict[str, list[dict[str, Any]]] = {}

    for plan in split_plans(args):
        rows_by_split[plan.name] = export_split(args.output_dir, plan, args, counters)

    metadata = {
        "tasks": TASKS,
        "validator_dataset_tasks": VALIDATOR_DATASET_TASKS,
        "rampnet_crop_dataset": RAMPNET_CROP_DATASET if args.include_rampnet_crop else None,
        "rampnet_panorama_dataset": RAMPNET_PANORAMA_DATASET if args.include_rampnet_panorama else None,
        "train_per_task": args.train_per_task,
        "validation_per_task": args.validation_per_task,
        "test_per_task": args.test_per_task,
        "balanced": args.balanced,
        "max_image_side": args.max_image_side,
        "summary": summarize(rows_by_split),
    }
    (args.output_dir / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps(metadata["summary"], indent=2))


if __name__ == "__main__":
    main()
