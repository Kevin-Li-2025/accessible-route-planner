#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

import numpy as np
import torch
from sklearn.metrics import accuracy_score, brier_score_loss, f1_score, precision_score, recall_score, roc_auc_score
from torch.utils.data import DataLoader
from torchvision import transforms
from tqdm import tqdm

from serve_accessibility_vision import build_model
from train_accessibility_vision import TASKS, AccessibilityDataset, expected_calibration_error, load_exported_split


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate an averaged ensemble of AccessCity vision checkpoints.")
    parser.add_argument("--dataset-root", type=Path, required=True)
    parser.add_argument("--checkpoint", dest="checkpoints", type=Path, action="append", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--calibration-split", default="validation")
    parser.add_argument("--holdout-split", default="test")
    parser.add_argument("--batch-size", type=int, default=48)
    parser.add_argument("--num-workers", type=int, default=2)
    parser.add_argument("--seed", type=int, default=20260524)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--threshold-grid", default="0.25,0.30,0.35,0.40,0.45,0.50,0.55,0.60,0.65,0.70,0.75")
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


def best_thresholds(
    scores: np.ndarray,
    targets: np.ndarray,
    mask: np.ndarray,
    threshold_grid: list[float],
    calibration_bins: int,
) -> dict[str, Any]:
    metrics: dict[str, Any] = {"tasks": {}, "macro_f1": 0.0}
    f1_values: list[float] = []
    for task_id, task_name in enumerate(TASKS):
        selected = mask[:, task_id] > 0
        y_true = targets[selected, task_id].astype(np.int32)
        y_score = scores[selected, task_id].astype(np.float32)
        best = {"threshold": 0.5, "f1": -1.0}
        for threshold in threshold_grid:
            y_pred = (y_score >= threshold).astype(np.int32)
            f1 = f1_score(y_true, y_pred, zero_division=0)
            if f1 > best["f1"]:
                best = {"threshold": threshold, "f1": f1}
        task_metrics = task_metrics_for_threshold(y_true, y_score, best["threshold"], calibration_bins)
        metrics["tasks"][task_name] = task_metrics
        f1_values.append(task_metrics["f1"])
    metrics["macro_f1"] = float(np.mean(f1_values)) if f1_values else 0.0
    metrics["macro_ece"] = macro_ece(metrics)
    return metrics


def evaluate_fixed_thresholds(
    scores: np.ndarray,
    targets: np.ndarray,
    mask: np.ndarray,
    thresholds: dict[str, float],
    calibration_bins: int,
) -> dict[str, Any]:
    metrics: dict[str, Any] = {"tasks": {}, "macro_f1": 0.0}
    f1_values: list[float] = []
    for task_id, task_name in enumerate(TASKS):
        selected = mask[:, task_id] > 0
        y_true = targets[selected, task_id].astype(np.int32)
        y_score = scores[selected, task_id].astype(np.float32)
        task_metrics = task_metrics_for_threshold(y_true, y_score, thresholds[task_name], calibration_bins)
        metrics["tasks"][task_name] = task_metrics
        f1_values.append(task_metrics["f1"])
    metrics["macro_f1"] = float(np.mean(f1_values)) if f1_values else 0.0
    metrics["macro_ece"] = macro_ece(metrics)
    return metrics


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


def average_scores(
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
    return np.mean(np.stack(scores, axis=0), axis=0), target_ref, mask_ref, members


def main() -> None:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    device = torch.device(args.device if args.device == "cuda" and torch.cuda.is_available() else "cpu")
    thresholds = [float(item) for item in args.threshold_grid.split(",") if item.strip()]

    calibration_rows = load_exported_split(args.dataset_root, args.calibration_split, 0, args.seed)
    holdout_rows = load_exported_split(args.dataset_root, args.holdout_split, 0, args.seed)

    calibration_scores, calibration_targets, calibration_mask, members = average_scores(
        args.checkpoints,
        calibration_rows,
        args,
        device,
    )
    calibration_metrics = best_thresholds(
        calibration_scores,
        calibration_targets,
        calibration_mask,
        thresholds,
        args.calibration_bins,
    )
    fixed_thresholds = {
        task: metrics["threshold"]
        for task, metrics in calibration_metrics["tasks"].items()
    }

    holdout_scores, holdout_targets, holdout_mask, _members = average_scores(
        args.checkpoints,
        holdout_rows,
        args,
        device,
    )
    holdout_metrics = evaluate_fixed_thresholds(
        holdout_scores,
        holdout_targets,
        holdout_mask,
        fixed_thresholds,
        args.calibration_bins,
    )
    holdout_metrics["holdout_split"] = args.holdout_split
    holdout_metrics["ensemble_members"] = members

    result = {
        "ensembleMembers": members,
        "calibrationSplit": args.calibration_split,
        "holdoutSplit": args.holdout_split,
        "calibrationRows": len(calibration_rows),
        "holdoutRows": len(holdout_rows),
        "calibrationMetrics": calibration_metrics,
        "holdoutMetrics": holdout_metrics,
    }
    (args.output_dir / "ensemble_metrics.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"holdout_macro_f1": holdout_metrics["macro_f1"], "holdout_macro_ece": holdout_metrics["macro_ece"]}, indent=2))


if __name__ == "__main__":
    main()
