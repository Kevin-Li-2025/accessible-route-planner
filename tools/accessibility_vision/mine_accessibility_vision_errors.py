#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import shutil
from collections import defaultdict
from pathlib import Path
from typing import Any

import torch
import torch.nn as nn
from PIL import Image
from PIL import ImageDraw
from torchvision import models, transforms
from tqdm import tqdm


DEFAULT_TASKS = [
    "curb_ramp_present",
    "curb_ramp_absent",
    "obstacle_present",
    "surface_problem_present",
    "crosswalk_present",
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Mine hard examples from an exported AccessCity accessibility vision split."
    )
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--dataset-root", type=Path, required=True)
    parser.add_argument("--split", default="test")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--tasks", default="", help="Comma-separated task filter. Empty means all tasks.")
    parser.add_argument("--uncertain-margin", type=float, default=0.08)
    parser.add_argument("--precision-targets", default="0.70,0.80,0.90")
    parser.add_argument("--examples-per-bucket", type=int, default=32)
    parser.add_argument("--copy-images", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--contact-sheets", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--contact-sheet-columns", type=int, default=4)
    parser.add_argument("--contact-thumb-size", type=int, default=180)
    return parser.parse_args()


def build_model(model_name: str, num_tasks: int) -> nn.Module:
    if model_name == "convnext_tiny":
        model = models.convnext_tiny(weights=None)
        in_features = model.classifier[2].in_features
        model.classifier[2] = nn.Linear(in_features, num_tasks)
        return model

    model = models.efficientnet_b0(weights=None)
    in_features = model.classifier[1].in_features
    model.classifier[1] = nn.Linear(in_features, num_tasks)
    return model


def load_rows(dataset_root: Path, split: str, tasks: list[str]) -> list[dict[str, Any]]:
    split_file = dataset_root / f"{split}.jsonl"
    rows: list[dict[str, Any]] = []
    with split_file.open("r", encoding="utf-8") as handle:
        for index, line in enumerate(handle):
            row = json.loads(line)
            task = row.get("task") or tasks[int(row["task_id"])]
            if task not in tasks:
                raise ValueError(f"Unknown task {task} in {split_file}:{index + 1}")
            row["row_index"] = index
            row["task"] = task
            row["task_id"] = tasks.index(task)
            row["image_path"] = str(dataset_root / row["image"])
            rows.append(row)
    return rows


def selected_tasks(all_tasks: list[str], task_filter: str) -> set[str]:
    if not task_filter.strip():
        return set(all_tasks)
    selected = {item.strip() for item in task_filter.split(",") if item.strip()}
    unknown = selected - set(all_tasks)
    if unknown:
        raise ValueError(f"Unknown tasks: {sorted(unknown)}")
    return selected


def build_eval_transform(image_size: int) -> transforms.Compose:
    return transforms.Compose(
        [
            transforms.Resize((image_size, image_size)),
            transforms.ToTensor(),
            transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
        ]
    )


def temperature_tensor(checkpoint: dict[str, Any], tasks: list[str], device: torch.device) -> torch.Tensor | None:
    logit_temperatures = checkpoint.get("logit_temperatures") or {}
    if not logit_temperatures:
        return None
    values = [max(float(logit_temperatures.get(task, 1.0)), 1e-6) for task in tasks]
    return torch.tensor(values, dtype=torch.float32, device=device).view(1, -1)


