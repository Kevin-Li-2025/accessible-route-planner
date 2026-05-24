#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import io
import time
from pathlib import Path
from typing import Any

import requests
import torch
import torch.nn as nn
import uvicorn
from fastapi import FastAPI, HTTPException
from PIL import Image
from pydantic import BaseModel, Field
from torchvision import models, transforms


DEFAULT_TASKS = [
    "curb_ramp_present",
    "curb_ramp_absent",
    "obstacle_present",
    "surface_problem_present",
    "crosswalk_present",
]


class PhotoInput(BaseModel):
    url: str | None = None
    image_base64: str | None = Field(default=None, alias="imageBase64")
    caption: str | None = None
    source: str | None = None


class AnalyzeRequest(BaseModel):
    photos: list[PhotoInput] = []
    threshold_floor: float = Field(default=0.35, alias="thresholdFloor")


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


def load_image(photo: PhotoInput, timeout: float = 5.0) -> Image.Image:
    if photo.image_base64:
        raw = base64.b64decode(photo.image_base64)
        return Image.open(io.BytesIO(raw)).convert("RGB")
    if photo.url:
        response = requests.get(photo.url, timeout=timeout)
        response.raise_for_status()
        return Image.open(io.BytesIO(response.content)).convert("RGB")
    raise ValueError("photo must include url or imageBase64")


def task_candidates(tasks: list[str], probs: list[float], thresholds: dict[str, float], floor: float) -> list[dict[str, Any]]:
    by_task = dict(zip(tasks, probs, strict=True))
    candidates: list[dict[str, Any]] = []

    def add(task: str, attribute: str, value: str, evidence: str) -> None:
        score = by_task.get(task, 0.0)
        threshold = max(float(thresholds.get(task, 0.5)), floor)
        if score >= threshold:
            candidates.append(
                {
                    "attribute": attribute,
                    "value": value,
                    "confidence": round(float(score), 3),
                    "evidence": evidence,
                    "source": "accesscity_local_vision",
                    "canAutoApply": False,
                }
            )

    add("curb_ramp_present", "curb_ramp", "true", "Vision model detected a curb ramp candidate.")
    add("curb_ramp_absent", "curb_ramp", "false", "Vision model detected a missing or unusable curb ramp candidate.")
    add("curb_ramp_absent", "kerb_height_metres", ">0.06", "Missing curb ramp suggests a raised kerb should be measured.")
    add("obstacle_present", "obstacle", "present", "Vision model detected a sidewalk obstacle candidate.")
    add("surface_problem_present", "smoothness", "bad", "Vision model detected a surface problem candidate.")
    add("crosswalk_present", "crosswalk", "true", "Vision model detected a crosswalk candidate.")
    return candidates


def create_app(checkpoint_path: Path, device_name: str) -> FastAPI:
    checkpoint = torch.load(checkpoint_path, map_location="cpu")
    tasks = checkpoint.get("tasks", DEFAULT_TASKS)
    thresholds = checkpoint.get("thresholds", {})
    metrics = checkpoint.get("metrics") or {}
    holdout_metrics = checkpoint.get("holdout_metrics") or {}
    dataset_summary = checkpoint.get("dataset_summary") or {}
    model_name = checkpoint.get("model_name", "convnext_tiny")
    image_size = int(checkpoint.get("image_size", 224))

    device = torch.device(device_name if device_name != "auto" else ("cuda" if torch.cuda.is_available() else "cpu"))
    model = build_model(model_name, len(tasks))
    model.load_state_dict(checkpoint["model_state"])
    model.to(device)
    model.eval()

    transform = transforms.Compose(
        [
            transforms.Resize((image_size, image_size)),
            transforms.ToTensor(),
            transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
        ]
    )

    app = FastAPI(title="AccessCity Accessibility Vision", version="1.0")

    @app.get("/health")
    def health() -> dict[str, Any]:
        return {
            "status": "ok",
            "model": model_name,
            "tasks": tasks,
            "device": str(device),
            "thresholds": thresholds,
            "calibration": {
                "split": checkpoint.get("calibration_split"),
                "macroF1": metrics.get("macro_f1"),
                "macroEce": metrics.get("macro_ece"),
            },
            "holdout": {
                "split": checkpoint.get("holdout_split"),
                "macroF1": holdout_metrics.get("macro_f1"),
                "macroEce": holdout_metrics.get("macro_ece"),
            },
            "datasetRows": {
                key: value.get("rows") if isinstance(value, dict) else None
                for key, value in dataset_summary.items()
            },
        }

    @app.post("/v1/accessibility-vision/analyze")
    @torch.inference_mode()
    def analyze(request: AnalyzeRequest) -> dict[str, Any]:
        if not request.photos:
            raise HTTPException(status_code=400, detail="At least one photo is required.")

        started = time.perf_counter()
        tensors = []
        for photo in request.photos[:4]:
            try:
                image = load_image(photo)
            except Exception as exc:  # noqa: BLE001
                raise HTTPException(status_code=400, detail=f"Could not load photo: {exc}") from exc
            tensors.append(transform(image))

        batch = torch.stack(tensors).to(device)
        probs = torch.sigmoid(model(batch)).detach().cpu()
        max_probs = probs.max(dim=0).values.tolist()
        candidates = task_candidates(tasks, max_probs, thresholds, request.threshold_floor)
        latency_ms = (time.perf_counter() - started) * 1000
        return {
            "model": model_name,
            "tasks": tasks,
            "thresholds": {task: round(float(thresholds.get(task, 0.5)), 4) for task in tasks},
            "probabilities": {task: round(float(prob), 4) for task, prob in zip(tasks, max_probs, strict=True)},
            "candidates": candidates,
            "forRouteDecision": False,
            "latencyMs": round(latency_ms, 3),
            "limitations": [
                "Vision output is a review-only accessibility candidate signal.",
                "Candidates require human/admin review before updating accessibility profiles.",
                "The model never generates route geometry or changes routing edge costs.",
            ],
        }

    return app


def main() -> None:
    parser = argparse.ArgumentParser(description="Serve AccessCity accessibility vision classifier.")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8095)
    parser.add_argument("--device", default="auto")
    args = parser.parse_args()

    app = create_app(args.checkpoint, args.device)
    uvicorn.run(app, host=args.host, port=args.port)


if __name__ == "__main__":
    main()
