#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import re
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
import torch
from sklearn.metrics import accuracy_score, average_precision_score, brier_score_loss, f1_score, precision_score, recall_score, roc_auc_score
from torch.utils.data import DataLoader
from torchvision import transforms
from tqdm import tqdm

from serve_accessibility_vision import build_model
from train_accessibility_vision import TASKS, AccessibilityDataset, expected_calibration_error, load_exported_split


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate a calibrated ensemble of AccessCity vision checkpoints.")
    parser.add_argument("--dataset-root", type=Path, required=True)
    parser.add_argument("--checkpoint", dest="checkpoints", type=Path, action="append", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--calibration-split", default="validation")
    parser.add_argument("--holdout-split", default="test")
    parser.add_argument("--batch-size", type=int, default=48)
    parser.add_argument("--num-workers", type=int, default=2)
    parser.add_argument("--seed", type=int, default=20260524)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--weighting", choices=["uniform", "validation"], default="uniform")
    parser.add_argument("--threshold-grid", default="0.25,0.30,0.35,0.40,0.45,0.50,0.55,0.60,0.65,0.70,0.75")
    parser.add_argument("--weight-grid", default="0.00,0.05,0.10,0.15,0.20,0.25,0.30,0.35,0.40,0.45,0.50,0.55,0.60,0.65,0.70,0.75,0.80,0.85,0.90,0.95,1.00")
    parser.add_argument("--calibration-bins", type=int, default=10)
    return parser.parse_args()


def eval_transform(image_size: int) -> transforms.Compose:
    return transforms.Compose(
        [
            transforms.Resize((image_size, image_size)),
            transforms.ToTensor(),
            transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
        ]
    )


def temperature_tensor(checkpoint: dict[str, Any], device: torch.device) -> torch.Tensor | None:
    temperatures = checkpoint.get("logit_temperatures") or {}
    if not temperatures:
        return None
    values = [max(float(temperatures.get(task, 1.0)), 1e-6) for task in TASKS]
    return torch.tensor(values, dtype=torch.float32, device=device).view(1, -1)


@torch.inference_mode()
def collect_checkpoint_scores(
    checkpoint_path: Path,
    rows: list[dict[str, Any]],
    args: argparse.Namespace,
    device: torch.device,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, dict[str, Any]]:
    checkpoint = torch.load(checkpoint_path, map_location="cpu")
    model_name = str(checkpoint.get("model_name", "convnext_tiny"))
    image_size = int(checkpoint.get("image_size", 224))
    tasks = checkpoint.get("tasks") or TASKS
    if list(tasks) != TASKS:
        raise RuntimeError(f"{checkpoint_path} task order {tasks!r} does not match expected {TASKS!r}.")

    dataset = AccessibilityDataset(rows, eval_transform(image_size), weak_cross_task_negatives=False)
    loader = DataLoader(
        dataset,
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.num_workers,
        pin_memory=device.type == "cuda",
    )

    model = build_model(model_name, len(TASKS)).to(device)
    model.load_state_dict(checkpoint["model_state"])
    model.eval()
    temp = temperature_tensor(checkpoint, device)

    score_batches: list[np.ndarray] = []
    target_batches: list[np.ndarray] = []
    mask_batches: list[np.ndarray] = []
    for images, targets, mask, _sample_weight in tqdm(loader, desc=f"score {checkpoint_path.name}", leave=False):
        images = images.to(device, non_blocking=True)
        logits = model(images)
        if temp is not None:
            logits = logits / temp
        score_batches.append(torch.sigmoid(logits).cpu().numpy())
        target_batches.append(targets.numpy())
        mask_batches.append(mask.numpy())

    return (
        np.concatenate(score_batches, axis=0),
        np.concatenate(target_batches, axis=0),
        np.concatenate(mask_batches, axis=0),
        {
            "path": str(checkpoint_path),
            "modelName": model_name,
            "imageSize": image_size,
            "temperatureScaled": bool(checkpoint.get("logit_temperatures")),
            "holdoutMacroF1": (checkpoint.get("holdout_metrics") or {}).get("macro_f1"),
            "holdoutMacroEce": (checkpoint.get("holdout_metrics") or {}).get("macro_ece"),
        },
    )


def candidate_weight_vectors(member_count: int, weight_grid: list[float]) -> list[np.ndarray]:
    candidates: list[np.ndarray] = []

    def add(weights: np.ndarray) -> None:
        total = float(weights.sum())
        if total <= 0:
            return
        normalized = (weights / total).astype(np.float32)
        if not any(np.allclose(normalized, existing, atol=1e-6) for existing in candidates):
            candidates.append(normalized)

    add(np.ones(member_count, dtype=np.float32))
    for member_id in range(member_count):
        one_hot = np.zeros(member_count, dtype=np.float32)
        one_hot[member_id] = 1.0
        add(one_hot)

    if member_count == 2:
        for weight in weight_grid:
            add(np.array([weight, 1.0 - weight], dtype=np.float32))
    else:
        for left in range(member_count):
            for right in range(left + 1, member_count):
                for weight in weight_grid:
                    weights = np.zeros(member_count, dtype=np.float32)
                    weights[left] = weight
                    weights[right] = 1.0 - weight
                    add(weights)

    return candidates


def apply_task_weights(score_stack: np.ndarray, task_weights: np.ndarray) -> np.ndarray:
    return (score_stack * task_weights[:, np.newaxis, :]).sum(axis=0)


def format_ensemble_weights(members: list[dict[str, Any]], task_weights: np.ndarray) -> dict[str, dict[str, float]]:
    formatted: dict[str, dict[str, float]] = {}
    for task_id, task_name in enumerate(TASKS):
        formatted[task_name] = {
            member["path"]: round(float(task_weights[member_id, task_id]), 6)
            for member_id, member in enumerate(members)
        }
    return formatted


def best_thresholds_and_weights(
    score_stack: np.ndarray,
    targets: np.ndarray,
    mask: np.ndarray,
    members: list[dict[str, Any]],
    weighting: str,
    threshold_grid: list[float],
    weight_grid: list[float],
    calibration_bins: int,
) -> dict[str, Any]:
    metrics: dict[str, Any] = {"tasks": {}, "macro_f1": 0.0}
    f1_values: list[float] = []
    task_weights = np.zeros((score_stack.shape[0], len(TASKS)), dtype=np.float32)
    uniform_weights = np.ones(score_stack.shape[0], dtype=np.float32) / score_stack.shape[0]
    weight_candidates = [uniform_weights] if weighting == "uniform" else candidate_weight_vectors(score_stack.shape[0], weight_grid)
    for task_id, task_name in enumerate(TASKS):
        selected = mask[:, task_id] > 0
        y_true = targets[selected, task_id].astype(np.int32)
        member_scores = score_stack[:, selected, task_id].astype(np.float32)
        best = {
            "threshold": 0.5,
            "f1": -1.0,
            "weights": uniform_weights,
            "scores": member_scores.mean(axis=0),
        }
        for weights in weight_candidates:
            y_score = np.average(member_scores, axis=0, weights=weights).astype(np.float32)
            for threshold in threshold_grid:
                y_pred = (y_score >= threshold).astype(np.int32)
                f1 = f1_score(y_true, y_pred, zero_division=0)
                if f1 > best["f1"]:
                    best = {"threshold": threshold, "f1": f1, "weights": weights, "scores": y_score}
        task_weights[:, task_id] = best["weights"]
        y_score = best["scores"]
        task_metrics = task_metrics_for_threshold(y_true, y_score, best["threshold"], calibration_bins)
        task_metrics["ensembleWeights"] = {
            members[member_id]["path"]: round(float(best["weights"][member_id]), 6)
            for member_id in range(len(members))
        }
        metrics["tasks"][task_name] = task_metrics
        f1_values.append(task_metrics["f1"])
    metrics["macro_f1"] = float(np.mean(f1_values)) if f1_values else 0.0
    metrics["macro_ece"] = macro_ece(metrics)
    metrics["ensembleWeights"] = format_ensemble_weights(members, task_weights)
    metrics["ensembleWeightMatrix"] = task_weights.tolist()
    return metrics


def evaluate_fixed_thresholds(
    score_stack: np.ndarray,
    targets: np.ndarray,
    mask: np.ndarray,
    thresholds: dict[str, float],
    task_weights: np.ndarray,
    members: list[dict[str, Any]],
    calibration_bins: int,
) -> dict[str, Any]:
    metrics: dict[str, Any] = {"tasks": {}, "macro_f1": 0.0}
    f1_values: list[float] = []
    for task_id, task_name in enumerate(TASKS):
        selected = mask[:, task_id] > 0
        if not selected.any():
            continue
        y_true = targets[selected, task_id].astype(np.int32)
        y_score = np.average(
            score_stack[:, selected, task_id].astype(np.float32),
            axis=0,
            weights=task_weights[:, task_id],
        ).astype(np.float32)
        task_metrics = task_metrics_for_threshold(y_true, y_score, thresholds[task_name], calibration_bins)
        task_metrics["ensembleWeights"] = {
            members[member_id]["path"]: round(float(task_weights[member_id, task_id]), 6)
            for member_id in range(len(members))
        }
        metrics["tasks"][task_name] = task_metrics
        f1_values.append(task_metrics["f1"])
    metrics["macro_f1"] = float(np.mean(f1_values)) if f1_values else 0.0
    metrics["macro_ece"] = macro_ece(metrics)
    metrics["ensembleWeights"] = format_ensemble_weights(members, task_weights)
    metrics["ensembleWeightMatrix"] = task_weights.tolist()
    return metrics


def infer_city(row: dict[str, Any]) -> str:
    metadata = row.get("metadata") if isinstance(row.get("metadata"), dict) else {}
    for key in ("city", "source_city", "sourceCity"):
        value = metadata.get(key) or row.get(key)
        if value:
            return normalize_city_name(str(value))

    source_path = str(metadata.get("source_path") or row.get("source_path") or row.get("image") or "")
    basename = Path(source_path).name.lower()
    match = re.match(r"([a-z][a-z0-9-]+)_", basename)
    if match:
        return normalize_city_name(match.group(1))
    return "unknown"


def normalize_city_name(value: str) -> str:
    normalized = re.sub(r"[^a-z0-9-]+", "-", value.strip().lower()).strip("-")
    return normalized or "unknown"


def evaluate_city_slices(
    score_stack: np.ndarray,
    targets: np.ndarray,
    mask: np.ndarray,
    rows: list[dict[str, Any]],
    thresholds: dict[str, float],
    task_weights: np.ndarray,
    members: list[dict[str, Any]],
    calibration_bins: int,
    min_rows: int = 20,
) -> dict[str, Any]:
    city_to_indices: dict[str, list[int]] = defaultdict(list)
    for index, row in enumerate(rows):
        city_to_indices[infer_city(row)].append(index)

    slices: dict[str, Any] = {}
    for city, indices in sorted(city_to_indices.items()):
        if len(indices) < min_rows:
            continue
        index_array = np.asarray(indices, dtype=np.int64)
        metrics = evaluate_fixed_thresholds(
            score_stack[:, index_array, :],
            targets[index_array, :],
            mask[index_array, :],
            thresholds,
            task_weights,
            members,
            calibration_bins,
        )
        metrics["rows"] = int(len(indices))
        slices[city] = metrics
    return slices


def task_metrics_for_threshold(
    y_true: np.ndarray,
    y_score: np.ndarray,
    threshold: float,
    calibration_bins: int,
) -> dict[str, Any]:
    y_pred = (y_score >= threshold).astype(np.int32)
    try:
        auc = roc_auc_score(y_true, y_score)
    except ValueError:
        auc = float("nan")
    try:
        average_precision = average_precision_score(y_true, y_score)
    except ValueError:
        average_precision = float("nan")
    try:
        brier = brier_score_loss(y_true, y_score)
    except ValueError:
        brier = float("nan")

    return {
        "count": int(y_true.size),
        "positive_rate": float(y_true.mean()) if y_true.size else 0.0,
        "threshold": float(threshold),
        "accuracy": float(accuracy_score(y_true, y_pred)),
        "precision": float(precision_score(y_true, y_pred, zero_division=0)),
        "recall": float(recall_score(y_true, y_pred, zero_division=0)),
        "f1": float(f1_score(y_true, y_pred, zero_division=0)),
        "roc_auc": None if math.isnan(auc) else float(auc),
        "average_precision": None if math.isnan(average_precision) else float(average_precision),
        "brier": None if math.isnan(brier) else float(brier),
        "ece": expected_calibration_error(y_true, y_score, calibration_bins),
        "confusion": {
            "tp": int(((y_pred == 1) & (y_true == 1)).sum()),
            "fp": int(((y_pred == 1) & (y_true == 0)).sum()),
            "tn": int(((y_pred == 0) & (y_true == 0)).sum()),
            "fn": int(((y_pred == 0) & (y_true == 1)).sum()),
        },
    }


def macro_ece(metrics: dict[str, Any]) -> float | None:
    values = [task["ece"] for task in metrics.get("tasks", {}).values() if task.get("ece") is not None]
    return float(np.mean(values)) if values else None


def collect_member_score_stack(
    checkpoint_paths: list[Path],
    rows: list[dict[str, Any]],
    args: argparse.Namespace,
    device: torch.device,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, list[dict[str, Any]]]:
    scores: list[np.ndarray] = []
    target_ref: np.ndarray | None = None
    mask_ref: np.ndarray | None = None
    members: list[dict[str, Any]] = []
    for checkpoint_path in checkpoint_paths:
        score, targets, mask, member = collect_checkpoint_scores(checkpoint_path, rows, args, device)
        scores.append(score)
        members.append(member)
        if target_ref is None:
            target_ref = targets
            mask_ref = mask
        elif not np.array_equal(target_ref, targets) or not np.array_equal(mask_ref, mask):
            raise RuntimeError(f"{checkpoint_path} produced a different dataset order.")
    if target_ref is None or mask_ref is None:
        raise RuntimeError("At least one checkpoint is required.")
    return np.stack(scores, axis=0), target_ref, mask_ref, members


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    device = torch.device(args.device if args.device == "cuda" and torch.cuda.is_available() else "cpu")
    thresholds = [float(item) for item in args.threshold_grid.split(",") if item.strip()]
    weight_grid = [float(item) for item in args.weight_grid.split(",") if item.strip()]

    calibration_rows = load_exported_split(args.dataset_root, args.calibration_split, 0, args.seed)
    holdout_rows = load_exported_split(args.dataset_root, args.holdout_split, 0, args.seed)

    calibration_score_stack, calibration_targets, calibration_mask, members = collect_member_score_stack(
        args.checkpoints,
        calibration_rows,
        args,
        device,
    )
    calibration_metrics = best_thresholds_and_weights(
        calibration_score_stack,
        calibration_targets,
        calibration_mask,
        members,
        args.weighting,
        thresholds,
        weight_grid,
        args.calibration_bins,
    )
    fixed_thresholds = {
        task: metrics["threshold"]
        for task, metrics in calibration_metrics["tasks"].items()
    }
    task_weights = np.asarray(calibration_metrics["ensembleWeightMatrix"], dtype=np.float32)

    holdout_score_stack, holdout_targets, holdout_mask, _members = collect_member_score_stack(
        args.checkpoints,
        holdout_rows,
        args,
        device,
    )
    holdout_metrics = evaluate_fixed_thresholds(
        holdout_score_stack,
        holdout_targets,
        holdout_mask,
        fixed_thresholds,
        task_weights,
        members,
        args.calibration_bins,
    )
    holdout_metrics["holdout_split"] = args.holdout_split
    holdout_metrics["ensemble_members"] = members
    holdout_metrics["by_city"] = evaluate_city_slices(
        holdout_score_stack,
        holdout_targets,
        holdout_mask,
        holdout_rows,
        fixed_thresholds,
        task_weights,
        members,
        args.calibration_bins,
    )

    result = {
        "ensembleMembers": members,
        "calibrationSplit": args.calibration_split,
        "holdoutSplit": args.holdout_split,
        "calibrationRows": len(calibration_rows),
        "holdoutRows": len(holdout_rows),
        "weighting": args.weighting,
        "calibrationMetrics": calibration_metrics,
        "holdoutMetrics": holdout_metrics,
    }
    (args.output_dir / "ensemble_metrics.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"holdout_macro_f1": holdout_metrics["macro_f1"], "holdout_macro_ece": holdout_metrics["macro_ece"]}, indent=2))


if __name__ == "__main__":
    main()
