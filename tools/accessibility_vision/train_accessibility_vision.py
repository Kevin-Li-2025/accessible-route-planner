#!/usr/bin/env python3
from __future__ import annotations

import argparse
import copy
import json
import math
import random
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import numpy as np
import torch
import torch.nn as nn
from datasets import concatenate_datasets, load_dataset
from PIL import Image
from PIL import ImageDraw
from sklearn.metrics import accuracy_score, brier_score_loss, f1_score, precision_score, recall_score, roc_auc_score
from torch.utils.data import DataLoader, Dataset
from torchvision import models, transforms
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


@dataclass(frozen=True)
class Example:
    image: Image.Image
    task_id: int
    target: float


class AccessibilityDataset(Dataset[tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]]):
    def __init__(self, dataset: Any, transform: transforms.Compose, weak_cross_task_negatives: bool):
        self.dataset = dataset
        self.transform = transform
        self.weak_cross_task_negatives = weak_cross_task_negatives

    def __len__(self) -> int:
        return len(self.dataset)

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
        row = self.dataset[index]
        image = row["image"]
        if not isinstance(image, Image.Image):
            image = Image.open(image)
        image = image.convert("RGB")

        targets = torch.zeros(len(TASKS), dtype=torch.float32)
        mask = torch.zeros(len(TASKS), dtype=torch.float32)
        sample_weight = torch.full((len(TASKS),), float(row.get("sample_weight", 1.0)), dtype=torch.float32)
        task_id = int(row["task_id"])
        target = float(row["target"])
        targets[task_id] = target
        if self.weak_cross_task_negatives and target >= 0.5:
            mask[:] = 1.0
        else:
            mask[task_id] = 1.0
        return self.transform(image), targets, mask, sample_weight


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Train AccessCity sidewalk accessibility vision classifier.")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--model", default="convnext_tiny", choices=["convnext_tiny", "convnext_small", "efficientnet_b0"])
    parser.add_argument("--epochs", type=int, default=6)
    parser.add_argument("--batch-size", type=int, default=48)
    parser.add_argument("--learning-rate", type=float, default=3e-4)
    parser.add_argument("--min-learning-rate", type=float, default=1e-6)
    parser.add_argument("--weight-decay", type=float, default=0.05)
    parser.add_argument("--num-workers", type=int, default=2)
    parser.add_argument("--seed", type=int, default=20260524)
    parser.add_argument("--max-train-per-task", type=int, default=0)
    parser.add_argument("--max-eval-per-task", type=int, default=0)
    parser.add_argument("--image-size", type=int, default=224)
    parser.add_argument("--threshold-grid", default="0.25,0.30,0.35,0.40,0.45,0.50,0.55,0.60,0.65,0.70,0.75")
    parser.add_argument("--scheduler", default="cosine", choices=["cosine", "none"])
    parser.add_argument("--warmup-epochs", type=float, default=1.0)
    parser.add_argument("--gradient-clip-norm", type=float, default=1.0)
    parser.add_argument("--ema-decay", type=float, default=0.999)
    parser.add_argument("--freeze-backbone-epochs", type=float, default=0.0)
    parser.add_argument("--early-stopping-patience", type=int, default=0)
    parser.add_argument("--train-augmentation", default="standard", choices=["standard", "strong"])
    parser.add_argument("--channels-last", action="store_true")
    parser.add_argument("--calibration-split", default="validation")
    parser.add_argument("--holdout-split", default="test")
    parser.add_argument("--calibration-bins", type=int, default=10)
    parser.add_argument(
        "--temperature-scale",
        action="store_true",
        help="Fit per-task logit temperatures on the calibration split before final threshold selection.",
    )
    parser.add_argument("--temperature-scale-max-iter", type=int, default=80)
    parser.add_argument("--synthetic-smoke", action="store_true", help="Use generated images to verify the training/serving pipeline without downloading data.")
    parser.add_argument("--dataset-root", type=Path, default=None, help="Use an exported JSONL/image dataset instead of Hugging Face Hub.")
    parser.add_argument(
        "--weak-cross-task-negatives",
        action="store_true",
        help="For positive-only Project Sidewalk exports, treat other task heads as weak negatives for the current image.",
    )
    parser.add_argument(
        "--task-balanced-loss",
        action="store_true",
        help="Weight supervised labels so each task contributes equally even when one task has more training rows.",
    )
    return parser.parse_args()


def seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)


