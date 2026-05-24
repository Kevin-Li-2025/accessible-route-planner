#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import random
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import torch
import torch.nn as nn
from datasets import concatenate_datasets, load_dataset
from PIL import Image
from PIL import ImageDraw
from sklearn.metrics import accuracy_score, f1_score, precision_score, recall_score, roc_auc_score
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


class AccessibilityDataset(Dataset[tuple[torch.Tensor, torch.Tensor, torch.Tensor]]):
    def __init__(self, dataset: Any, transform: transforms.Compose, weak_cross_task_negatives: bool):
        self.dataset = dataset
        self.transform = transform
        self.weak_cross_task_negatives = weak_cross_task_negatives

    def __len__(self) -> int:
        return len(self.dataset)

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        row = self.dataset[index]
        image = row["image"]
        if not isinstance(image, Image.Image):
            image = Image.open(image)
        image = image.convert("RGB")

        targets = torch.zeros(len(TASKS), dtype=torch.float32)
        mask = torch.zeros(len(TASKS), dtype=torch.float32)
        task_id = int(row["task_id"])
        target = float(row["target"])
        targets[task_id] = target
        if self.weak_cross_task_negatives and target >= 0.5:
            mask[:] = 1.0
        else:
            mask[task_id] = 1.0
        return self.transform(image), targets, mask


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Train AccessCity sidewalk accessibility vision classifier.")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--model", default="convnext_tiny", choices=["convnext_tiny", "efficientnet_b0"])
    parser.add_argument("--epochs", type=int, default=6)
    parser.add_argument("--batch-size", type=int, default=48)
    parser.add_argument("--learning-rate", type=float, default=3e-4)
    parser.add_argument("--weight-decay", type=float, default=0.05)
    parser.add_argument("--num-workers", type=int, default=2)
    parser.add_argument("--seed", type=int, default=20260524)
    parser.add_argument("--max-train-per-task", type=int, default=0)
    parser.add_argument("--max-eval-per-task", type=int, default=0)
    parser.add_argument("--image-size", type=int, default=224)
    parser.add_argument("--threshold-grid", default="0.25,0.30,0.35,0.40,0.45,0.50,0.55,0.60,0.65,0.70,0.75")
    parser.add_argument("--synthetic-smoke", action="store_true", help="Use generated images to verify the training/serving pipeline without downloading data.")
    parser.add_argument("--dataset-root", type=Path, default=None, help="Use an exported JSONL/image dataset instead of Hugging Face Hub.")
    parser.add_argument(
        "--weak-cross-task-negatives",
        action="store_true",
        help="For positive-only Project Sidewalk exports, treat other task heads as weak negatives for the current image.",
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
    rng = random.Random(seed + (0 if split == "train" else 10_000))
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


def build_transforms(image_size: int) -> tuple[transforms.Compose, transforms.Compose]:
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

    weights = models.EfficientNet_B0_Weights.IMAGENET1K_V1
    model = models.efficientnet_b0(weights=weights)
    in_features = model.classifier[1].in_features
    model.classifier[1] = nn.Linear(in_features, len(TASKS))
    return model


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


def masked_bce_loss(logits: torch.Tensor, targets: torch.Tensor, mask: torch.Tensor, pos_weight: torch.Tensor) -> torch.Tensor:
    raw = nn.functional.binary_cross_entropy_with_logits(logits, targets, reduction="none", pos_weight=pos_weight)
    return (raw * mask).sum() / torch.clamp(mask.sum(), min=1.0)


@torch.inference_mode()
def evaluate(model: nn.Module, loader: DataLoader, device: torch.device, thresholds: list[float]) -> dict[str, Any]:
    model.eval()
    per_task_scores: list[list[float]] = [[] for _ in TASKS]
    per_task_targets: list[list[int]] = [[] for _ in TASKS]

    for images, targets, mask in tqdm(loader, desc="eval", leave=False):
        images = images.to(device, non_blocking=True)
        probs = torch.sigmoid(model(images)).cpu()
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

        task_metrics = {
            "count": int(y_true.size),
            "positive_rate": float(y_true.mean()),
            "threshold": float(best["threshold"]),
            "accuracy": float(accuracy_score(y_true, y_pred)),
            "precision": float(precision_score(y_true, y_pred, zero_division=0)),
            "recall": float(recall_score(y_true, y_pred, zero_division=0)),
            "f1": float(best["f1"]),
            "roc_auc": None if math.isnan(auc) else float(auc),
        }
        metrics["tasks"][task_name] = task_metrics
        f1_values.append(task_metrics["f1"])

    metrics["macro_f1"] = float(np.mean(f1_values)) if f1_values else 0.0
    return metrics


def save_checkpoint(path: Path, model: nn.Module, args: argparse.Namespace, metrics: dict[str, Any]) -> None:
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
        },
        path,
    )