@torch.inference_mode()
def predict_rows(
    rows: list[dict[str, Any]],
    checkpoint: dict[str, Any],
    device: torch.device,
    batch_size: int,
) -> list[dict[str, Any]]:
    tasks = checkpoint.get("tasks", DEFAULT_TASKS)
    model_name = checkpoint.get("model_name", "convnext_tiny")
    image_size = int(checkpoint.get("image_size", 224))
    thresholds = checkpoint.get("thresholds") or {}
    transform = build_eval_transform(image_size)
    model = build_model(model_name, len(tasks))
    model.load_state_dict(checkpoint["model_state"])
    model.to(device)
    model.eval()
    temperatures = temperature_tensor(checkpoint, tasks, device)

    predictions: list[dict[str, Any]] = []
    for start in tqdm(range(0, len(rows), batch_size), desc="predict"):
        batch_rows = rows[start : start + batch_size]
        tensors = []
        kept_rows = []
        for row in batch_rows:
            try:
                image = Image.open(row["image_path"]).convert("RGB")
                tensors.append(transform(image))
                kept_rows.append(row)
            except Exception as exc:  # noqa: BLE001
                predictions.append(
                    {
                        "row_index": row["row_index"],
                        "task": row["task"],
                        "target": int(row["target"]),
                        "image": row["image"],
                        "image_path": row["image_path"],
                        "error": f"image_load_failed: {exc}",
                    }
                )
        if not tensors:
            continue

        logits = model(torch.stack(tensors).to(device))
        if temperatures is not None:
            logits = logits / temperatures
        probabilities = torch.sigmoid(logits).detach().cpu()
        for row, probs in zip(kept_rows, probabilities, strict=True):
            task = row["task"]
            task_id = int(row["task_id"])
            target = int(row["target"])
            threshold = float(thresholds.get(task, 0.5))
            score = float(probs[task_id])
            predicted = 1 if score >= threshold else 0
            predictions.append(
                {
                    "row_index": row["row_index"],
                    "task": task,
                    "task_id": task_id,
                    "target": target,
                    "predicted": predicted,
                    "score": score,
                    "threshold": threshold,
                    "margin": score - threshold,
                    "absolute_margin": abs(score - threshold),
                    "source_dataset": row.get("source_dataset"),
                    "source_kind": row.get("source_kind"),
                    "split": row.get("split"),
                    "image": row["image"],
                    "image_path": row["image_path"],
                    "metadata": row.get("metadata") or {},
                }
            )
    return predictions


def summarize_group(rows: list[dict[str, Any]]) -> dict[str, Any]:
    valid = [row for row in rows if "error" not in row]
    total = len(valid)
    tp = sum(1 for row in valid if row["target"] == 1 and row["predicted"] == 1)
    fp = sum(1 for row in valid if row["target"] == 0 and row["predicted"] == 1)
    tn = sum(1 for row in valid if row["target"] == 0 and row["predicted"] == 0)
    fn = sum(1 for row in valid if row["target"] == 1 and row["predicted"] == 0)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    scores = [float(row["score"]) for row in valid]
    return {
        "count": total,
        "positive": tp + fn,
        "negative": tn + fp,
        "accuracy": (tp + tn) / total if total else 0.0,
        "precision": precision,
        "recall": recall,
        "f1": f1,
        "score_mean": sum(scores) / total if total else None,
        "score_min": min(scores) if scores else None,
        "score_max": max(scores) if scores else None,
        "confusion": {"tp": tp, "fp": fp, "tn": tn, "fn": fn},
    }


def parse_precision_targets(value: str) -> list[float]:
    return [float(item.strip()) for item in value.split(",") if item.strip()]


def threshold_metrics(rows: list[dict[str, Any]], threshold: float) -> dict[str, Any]:
    valid = [row for row in rows if "error" not in row]
    tp = fp = tn = fn = 0
    for row in valid:
        target = int(row["target"])
        predicted = 1 if float(row["score"]) >= threshold else 0
        tp += 1 if target == 1 and predicted == 1 else 0
        fp += 1 if target == 0 and predicted == 1 else 0
        tn += 1 if target == 0 and predicted == 0 else 0
        fn += 1 if target == 1 and predicted == 0 else 0
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        "threshold": threshold,
        "precision": precision,
        "recall": recall,
        "f1": f1,
        "predicted_positive": tp + fp,
        "confusion": {"tp": tp, "fp": fp, "tn": tn, "fn": fn},
    }