def load_split(split: str, max_per_task: int, seed: int):
    parts = []
    for dataset_name, task_name in DATASET_TASKS.items():
        try:
            ds = load_dataset(dataset_name, split=split)
        except ValueError:
            if split != "validation":
                raise
            ds = load_dataset(dataset_name, split="val")
        label_feature = ds.features["label"]
        label_names = getattr(label_feature, "names", None) or []
        positive_label = label_names.index("correct") if "correct" in label_names else 0
        task_id = TASKS.index(task_name)

        if max_per_task > 0 and len(ds) > max_per_task:
            ds = ds.shuffle(seed=seed).select(range(max_per_task))

        def annotate(row: dict[str, Any]) -> dict[str, Any]:
            row["task_id"] = task_id
            row["target"] = 1 if int(row["label"]) == positive_label else 0
            return row

        parts.append(ds.map(annotate, desc=f"tag {dataset_name} {split}"))
    return concatenate_datasets(parts).shuffle(seed=seed)


def load_synthetic_split(split: str, max_per_task: int, seed: int) -> list[dict[str, Any]]:
    split_offsets = {"train": 0, "validation": 10_000, "test": 20_000}
    rng = random.Random(seed + split_offsets.get(split, 30_000))
    rows: list[dict[str, Any]] = []
    count = max_per_task if max_per_task > 0 else 64
    task_colors = [
        (30, 144, 255),
        (220, 20, 60),
        (255, 140, 0),
        (139, 69, 19),
        (46, 139, 87),
    ]
    for task_id, color in enumerate(task_colors):
        for index in range(count):
            target = 1 if index % 2 == 0 else 0
            image = Image.new("RGB", (256, 256), (35, 35, 35))
            draw = ImageDraw.Draw(image)
            draw.rectangle((0, 170, 256, 256), fill=(75, 75, 75))
            jitter = rng.randint(-12, 12)
            if target:
                offset = 20 + task_id * 35
                draw.rectangle((offset + jitter, 82, offset + 44 + jitter, 160), fill=color)
                draw.ellipse((170, 38 + task_id * 3, 222, 90 + task_id * 3), fill=color)
            else:
                draw.line((20, 210, 236, 190 + jitter), fill=(190, 190, 190), width=5)
                draw.rectangle((170 + jitter, 60, 210 + jitter, 120), outline=(120, 120, 120), width=4)
            rows.append({"image": image, "task_id": task_id, "target": target})
    rng.shuffle(rows)
    return rows


