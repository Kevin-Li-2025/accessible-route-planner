#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
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

DATASET_TASKS = {
    "projectsidewalk/sidewalk-validator-ai-dataset-curbramp": "curb_ramp_present",
    "projectsidewalk/sidewalk-validator-ai-dataset-nocurbramp": "curb_ramp_absent",
    "projectsidewalk/sidewalk-validator-ai-dataset-obstacle": "obstacle_present",
    "projectsidewalk/sidewalk-validator-ai-dataset-surfaceproblem": "surface_problem_present",
    "projectsidewalk/sidewalk-validator-ai-dataset-crosswalk": "crosswalk_present",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export a bounded Project Sidewalk subset for offline L20 training.")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--train-per-task", type=int, default=1200)
    parser.add_argument("--validation-per-task", type=int, default=300)
    parser.add_argument("--jpeg-quality", type=int, default=92)
    parser.add_argument("--balanced", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--hf-token", default=os.getenv("HF_TOKEN"), help="Optional Hugging Face token for large authenticated exports.")
    return parser.parse_args()


def positive_label(dataset_name: str, hf_token: str | None) -> int:
    builder = load_dataset_builder(dataset_name, token=hf_token)
    label_names = getattr(builder.info.features["label"], "names", None) or []
    return label_names.index("correct") if "correct" in label_names else 0


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")


def export_split(output_dir: Path, split: str, per_task: int, jpeg_quality: int, balanced: bool, hf_token: str | None) -> None:
    rows: list[dict[str, Any]] = []
    images_dir = output_dir / "images" / split
    images_dir.mkdir(parents=True, exist_ok=True)
    split_file = output_dir / f"{split}.jsonl"

    for dataset_name, task_name in DATASET_TASKS.items():
        task_id = TASKS.index(task_name)
        correct_label = positive_label(dataset_name, hf_token)
        stream = load_dataset(dataset_name, split=split, streaming=True, token=hf_token)
        task_dir = images_dir / task_name
        task_dir.mkdir(parents=True, exist_ok=True)

        count = 0
        positive_count = 0
        negative_count = 0
        positive_target = per_task // 2 if balanced else per_task
        negative_target = per_task - positive_target if balanced else 0
        progress = tqdm(total=per_task, desc=f"{split} {task_name}")
        for example in stream:
            target = 1 if int(example["label"]) == correct_label else 0
            if balanced:
                if target == 1 and positive_count >= positive_target:
                    continue
                if target == 0 and negative_count >= negative_target:
                    continue

            image = example["image"]
            if not isinstance(image, Image.Image):
                image = Image.open(image)
            image = image.convert("RGB")
            relative_path = Path("images") / split / task_name / f"{count:06d}.jpg"
            image.save(output_dir / relative_path, format="JPEG", quality=jpeg_quality, optimize=True)
            rows.append(
                {
                    "image": str(relative_path),
                    "task_id": task_id,
                    "task": task_name,
                    "target": target,
                    "source_dataset": dataset_name,
                    "split": split,
                }
            )
            count += 1
            if target == 1:
                positive_count += 1
            else:
                negative_count += 1
            progress.update(1)
            if count >= per_task:
                break
        progress.close()
        if count < per_task:
            raise RuntimeError(f"Only exported {count}/{per_task} rows for {dataset_name} {split}.")
        write_jsonl(split_file, rows)

    write_jsonl(split_file, rows)


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    export_split(args.output_dir, "train", args.train_per_task, args.jpeg_quality, args.balanced, args.hf_token)
    export_split(args.output_dir, "validation", args.validation_per_task, args.jpeg_quality, args.balanced, args.hf_token)
    (args.output_dir / "metadata.json").write_text(
        json.dumps(
            {
                "tasks": TASKS,
                "dataset_tasks": DATASET_TASKS,
                "train_per_task": args.train_per_task,
                "validation_per_task": args.validation_per_task,
                "balanced": args.balanced,
            },
            indent=2,
        ),
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