def main() -> None:
    args = parse_args()
    seed_everything(args.seed)
    args.output_dir.mkdir(parents=True, exist_ok=True)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if args.dataset_root is not None:
        train_raw = load_exported_split(args.dataset_root, "train", args.max_train_per_task, args.seed)
        val_raw = load_exported_split(args.dataset_root, "validation", args.max_eval_per_task, args.seed)
    elif args.synthetic_smoke:
        train_raw = load_synthetic_split("train", args.max_train_per_task, args.seed)
        val_raw = load_synthetic_split("validation", args.max_eval_per_task, args.seed)
    else:
        train_raw = load_split("train", args.max_train_per_task, args.seed)
        val_raw = load_split("validation", args.max_eval_per_task, args.seed)
    train_tf, eval_tf = build_transforms(args.image_size)

    train_dataset = AccessibilityDataset(train_raw, train_tf, args.weak_cross_task_negatives)
    val_dataset = AccessibilityDataset(val_raw, eval_tf, args.weak_cross_task_negatives)

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

    model = build_model(args.model).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.learning_rate, weight_decay=args.weight_decay)
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")
    pos_weight = compute_pos_weight(train_raw, args.weak_cross_task_negatives).to(device)
    thresholds = [float(item) for item in args.threshold_grid.split(",") if item.strip()]

    args_dict = {
        key: str(value) if isinstance(value, Path) else value
        for key, value in vars(args).items()
    }
    metadata = {
        "tasks": TASKS,
        "dataset_tasks": DATASET_TASKS,
        "train_rows": len(train_raw),
        "validation_rows": len(val_raw),
        "synthetic_smoke": args.synthetic_smoke,
        "weak_cross_task_negatives": args.weak_cross_task_negatives,
        "dataset_root": str(args.dataset_root) if args.dataset_root is not None else None,
        "args": args_dict,
    }
    (args.output_dir / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    best_macro_f1 = -1.0
    history = []
    started = time.perf_counter()
    for epoch in range(1, args.epochs + 1):
        model.train()
        running_loss = 0.0
        batches = 0
        for images, targets, mask in tqdm(train_loader, desc=f"epoch {epoch}/{args.epochs}"):
            images = images.to(device, non_blocking=True)
            targets = targets.to(device, non_blocking=True)
            mask = mask.to(device, non_blocking=True)

            optimizer.zero_grad(set_to_none=True)
            with torch.amp.autocast("cuda", enabled=device.type == "cuda"):
                logits = model(images)
                loss = masked_bce_loss(logits, targets, mask, pos_weight)
            scaler.scale(loss).backward()
            scaler.step(optimizer)
            scaler.update()

            running_loss += float(loss.detach().cpu())
            batches += 1

        metrics = evaluate(model, val_loader, device, thresholds)
        metrics["epoch"] = epoch
        metrics["train_loss"] = running_loss / max(batches, 1)
        metrics["elapsed_seconds"] = round(time.perf_counter() - started, 3)
        history.append(metrics)
        (args.output_dir / "history.json").write_text(json.dumps(history, indent=2), encoding="utf-8")
        (args.output_dir / "latest_metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
        save_checkpoint(args.output_dir / "latest.pt", model, args, metrics)

        if metrics["macro_f1"] >= best_macro_f1:
            best_macro_f1 = metrics["macro_f1"]
            save_checkpoint(args.output_dir / "best.pt", model, args, metrics)

        print(json.dumps({"epoch": epoch, "train_loss": metrics["train_loss"], "macro_f1": metrics["macro_f1"]}, indent=2))

    print(f"best_macro_f1={best_macro_f1:.4f}")


if __name__ == "__main__":
    main()