def load_exported_split(dataset_root: Path, split: str, max_per_task: int, seed: int) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    split_file = dataset_root / f"{split}.jsonl"
    with split_file.open("r", encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            row["image"] = str(dataset_root / row["image"])
            rows.append(row)

    if max_per_task > 0:
        rng = random.Random(seed)
        selected: list[dict[str, Any]] = []
        for task_id in range(len(TASKS)):
            task_rows = [row for row in rows if int(row["task_id"]) == task_id]
            rng.shuffle(task_rows)
            selected.extend(task_rows[:max_per_task])
        rows = selected

    random.Random(seed).shuffle(rows)
    return rows


def load_optional_exported_split(dataset_root: Path, split: str, max_per_task: int, seed: int) -> list[dict[str, Any]]:
    split_file = dataset_root / f"{split}.jsonl"
    if not split_file.exists():
        return []
    return load_exported_split(dataset_root, split, max_per_task, seed)


def build_transforms(image_size: int, train_augmentation: str) -> tuple[transforms.Compose, transforms.Compose]:
    if train_augmentation == "strong":
        train_tf = transforms.Compose(
            [
                transforms.RandomResizedCrop(image_size, scale=(0.72, 1.0), ratio=(0.85, 1.15)),
                transforms.RandomHorizontalFlip(p=0.5),
                transforms.RandomApply(
                    [
                        transforms.ColorJitter(
                            brightness=0.25,
                            contrast=0.25,
                            saturation=0.18,
                            hue=0.02,
                        )
                    ],
                    p=0.8,
                ),
                transforms.RandomGrayscale(p=0.04),
                transforms.RandomRotation(degrees=3),
                transforms.ToTensor(),
                transforms.RandomErasing(p=0.12, scale=(0.01, 0.04), ratio=(0.3, 3.3)),
                transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
            ]
        )
    else:
        train_tf = transforms.Compose(
            [
                transforms.Resize((image_size, image_size)),
                transforms.RandomHorizontalFlip(p=0.5),
                transforms.ColorJitter(brightness=0.15, contrast=0.15, saturation=0.1),
                transforms.ToTensor(),
                transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
            ]
        )
    eval_tf = transforms.Compose(
        [
            transforms.Resize((image_size, image_size)),
            transforms.ToTensor(),
            transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
        ]
    )
    return train_tf, eval_tf


def build_model(model_name: str) -> nn.Module:
    if model_name == "convnext_tiny":
        weights = models.ConvNeXt_Tiny_Weights.IMAGENET1K_V1
        model = models.convnext_tiny(weights=weights)
        in_features = model.classifier[2].in_features
        model.classifier[2] = nn.Linear(in_features, len(TASKS))
        return model
    if model_name == "convnext_small":
        weights = models.ConvNeXt_Small_Weights.IMAGENET1K_V1
        model = models.convnext_small(weights=weights)
        in_features = model.classifier[2].in_features
        model.classifier[2] = nn.Linear(in_features, len(TASKS))
        return model

    weights = models.EfficientNet_B0_Weights.IMAGENET1K_V1
    model = models.efficientnet_b0(weights=weights)
    in_features = model.classifier[1].in_features
    model.classifier[1] = nn.Linear(in_features, len(TASKS))
    return model


def set_backbone_trainable(model: nn.Module, model_name: str, trainable: bool) -> None:
    head_prefixes = {
        "convnext_tiny": ("classifier.2",),
        "convnext_small": ("classifier.2",),
        "efficientnet_b0": ("classifier.1",),
    }
    prefixes = head_prefixes.get(model_name, ())
    for name, parameter in model.named_parameters():
        parameter.requires_grad = trainable or name.startswith(prefixes)


def build_scheduler(
    optimizer: torch.optim.Optimizer,
    scheduler_name: str,
    *,
    learning_rate: float,
    min_learning_rate: float,
    total_steps: int,
    warmup_steps: int,
) -> torch.optim.lr_scheduler.LRScheduler | None:
    if scheduler_name == "none":
        return None

    total_steps = max(1, total_steps)
    warmup_steps = max(0, min(warmup_steps, total_steps - 1))
    min_factor = max(0.0, min(min_learning_rate / learning_rate, 1.0)) if learning_rate > 0 else 0.0

    def lr_lambda(step: int) -> float:
        if warmup_steps > 0 and step < warmup_steps:
            return max(min_factor, (step + 1) / warmup_steps)
        progress = (step - warmup_steps) / max(1, total_steps - warmup_steps)
        cosine = 0.5 * (1.0 + math.cos(math.pi * min(1.0, max(0.0, progress))))
        return min_factor + (1.0 - min_factor) * cosine

    return torch.optim.lr_scheduler.LambdaLR(optimizer, lr_lambda)


def clone_ema_model(model: nn.Module) -> nn.Module:
    ema_model = copy.deepcopy(model)
    ema_model.eval()
    for parameter in ema_model.parameters():
        parameter.requires_grad_(False)
    return ema_model


@torch.no_grad()
def update_ema_model(ema_model: nn.Module, model: nn.Module, decay: float) -> None:
    model_state = model.state_dict()
    for name, ema_value in ema_model.state_dict().items():
        model_value = model_state[name].detach()
        if ema_value.dtype.is_floating_point:
            ema_value.mul_(decay).add_(model_value, alpha=1.0 - decay)
        else:
            ema_value.copy_(model_value)


def to_device_batch(
    images: torch.Tensor,
    targets: torch.Tensor,
    mask: torch.Tensor,
    sample_weight: torch.Tensor,
    device: torch.device,
    *,
    channels_last: bool,
) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
    images = images.to(device, non_blocking=True)
    if channels_last and device.type == "cuda":
        images = images.contiguous(memory_format=torch.channels_last)
    return (
        images,
        targets.to(device, non_blocking=True),
        mask.to(device, non_blocking=True),
        sample_weight.to(device, non_blocking=True),
    )


def compute_pos_weight(dataset: Any, weak_cross_task_negatives: bool) -> torch.Tensor:
    positives = torch.zeros(len(TASKS), dtype=torch.float64)
    totals = torch.zeros(len(TASKS), dtype=torch.float64)
    for row in dataset:
        task_id = int(row["task_id"])
        target = float(row["target"])
        if weak_cross_task_negatives and target >= 0.5:
            positives[task_id] += 1.0
            totals += 1.0
        else:
            positives[task_id] += target
            totals[task_id] += 1.0
    negatives = torch.clamp(totals - positives, min=1.0)
    positives = torch.clamp(positives, min=1.0)
    return (negatives / positives).float()


def compute_task_loss_weight(dataset: Any, weak_cross_task_negatives: bool) -> torch.Tensor:
    totals = torch.zeros(len(TASKS), dtype=torch.float64)
    for row in dataset:
        target = float(row["target"])
        if weak_cross_task_negatives and target >= 0.5:
            totals += 1.0
        else:
            totals[int(row["task_id"])] += 1.0

    positive_totals = torch.clamp(totals, min=1.0)
    weights = positive_totals.sum() / (len(TASKS) * positive_totals)
    return weights.float()


def summarize_rows(dataset: Any, weak_cross_task_negatives: bool) -> dict[str, Any]:
    summary: dict[str, Any] = {"rows": len(dataset), "tasks": {}}
    positives = {task: 0 for task in TASKS}
    totals = {task: 0 for task in TASKS}
    for row in dataset:
        task_id = int(row["task_id"])
        task_name = TASKS[task_id]
        target = float(row["target"])
        if weak_cross_task_negatives and target >= 0.5:
            for name in TASKS:
                totals[name] += 1
            positives[task_name] += 1
        else:
            totals[task_name] += 1
            positives[task_name] += 1 if target >= 0.5 else 0

    for task_name in TASKS:
        task_total = totals[task_name]
        task_positives = positives[task_name]
        summary["tasks"][task_name] = {
            "rows": task_total,
            "positive": task_positives,
            "negative": task_total - task_positives,
            "positive_rate": round(task_positives / task_total, 4) if task_total else 0.0,
        }
    return summary


def expected_calibration_error(y_true: np.ndarray, y_score: np.ndarray, bins: int) -> float | None:
    if y_true.size == 0:
        return None

    edges = np.linspace(0.0, 1.0, bins + 1)
    ece = 0.0
    for index in range(bins):
        lower = edges[index]
        upper = edges[index + 1]
        selected = (y_score >= lower) & (y_score < upper if index < bins - 1 else y_score <= upper)
        if not selected.any():
            continue
        confidence = float(y_score[selected].mean())
        observed = float(y_true[selected].mean())
        ece += float(selected.mean()) * abs(confidence - observed)
    return ece


def masked_bce_loss(
    logits: torch.Tensor,
    targets: torch.Tensor,
    mask: torch.Tensor,
    pos_weight: torch.Tensor,
    task_loss_weight: torch.Tensor,
    sample_weight: torch.Tensor,
) -> torch.Tensor:
    raw = nn.functional.binary_cross_entropy_with_logits(logits, targets, reduction="none", pos_weight=pos_weight)
    weighted_mask = mask * task_loss_weight * sample_weight
    return (raw * weighted_mask).sum() / torch.clamp(weighted_mask.sum(), min=1.0)


@torch.inference_mode()
def collect_logits_and_targets(
    model: nn.Module,
    loader: DataLoader,
    device: torch.device,
) -> tuple[list[np.ndarray], list[np.ndarray]]:
    model.eval()
    per_task_logits: list[list[float]] = [[] for _ in TASKS]
    per_task_targets: list[list[int]] = [[] for _ in TASKS]

    for images, targets, mask, _sample_weight in tqdm(loader, desc="calibrate", leave=False):
        images = images.to(device, non_blocking=True)
        logits = model(images).cpu()
        targets = targets.cpu()
        mask = mask.cpu()

        for task_id in range(len(TASKS)):
            selected = mask[:, task_id] > 0
            if selected.any():
                per_task_logits[task_id].extend(logits[selected, task_id].tolist())
                per_task_targets[task_id].extend(targets[selected, task_id].int().tolist())

    return (
        [np.array(values, dtype=np.float32) for values in per_task_logits],
        [np.array(values, dtype=np.float32) for values in per_task_targets],
    )


def fit_logit_temperatures(
    per_task_logits: list[np.ndarray],
    per_task_targets: list[np.ndarray],
    max_iter: int,
) -> dict[str, float]:
    fitted: dict[str, float] = {}
    for task_id, task_name in enumerate(TASKS):
        logits_np = per_task_logits[task_id]
        targets_np = per_task_targets[task_id]
        if logits_np.size == 0 or np.unique(targets_np).size < 2:
            fitted[task_name] = 1.0
            continue

        logits = torch.tensor(logits_np, dtype=torch.float32)
        targets = torch.tensor(targets_np, dtype=torch.float32)
        log_temperature = torch.zeros((), requires_grad=True)
        optimizer = torch.optim.LBFGS([log_temperature], lr=0.1, max_iter=max_iter, line_search_fn="strong_wolfe")

        def closure() -> torch.Tensor:
            optimizer.zero_grad()
            temperature = torch.clamp(torch.exp(log_temperature), min=0.05, max=20.0)
            loss = nn.functional.binary_cross_entropy_with_logits(logits / temperature, targets)
            loss.backward()
            return loss

        optimizer.step(closure)
        fitted[task_name] = float(torch.clamp(torch.exp(log_temperature.detach()), min=0.05, max=20.0))
    return fitted


def temperature_tensor_for_device(logit_temperatures: dict[str, float] | None, device: torch.device) -> torch.Tensor | None:
    if not logit_temperatures:
        return None
    values = [max(float(logit_temperatures.get(task, 1.0)), 1e-6) for task in TASKS]
    return torch.tensor(values, dtype=torch.float32, device=device).view(1, -1)


@torch.inference_mode()
def evaluate(
    model: nn.Module,
    loader: DataLoader,
    device: torch.device,
    thresholds: list[float],
    calibration_bins: int,
    fixed_thresholds: dict[str, float] | None = None,
    logit_temperatures: dict[str, float] | None = None,
) -> dict[str, Any]:
    model.eval()
    per_task_scores: list[list[float]] = [[] for _ in TASKS]
    per_task_targets: list[list[int]] = [[] for _ in TASKS]
    temperature_tensor = temperature_tensor_for_device(logit_temperatures, device)

    for images, targets, mask, _sample_weight in tqdm(loader, desc="eval", leave=False):
        images = images.to(device, non_blocking=True)
        logits = model(images)
        if temperature_tensor is not None:
            logits = logits / temperature_tensor
        probs = torch.sigmoid(logits).cpu()
        targets = targets.cpu()
        mask = mask.cpu()

        for task_id in range(len(TASKS)):
            selected = mask[:, task_id] > 0
            if selected.any():
                per_task_scores[task_id].extend(probs[selected, task_id].tolist())
                per_task_targets[task_id].extend(targets[selected, task_id].int().tolist())

    metrics: dict[str, Any] = {"tasks": {}, "macro_f1": 0.0}
    f1_values = []
    for task_id, task_name in enumerate(TASKS):
        y_true = np.array(per_task_targets[task_id], dtype=np.int32)
        y_score = np.array(per_task_scores[task_id], dtype=np.float32)
        if y_true.size == 0:
            continue

        best = {"threshold": 0.5, "f1": -1.0}
        if fixed_thresholds is not None and task_name in fixed_thresholds:
            threshold = fixed_thresholds[task_name]
            y_pred = (y_score >= threshold).astype(np.int32)
            best = {"threshold": threshold, "f1": f1_score(y_true, y_pred, zero_division=0)}
        else:
            for threshold in thresholds:
                y_pred = (y_score >= threshold).astype(np.int32)
                f1 = f1_score(y_true, y_pred, zero_division=0)
                if f1 > best["f1"]:
                    best = {"threshold": threshold, "f1": f1}

        y_pred = (y_score >= best["threshold"]).astype(np.int32)
        try:
            auc = roc_auc_score(y_true, y_score)
        except ValueError:
            auc = float("nan")
        try:
            brier = brier_score_loss(y_true, y_score)
        except ValueError:
            brier = float("nan")

        true_positive = int(((y_pred == 1) & (y_true == 1)).sum())
        false_positive = int(((y_pred == 1) & (y_true == 0)).sum())
        true_negative = int(((y_pred == 0) & (y_true == 0)).sum())
        false_negative = int(((y_pred == 0) & (y_true == 1)).sum())

        task_metrics = {
            "count": int(y_true.size),
            "positive_rate": float(y_true.mean()),
            "threshold": float(best["threshold"]),
            "accuracy": float(accuracy_score(y_true, y_pred)),
            "precision": float(precision_score(y_true, y_pred, zero_division=0)),
            "recall": float(recall_score(y_true, y_pred, zero_division=0)),
            "f1": float(best["f1"]),
            "roc_auc": None if math.isnan(auc) else float(auc),
            "brier": None if math.isnan(brier) else float(brier),
            "ece": expected_calibration_error(y_true, y_score, calibration_bins),
            "confusion": {
                "tp": true_positive,
                "fp": false_positive,
                "tn": true_negative,
                "fn": false_negative,
            },
        }
        metrics["tasks"][task_name] = task_metrics
        f1_values.append(task_metrics["f1"])

    metrics["macro_f1"] = float(np.mean(f1_values)) if f1_values else 0.0
    ece_values = [values["ece"] for values in metrics["tasks"].values() if values.get("ece") is not None]
    metrics["macro_ece"] = float(np.mean(ece_values)) if ece_values else None
    return metrics


def save_checkpoint(
    path: Path,
    model: nn.Module,
    args: argparse.Namespace,
    metrics: dict[str, Any],
    dataset_summary: dict[str, Any],
    holdout_metrics: dict[str, Any] | None = None,
) -> None:
    thresholds = {
        task: values["threshold"]
        for task, values in metrics.get("tasks", {}).items()
        if "threshold" in values
    }
    torch.save(
        {
            "model_state": model.state_dict(),
            "model_name": args.model,
            "image_size": args.image_size,
            "tasks": TASKS,
            "thresholds": thresholds,
            "metrics": metrics,
            "holdout_metrics": holdout_metrics,
            "dataset_summary": dataset_summary,
            "calibration_split": args.calibration_split,
            "holdout_split": args.holdout_split,
            "calibration_bins": args.calibration_bins,
            "temperature_scaled": False,
            "logit_temperatures": {},
        },
        path,
    )


def thresholds_from_metrics(metrics: dict[str, Any]) -> dict[str, float]:
    return {
        task: values["threshold"]
        for task, values in metrics.get("tasks", {}).items()
        if "threshold" in values
    }


def is_better_checkpoint(metrics: dict[str, Any], best_macro_f1: float, best_macro_ece: float | None) -> bool:
    macro_f1 = float(metrics.get("macro_f1", 0.0))
    macro_ece = metrics.get("macro_ece")
    if macro_f1 > best_macro_f1 + 1e-6:
        return True
    if abs(macro_f1 - best_macro_f1) > 1e-6:
        return False
    if best_macro_ece is None:
        return macro_ece is not None
    return macro_ece is not None and float(macro_ece) < best_macro_ece


def format_metric(value: Any) -> str:
    if value is None:
        return "n/a"
    try:
        return f"{float(value):.4f}"
    except (TypeError, ValueError):
        return str(value)


def write_model_card(output_dir: Path, checkpoint: dict[str, Any], metadata: dict[str, Any]) -> None:
    metrics = checkpoint.get("metrics") or {}
    holdout_metrics = checkpoint.get("holdout_metrics") or {}
    thresholds = checkpoint.get("thresholds") or {}
    logit_temperatures = checkpoint.get("logit_temperatures") or {}
    dataset_summary = metadata.get("dataset_summary") or {}
    generated_at = datetime.now(timezone.utc).isoformat(timespec="seconds")

    lines = [
        "# AccessCity Accessibility Vision Model Card",
        "",
        f"Generated: `{generated_at}`",
        f"Model: `{checkpoint.get('model_name', 'unknown')}`",
        f"Image size: `{checkpoint.get('image_size', 'unknown')}`",
        f"Temperature scaled: `{bool(logit_temperatures)}`",
        "",
        "## Intended Use",
        "",
        "This model produces review-only sidewalk accessibility candidates from field or street-view images. "
        "It must not generate route geometry, change routing graph edge costs, or directly alter safe-path decisions.",
        "",
        "## Tasks",
        "",
    ]
    for task in checkpoint.get("tasks", TASKS):
        lines.append(f"- `{task}`")

    lines.extend(["", "## Dataset Summary", ""])
    for split, summary in dataset_summary.items():
        if not isinstance(summary, dict):
            continue
        lines.append(f"- `{split}` rows: `{summary.get('rows', 'n/a')}`")

    lines.extend(
        [
            "",
            "## Metrics",
            "",
            "| Split | Macro F1 | Macro ECE |",
            "| --- | ---: | ---: |",
            f"| calibration | {format_metric(metrics.get('macro_f1'))} | {format_metric(metrics.get('macro_ece'))} |",
            f"| holdout | {format_metric(holdout_metrics.get('macro_f1'))} | {format_metric(holdout_metrics.get('macro_ece'))} |",
            "",
            "## Per-Task Thresholds",
            "",
            "| Task | Threshold | Temperature |",
            "| --- | ---: | ---: |",
        ]
    )
    for task in checkpoint.get("tasks", TASKS):
        lines.append(
            f"| `{task}` | {format_metric(thresholds.get(task))} | {format_metric(logit_temperatures.get(task, 1.0))} |"
        )

    lines.extend(
        [
            "",
            "## Production Quality Gate",
            "",
            "Serve this checkpoint with `--require-holdout-metrics` and `--require-temperature-scaling`. "
            "Recommended initial production gates are holdout macro F1 >= 0.70 and macro ECE <= 0.12; raise the F1 gate per city after collecting local reviewed labels.",
            "",
            "## Guardrails",
            "",
            "- `canAutoApply` must remain false for generated candidates.",
            "- Human/admin review is required before candidate attributes update OSM/accessibility profiles.",
            "- The routing algorithm can only consume reviewed profile data, never raw model output.",
            "- Final holdout examples must not be merged back into training data.",
            "",
            "## Training Arguments",
            "",
            "```json",
            json.dumps(metadata.get("args", {}), indent=2),
            "```",
            "",
        ]
    )
    (output_dir / "model_card.md").write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    args = parse_args()
    seed_everything(args.seed)
    args.output_dir.mkdir(parents=True, exist_ok=True)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if args.dataset_root is not None:
        train_raw = load_exported_split(args.dataset_root, "train", args.max_train_per_task, args.seed)
        val_raw = load_exported_split(args.dataset_root, args.calibration_split, args.max_eval_per_task, args.seed)
        holdout_raw = load_optional_exported_split(args.dataset_root, args.holdout_split, args.max_eval_per_task, args.seed)
    elif args.synthetic_smoke:
        train_raw = load_synthetic_split("train", args.max_train_per_task, args.seed)
        val_raw = load_synthetic_split(args.calibration_split, args.max_eval_per_task, args.seed)
        holdout_raw = load_synthetic_split(args.holdout_split, args.max_eval_per_task, args.seed)
    else:
        train_raw = load_split("train", args.max_train_per_task, args.seed)
        val_raw = load_split(args.calibration_split, args.max_eval_per_task, args.seed)
        try:
            holdout_raw = load_split(args.holdout_split, args.max_eval_per_task, args.seed)
        except Exception:  # noqa: BLE001
            holdout_raw = []
    train_tf, eval_tf = build_transforms(args.image_size, args.train_augmentation)

    train_dataset = AccessibilityDataset(train_raw, train_tf, args.weak_cross_task_negatives)
    val_dataset = AccessibilityDataset(val_raw, eval_tf, args.weak_cross_task_negatives)
    holdout_dataset = AccessibilityDataset(holdout_raw, eval_tf, args.weak_cross_task_negatives) if holdout_raw else None

    train_loader = DataLoader(
        train_dataset,
        batch_size=args.batch_size,
        shuffle=True,
        num_workers=args.num_workers,
        pin_memory=device.type == "cuda",
    )
    val_loader = DataLoader(
        val_dataset,
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.num_workers,
        pin_memory=device.type == "cuda",
    )
    holdout_loader = (
        DataLoader(
            holdout_dataset,
            batch_size=args.batch_size,
            shuffle=False,
            num_workers=args.num_workers,
            pin_memory=device.type == "cuda",
        )
        if holdout_dataset is not None
        else None
    )

    model = build_model(args.model).to(device)
    if args.channels_last and device.type == "cuda":
        model = model.to(memory_format=torch.channels_last)
    ema_model = clone_ema_model(model) if args.ema_decay > 0 else None
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.learning_rate, weight_decay=args.weight_decay)
    total_steps = max(1, args.epochs * max(len(train_loader), 1))
    scheduler = build_scheduler(
        optimizer,
        args.scheduler,
        learning_rate=args.learning_rate,
        min_learning_rate=args.min_learning_rate,
        total_steps=total_steps,
        warmup_steps=int(args.warmup_epochs * max(len(train_loader), 1)),
    )
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")
    pos_weight = compute_pos_weight(train_raw, args.weak_cross_task_negatives).to(device)
    task_loss_weight = (
        compute_task_loss_weight(train_raw, args.weak_cross_task_negatives).to(device)
        if args.task_balanced_loss
        else torch.ones(len(TASKS), dtype=torch.float32, device=device)
    )
    thresholds = [float(item) for item in args.threshold_grid.split(",") if item.strip()]

    args_dict = {
        key: str(value) if isinstance(value, Path) else value
        for key, value in vars(args).items()
    }
    metadata = {
        "tasks": TASKS,
        "dataset_tasks": DATASET_TASKS,
        "train_rows": len(train_raw),
        "calibration_rows": len(val_raw),
        "holdout_rows": len(holdout_raw),
        "synthetic_smoke": args.synthetic_smoke,
        "weak_cross_task_negatives": args.weak_cross_task_negatives,
        "task_balanced_loss": args.task_balanced_loss,
        "temperature_scale": args.temperature_scale,
        "scheduler": args.scheduler,
        "warmup_epochs": args.warmup_epochs,
        "gradient_clip_norm": args.gradient_clip_norm,
        "ema_decay": args.ema_decay,
        "freeze_backbone_epochs": args.freeze_backbone_epochs,
        "early_stopping_patience": args.early_stopping_patience,
        "train_augmentation": args.train_augmentation,
        "channels_last": args.channels_last,
        "task_loss_weight": {
            task: float(task_loss_weight[index].detach().cpu())
            for index, task in enumerate(TASKS)
        },
        "dataset_root": str(args.dataset_root) if args.dataset_root is not None else None,
        "calibration_split": args.calibration_split,
        "holdout_split": args.holdout_split,
        "dataset_summary": {
            "train": summarize_rows(train_raw, args.weak_cross_task_negatives),
            args.calibration_split: summarize_rows(val_raw, args.weak_cross_task_negatives),
            args.holdout_split: summarize_rows(holdout_raw, args.weak_cross_task_negatives) if holdout_raw else None,
        },
        "args": args_dict,
    }
    (args.output_dir / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    dataset_summary = metadata["dataset_summary"]

    best_macro_f1 = -1.0
    best_macro_ece: float | None = None
    best_epoch = 0
    epochs_without_improvement = 0
    global_step = 0
    history = []
    started = time.perf_counter()
    for epoch in range(1, args.epochs + 1):
        set_backbone_trainable(model, args.model, trainable=epoch > args.freeze_backbone_epochs)
        model.train()
        running_loss = 0.0
        batches = 0
        for images, targets, mask, sample_weight in tqdm(train_loader, desc=f"epoch {epoch}/{args.epochs}"):
            images, targets, mask, sample_weight = to_device_batch(
                images,
                targets,
                mask,
                sample_weight,
                device,
                channels_last=args.channels_last,
            )

            optimizer.zero_grad(set_to_none=True)
            with torch.amp.autocast("cuda", enabled=device.type == "cuda"):
                logits = model(images)
                loss = masked_bce_loss(logits, targets, mask, pos_weight, task_loss_weight, sample_weight)
            scaler.scale(loss).backward()
            if args.gradient_clip_norm > 0:
                scaler.unscale_(optimizer)
                nn.utils.clip_grad_norm_(model.parameters(), max_norm=args.gradient_clip_norm)
            scaler.step(optimizer)
            scaler.update()
            if scheduler is not None:
                scheduler.step()
            if ema_model is not None:
                global_step += 1
                ema_decay = min(args.ema_decay, (1 + global_step) / (10 + global_step))
                update_ema_model(ema_model, model, ema_decay)

            running_loss += float(loss.detach().cpu())
            batches += 1

        eval_model = ema_model if ema_model is not None else model
        metrics = evaluate(eval_model, val_loader, device, thresholds, args.calibration_bins)
        metrics["epoch"] = epoch
        metrics["train_loss"] = running_loss / max(batches, 1)
        metrics["elapsed_seconds"] = round(time.perf_counter() - started, 3)
        metrics["calibration_split"] = args.calibration_split
        metrics["learning_rate"] = optimizer.param_groups[0]["lr"]
        metrics["ema_decay"] = args.ema_decay if ema_model is not None else None
        history.append(metrics)
        (args.output_dir / "history.json").write_text(json.dumps(history, indent=2), encoding="utf-8")
        (args.output_dir / "latest_metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
        save_checkpoint(args.output_dir / "latest.pt", eval_model, args, metrics, dataset_summary)

        if is_better_checkpoint(metrics, best_macro_f1, best_macro_ece):
            best_macro_f1 = metrics["macro_f1"]
            best_macro_ece = metrics.get("macro_ece")
            best_epoch = epoch
            epochs_without_improvement = 0
            save_checkpoint(args.output_dir / "best.pt", eval_model, args, metrics, dataset_summary)
        else:
            epochs_without_improvement += 1

        print(json.dumps({"epoch": epoch, "train_loss": metrics["train_loss"], "macro_f1": metrics["macro_f1"]}, indent=2))
        if args.early_stopping_patience > 0 and epochs_without_improvement >= args.early_stopping_patience:
            print(f"early_stopping_patience reached after epoch {epoch}; best_epoch={best_epoch}")
            break

    best_path = args.output_dir / "best.pt"
    best_checkpoint = torch.load(best_path, map_location=device)
    model.load_state_dict(best_checkpoint["model_state"])
    logit_temperatures: dict[str, float] = {}
    if args.temperature_scale:
        per_task_logits, per_task_targets = collect_logits_and_targets(model, val_loader, device)
        logit_temperatures = fit_logit_temperatures(
            per_task_logits,
            per_task_targets,
            args.temperature_scale_max_iter,
        )
        calibrated_metrics = evaluate(
            model,
            val_loader,
            device,
            thresholds,
            args.calibration_bins,
            logit_temperatures=logit_temperatures,
        )
        calibrated_metrics["epoch"] = best_epoch
        calibrated_metrics["calibration_split"] = args.calibration_split
        calibrated_metrics["temperature_scaled"] = True
        calibrated_metrics["logit_temperatures"] = logit_temperatures
        (args.output_dir / "calibrated_metrics.json").write_text(json.dumps(calibrated_metrics, indent=2), encoding="utf-8")
        best_checkpoint["metrics"] = calibrated_metrics
        best_checkpoint["thresholds"] = thresholds_from_metrics(calibrated_metrics)
        best_checkpoint["temperature_scaled"] = True
        best_checkpoint["logit_temperatures"] = logit_temperatures

    if holdout_loader is not None:
        holdout_metrics = evaluate(
            model,
            holdout_loader,
            device,
            thresholds,
            args.calibration_bins,
            fixed_thresholds=best_checkpoint.get("thresholds", {}),
            logit_temperatures=logit_temperatures,
        )
        holdout_metrics["best_epoch"] = best_epoch
        holdout_metrics["holdout_split"] = args.holdout_split
        holdout_metrics["temperature_scaled"] = bool(logit_temperatures)
        holdout_metrics["logit_temperatures"] = logit_temperatures
        (args.output_dir / "holdout_metrics.json").write_text(json.dumps(holdout_metrics, indent=2), encoding="utf-8")
        best_checkpoint["holdout_metrics"] = holdout_metrics
        print(json.dumps({"holdout_macro_f1": holdout_metrics["macro_f1"], "holdout_macro_ece": holdout_metrics["macro_ece"]}, indent=2))
    torch.save(best_checkpoint, best_path)
    write_model_card(args.output_dir, best_checkpoint, metadata)

    print(f"best_macro_f1={best_macro_f1:.4f}")


if __name__ == "__main__":
    main()