def threshold_recommendations(rows: list[dict[str, Any]], precision_targets: list[float]) -> dict[str, Any]:
    valid = [row for row in rows if "error" not in row]
    if not valid:
        return {}

    candidate_thresholds = sorted(
        {0.0, 1.0, *[round(float(row["score"]), 6) for row in valid]},
        reverse=False,
    )
    metrics = [threshold_metrics(valid, threshold) for threshold in candidate_thresholds]
    best_f1 = max(metrics, key=lambda item: (item["f1"], item["recall"], -item["threshold"]))
    recommendations: dict[str, Any] = {"best_f1": best_f1, "precision_targets": {}}
    for target in precision_targets:
        feasible = [item for item in metrics if item["precision"] >= target and item["predicted_positive"] > 0]
        if feasible:
            best = max(feasible, key=lambda item: (item["recall"], item["f1"], -item["threshold"]))
            recommendations["precision_targets"][f"{target:.2f}"] = best
        else:
            recommendations["precision_targets"][f"{target:.2f}"] = None
    return recommendations


def summarize_predictions(
    predictions: list[dict[str, Any]],
    uncertain_margin: float,
    precision_targets: list[float],
) -> dict[str, Any]:
    by_task: dict[str, list[dict[str, Any]]] = defaultdict(list)
    by_task_source: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in predictions:
        if "error" in row:
            continue
        by_task[row["task"]].append(row)
        by_task_source[(row["task"], row.get("source_kind") or "unknown")].append(row)

    summary: dict[str, Any] = {
        "count": len([row for row in predictions if "error" not in row]),
        "errors": len([row for row in predictions if "error" in row]),
        "uncertain_margin": uncertain_margin,
        "tasks": {},
        "task_source_kind": {},
    }
    for task, rows in sorted(by_task.items()):
        task_summary = summarize_group(rows)
        uncertain = [row for row in rows if float(row["absolute_margin"]) <= uncertain_margin]
        task_summary["uncertain"] = len(uncertain)
        task_summary["uncertain_rate"] = len(uncertain) / len(rows) if rows else 0.0
        task_summary["threshold_recommendations"] = threshold_recommendations(rows, precision_targets)
        summary["tasks"][task] = task_summary

    for (task, source_kind), rows in sorted(by_task_source.items()):
        summary["task_source_kind"].setdefault(task, {})[source_kind] = summarize_group(rows)
    return summary


def hard_buckets(
    predictions: list[dict[str, Any]],
    tasks: set[str],
    examples_per_bucket: int,
    uncertain_margin: float,
) -> dict[tuple[str, str], list[dict[str, Any]]]:
    by_bucket: dict[tuple[str, str], list[dict[str, Any]]] = {}
    valid = [row for row in predictions if "error" not in row and row["task"] in tasks]
    for task in sorted(tasks):
        task_rows = [row for row in valid if row["task"] == task]
        false_positive = [row for row in task_rows if row["target"] == 0 and row["predicted"] == 1]
        false_negative = [row for row in task_rows if row["target"] == 1 and row["predicted"] == 0]
        uncertain = [row for row in task_rows if float(row["absolute_margin"]) <= uncertain_margin]
        confident_wrong = false_positive + false_negative

        by_bucket[(task, "false_positive")] = sorted(false_positive, key=lambda row: row["margin"], reverse=True)[:examples_per_bucket]
        by_bucket[(task, "false_negative")] = sorted(false_negative, key=lambda row: row["margin"])[:examples_per_bucket]
        by_bucket[(task, "uncertain")] = sorted(uncertain, key=lambda row: row["absolute_margin"])[:examples_per_bucket]
        by_bucket[(task, "confident_wrong")] = sorted(
            confident_wrong,
            key=lambda row: abs(float(row["margin"])),
            reverse=True,
        )[:examples_per_bucket]
    return by_bucket


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")


def copy_hard_images(output_dir: Path, buckets: dict[tuple[str, str], list[dict[str, Any]]]) -> None:
    for (task, bucket), rows in buckets.items():
        target_dir = output_dir / "images" / task / bucket
        target_dir.mkdir(parents=True, exist_ok=True)
        for index, row in enumerate(rows):
            suffix = Path(row["image_path"]).suffix or ".jpg"
            target_path = target_dir / f"{index:03d}_target{row['target']}_score{row['score']:.3f}{suffix}"
            shutil.copy2(row["image_path"], target_path)


def draw_contact_sheet(rows: list[dict[str, Any]], output_path: Path, columns: int, thumb_size: int) -> None:
    if not rows:
        return
    columns = max(1, columns)
    rows_count = math.ceil(len(rows) / columns)
    label_height = 46
    sheet = Image.new("RGB", (columns * thumb_size, rows_count * (thumb_size + label_height)), "white")
    draw = ImageDraw.Draw(sheet)
    for index, row in enumerate(rows):
        column = index % columns
        row_index = index // columns
        x = column * thumb_size
        y = row_index * (thumb_size + label_height)
        try:
            image = Image.open(row["image_path"]).convert("RGB")
            image.thumbnail((thumb_size, thumb_size), Image.Resampling.BICUBIC)
            image_x = x + (thumb_size - image.width) // 2
            image_y = y + (thumb_size - image.height) // 2
            sheet.paste(image, (image_x, image_y))
        except Exception as exc:  # noqa: BLE001
            draw.text((x + 6, y + 6), f"load failed: {exc}", fill=(180, 0, 0))
        label = f"{index:02d} y={row['target']} p={row['score']:.3f} t={row['threshold']:.2f}"
        source = str(row.get("source_kind") or "")[:28]
        draw.rectangle((x, y + thumb_size, x + thumb_size, y + thumb_size + label_height), fill=(245, 245, 245))
        draw.text((x + 6, y + thumb_size + 5), label, fill=(0, 0, 0))
        draw.text((x + 6, y + thumb_size + 24), source, fill=(70, 70, 70))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output_path, quality=90)


def write_contact_sheets(
    output_dir: Path,
    buckets: dict[tuple[str, str], list[dict[str, Any]]],
    columns: int,
    thumb_size: int,
) -> None:
    for (task, bucket), rows in buckets.items():
        draw_contact_sheet(rows, output_dir / "contact_sheets" / f"{task}_{bucket}.jpg", columns, thumb_size)


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)

    checkpoint = torch.load(args.checkpoint, map_location="cpu")
    tasks = checkpoint.get("tasks", DEFAULT_TASKS)
    focus_tasks = selected_tasks(tasks, args.tasks)
    rows = [row for row in load_rows(args.dataset_root, args.split, tasks) if row["task"] in focus_tasks]
    device = torch.device(args.device if args.device != "auto" else ("cuda" if torch.cuda.is_available() else "cpu"))
    predictions = predict_rows(rows, checkpoint, device, args.batch_size)
    summary = summarize_predictions(predictions, args.uncertain_margin, parse_precision_targets(args.precision_targets))
    buckets = hard_buckets(predictions, focus_tasks, args.examples_per_bucket, args.uncertain_margin)

    write_jsonl(args.output_dir / "predictions.jsonl", predictions)
    hard_rows = []
    for (task, bucket), rows in sorted(buckets.items()):
        for row in rows:
            hard_row = dict(row)
            hard_row["bucket"] = bucket
            hard_rows.append(hard_row)
    write_jsonl(args.output_dir / "hard_examples.jsonl", hard_rows)
    (args.output_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")

    if args.copy_images:
        copy_hard_images(args.output_dir, buckets)
    if args.contact_sheets:
        write_contact_sheets(args.output_dir, buckets, args.contact_sheet_columns, args.contact_thumb_size)

    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
