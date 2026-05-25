#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import base64
import io
import os
import queue
import threading
import time
from concurrent.futures import Future
from contextlib import asynccontextmanager
from dataclasses import dataclass
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


@dataclass
class BatchRequest:
    tensors: list[torch.Tensor]
    submitted_at: float
    future: Future[dict[str, Any]]


@dataclass(frozen=True)
class QualityGateResult:
    passed: bool
    failures: list[str]
    checks: dict[str, Any]

    def as_dict(self) -> dict[str, Any]:
        return {
            "passed": self.passed,
            "failures": self.failures,
            "checks": self.checks,
        }


class MicroBatchPredictor:
    def __init__(
        self,
        model: nn.Module,
        device: torch.device,
        temperature_tensor: torch.Tensor | None,
        max_batch_images: int,
        max_wait_ms: float,
    ) -> None:
        self.model = model
        self.device = device
        self.temperature_tensor = temperature_tensor
        self.max_batch_images = max(1, max_batch_images)
        self.max_wait_seconds = max(0.0, max_wait_ms / 1000.0)
        self.requests: queue.Queue[BatchRequest | None] = queue.Queue()
        self.closed = False
        self.stats_lock = threading.Lock()
        self.stats: dict[str, Any] = {
            "totalRequests": 0,
            "totalImages": 0,
            "totalBatches": 0,
            "maxObservedBatchImages": 0,
            "lastBatchImages": 0,
            "lastBatchRequests": 0,
            "lastBatchLatencyMs": 0.0,
            "lastAverageQueueWaitMs": 0.0,
        }
        self.worker = threading.Thread(target=self._run, name="accesscity-vision-microbatch", daemon=True)
        self.worker.start()

    def submit(self, tensors: list[torch.Tensor]) -> Future[dict[str, Any]]:
        if self.closed:
            future: Future[dict[str, Any]] = Future()
            future.set_exception(RuntimeError("vision predictor is closed"))
            return future
        future = Future()
        self.requests.put(BatchRequest(tensors=tensors, submitted_at=time.perf_counter(), future=future))
        return future

    def snapshot_stats(self) -> dict[str, Any]:
        with self.stats_lock:
            stats = dict(self.stats)
        stats["pendingRequests"] = self.requests.qsize()
        stats["maxBatchImages"] = self.max_batch_images
        stats["maxWaitMs"] = round(self.max_wait_seconds * 1000, 3)
        return stats

    def close(self) -> None:
        self.closed = True
        self.requests.put(None)
        self.worker.join(timeout=2)

    def _run(self) -> None:
        while True:
            request = self.requests.get()
            if request is None:
                return

            batch = [request]
            image_count = len(request.tensors)
            deadline = time.perf_counter() + self.max_wait_seconds
            while image_count < self.max_batch_images:
                timeout = deadline - time.perf_counter()
                if timeout <= 0:
                    break
                try:
                    next_request = self.requests.get(timeout=timeout)
                except queue.Empty:
                    break
                if next_request is None:
                    self.requests.put(None)
                    break
                batch.append(next_request)
                image_count += len(next_request.tensors)

            self._run_batch(batch)

    def _run_batch(self, requests: list[BatchRequest]) -> None:
        started = time.perf_counter()
        try:
            all_tensors = [tensor for request in requests for tensor in request.tensors]
            image_count = len(all_tensors)
            with torch.inference_mode():
                batch = torch.stack(all_tensors).to(self.device)
                logits = self.model(batch)
                if self.temperature_tensor is not None:
                    logits = logits / self.temperature_tensor
                probabilities = torch.sigmoid(logits).detach().cpu()

            latency_ms = (time.perf_counter() - started) * 1000
            offset = 0
            queue_waits = []
            for request in requests:
                count = len(request.tensors)
                max_probs = probabilities[offset : offset + count].max(dim=0).values.tolist()
                offset += count
                queue_wait_ms = (started - request.submitted_at) * 1000
                queue_waits.append(queue_wait_ms)
                request.future.set_result(
                    {
                        "probabilities": max_probs,
                        "batchImages": image_count,
                        "batchRequests": len(requests),
                        "queueWaitMs": queue_wait_ms,
                        "inferenceLatencyMs": latency_ms,
                    }
                )

            with self.stats_lock:
                self.stats["totalRequests"] += len(requests)
                self.stats["totalImages"] += image_count
                self.stats["totalBatches"] += 1
                self.stats["maxObservedBatchImages"] = max(self.stats["maxObservedBatchImages"], image_count)
                self.stats["lastBatchImages"] = image_count
                self.stats["lastBatchRequests"] = len(requests)
                self.stats["lastBatchLatencyMs"] = round(latency_ms, 3)
                self.stats["lastAverageQueueWaitMs"] = round(sum(queue_waits) / max(len(queue_waits), 1), 3)
        except Exception as exc:  # noqa: BLE001
            for request in requests:
                request.future.set_exception(exc)


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


def load_and_transform_photos(photos: list[PhotoInput], transform: transforms.Compose) -> list[torch.Tensor]:
    tensors = []
    for photo in photos[:4]:
        image = load_image(photo)
        tensors.append(transform(image))
    return tensors


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


def metric_number(metrics: dict[str, Any], name: str) -> float | None:
    value = metrics.get(name)
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def validate_checkpoint_quality(
    checkpoint: dict[str, Any],
    require_holdout_metrics: bool,
    min_holdout_macro_f1: float,
    max_holdout_macro_ece: float,
    min_calibration_macro_f1: float,
    max_calibration_macro_ece: float,
    require_temperature_scaling: bool,
) -> QualityGateResult:
    metrics = checkpoint.get("metrics") or {}
    holdout_metrics = checkpoint.get("holdout_metrics") or {}
    logit_temperatures = checkpoint.get("logit_temperatures") or {}
    failures: list[str] = []

    checks: dict[str, Any] = {
        "requireHoldoutMetrics": require_holdout_metrics,
        "minHoldoutMacroF1": min_holdout_macro_f1,
        "maxHoldoutMacroEce": max_holdout_macro_ece,
        "minCalibrationMacroF1": min_calibration_macro_f1,
        "maxCalibrationMacroEce": max_calibration_macro_ece,
        "requireTemperatureScaling": require_temperature_scaling,
        "temperatureScaled": bool(logit_temperatures),
        "holdoutMacroF1": metric_number(holdout_metrics, "macro_f1"),
        "holdoutMacroEce": metric_number(holdout_metrics, "macro_ece"),
        "calibrationMacroF1": metric_number(metrics, "macro_f1"),
        "calibrationMacroEce": metric_number(metrics, "macro_ece"),
    }

    if require_temperature_scaling and not logit_temperatures:
        failures.append("checkpoint is not temperature-scaled")

    holdout_required = require_holdout_metrics or min_holdout_macro_f1 > 0.0 or max_holdout_macro_ece < 1.0
    if holdout_required and not holdout_metrics:
        failures.append("checkpoint is missing final holdout metrics")

    holdout_f1 = checks["holdoutMacroF1"]
    if holdout_required and holdout_f1 is None:
        failures.append("holdout macro_f1 is missing")
    elif holdout_f1 is not None and holdout_f1 < min_holdout_macro_f1:
        failures.append(f"holdout macro_f1 {holdout_f1:.4f} is below {min_holdout_macro_f1:.4f}")

    holdout_ece = checks["holdoutMacroEce"]
    if holdout_required and holdout_ece is None:
        failures.append("holdout macro_ece is missing")
    elif holdout_ece is not None and holdout_ece > max_holdout_macro_ece:
        failures.append(f"holdout macro_ece {holdout_ece:.4f} is above {max_holdout_macro_ece:.4f}")

    calibration_f1 = checks["calibrationMacroF1"]
    if min_calibration_macro_f1 > 0.0 and calibration_f1 is None:
        failures.append("calibration macro_f1 is missing")
    elif calibration_f1 is not None and calibration_f1 < min_calibration_macro_f1:
        failures.append(f"calibration macro_f1 {calibration_f1:.4f} is below {min_calibration_macro_f1:.4f}")

    calibration_ece = checks["calibrationMacroEce"]
    if max_calibration_macro_ece < 1.0 and calibration_ece is None:
        failures.append("calibration macro_ece is missing")
    elif calibration_ece is not None and calibration_ece > max_calibration_macro_ece:
        failures.append(f"calibration macro_ece {calibration_ece:.4f} is above {max_calibration_macro_ece:.4f}")

    return QualityGateResult(passed=not failures, failures=failures, checks=checks)


def bool_from_env(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "y", "on"}


def create_app(
    checkpoint_path: Path,
    device_name: str,
    max_batch_images: int,
    max_batch_wait_ms: float,
    require_holdout_metrics: bool = False,
    min_holdout_macro_f1: float = 0.0,
    max_holdout_macro_ece: float = 1.0,
    min_calibration_macro_f1: float = 0.0,
    max_calibration_macro_ece: float = 1.0,
    require_temperature_scaling: bool = False,
) -> FastAPI:
    checkpoint = torch.load(checkpoint_path, map_location="cpu")
    tasks = checkpoint.get("tasks", DEFAULT_TASKS)
    thresholds = checkpoint.get("thresholds", {})
    logit_temperatures = checkpoint.get("logit_temperatures") or {}
    metrics = checkpoint.get("metrics") or {}
    holdout_metrics = checkpoint.get("holdout_metrics") or {}
    dataset_summary = checkpoint.get("dataset_summary") or {}
    model_name = checkpoint.get("model_name", "convnext_tiny")
    image_size = int(checkpoint.get("image_size", 224))
    quality_gate = validate_checkpoint_quality(
        checkpoint,
        require_holdout_metrics=require_holdout_metrics,
        min_holdout_macro_f1=min_holdout_macro_f1,
        max_holdout_macro_ece=max_holdout_macro_ece,
        min_calibration_macro_f1=min_calibration_macro_f1,
        max_calibration_macro_ece=max_calibration_macro_ece,
        require_temperature_scaling=require_temperature_scaling,
    )
    if not quality_gate.passed:
        raise RuntimeError("Checkpoint failed accessibility vision quality gate: " + "; ".join(quality_gate.failures))

    device = torch.device(device_name if device_name != "auto" else ("cuda" if torch.cuda.is_available() else "cpu"))
    model = build_model(model_name, len(tasks))
    model.load_state_dict(checkpoint["model_state"])
    model.to(device)
    model.eval()
    temperature_tensor = None
    if logit_temperatures:
        temperature_values = [max(float(logit_temperatures.get(task, 1.0)), 1e-6) for task in tasks]
        temperature_tensor = torch.tensor(temperature_values, dtype=torch.float32, device=device).view(1, -1)
    predictor = MicroBatchPredictor(model, device, temperature_tensor, max_batch_images, max_batch_wait_ms)

    transform = transforms.Compose(
        [
            transforms.Resize((image_size, image_size)),
            transforms.ToTensor(),
            transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
        ]
    )

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        try:
            yield
        finally:
            predictor.close()

    app = FastAPI(title="AccessCity Accessibility Vision", version="1.0", lifespan=lifespan)

    @app.get("/health")
    def health() -> dict[str, Any]:
        return {
            "status": "ok",
            "model": model_name,
            "tasks": tasks,
            "device": str(device),
            "thresholds": thresholds,
            "temperatureScaled": bool(logit_temperatures),
            "microBatching": predictor.snapshot_stats(),
            "logitTemperatures": {
                task: round(float(logit_temperatures.get(task, 1.0)), 4)
                for task in tasks
            },
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
            "qualityGate": quality_gate.as_dict(),
            "datasetRows": {
                key: value.get("rows") if isinstance(value, dict) else None
                for key, value in dataset_summary.items()
            },
        }

    @app.post("/v1/accessibility-vision/analyze")
    async def analyze(request: AnalyzeRequest) -> dict[str, Any]:
        if not request.photos:
            raise HTTPException(status_code=400, detail="At least one photo is required.")

        started = time.perf_counter()
        try:
            tensors = await asyncio.to_thread(load_and_transform_photos, request.photos, transform)
            prediction = await asyncio.wrap_future(predictor.submit(tensors))
        except Exception as exc:  # noqa: BLE001
            raise HTTPException(status_code=400, detail=f"Could not analyze photo: {exc}") from exc
        max_probs = prediction["probabilities"]
        candidates = task_candidates(tasks, max_probs, thresholds, request.threshold_floor)
        latency_ms = (time.perf_counter() - started) * 1000
        return {
            "model": model_name,
            "tasks": tasks,
            "thresholds": {task: round(float(thresholds.get(task, 0.5)), 4) for task in tasks},
            "temperatureScaled": bool(logit_temperatures),
            "probabilities": {task: round(float(prob), 4) for task, prob in zip(tasks, max_probs, strict=True)},
            "candidates": candidates,
            "forRouteDecision": False,
            "latencyMs": round(latency_ms, 3),
            "queueWaitMs": round(float(prediction["queueWaitMs"]), 3),
            "inferenceLatencyMs": round(float(prediction["inferenceLatencyMs"]), 3),
            "batchImages": int(prediction["batchImages"]),
            "batchRequests": int(prediction["batchRequests"]),
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
    parser.add_argument("--max-batch-images", type=int, default=int(os.getenv("VISION_MAX_BATCH_IMAGES", "32")))
    parser.add_argument("--max-batch-wait-ms", type=float, default=float(os.getenv("VISION_MAX_BATCH_WAIT_MS", "1")))
    parser.add_argument(
        "--require-holdout-metrics",
        action=argparse.BooleanOptionalAction,
        default=bool_from_env("VISION_REQUIRE_HOLDOUT_METRICS", False),
    )
    parser.add_argument("--min-holdout-macro-f1", type=float, default=float(os.getenv("VISION_MIN_HOLDOUT_MACRO_F1", "0")))
    parser.add_argument("--max-holdout-macro-ece", type=float, default=float(os.getenv("VISION_MAX_HOLDOUT_MACRO_ECE", "1")))
    parser.add_argument("--min-calibration-macro-f1", type=float, default=float(os.getenv("VISION_MIN_CALIBRATION_MACRO_F1", "0")))
    parser.add_argument("--max-calibration-macro-ece", type=float, default=float(os.getenv("VISION_MAX_CALIBRATION_MACRO_ECE", "1")))
    parser.add_argument(
        "--require-temperature-scaling",
        action=argparse.BooleanOptionalAction,
        default=bool_from_env("VISION_REQUIRE_TEMPERATURE_SCALING", False),
    )
    args = parser.parse_args()

    app = create_app(
        args.checkpoint,
        args.device,
        args.max_batch_images,
        args.max_batch_wait_ms,
        require_holdout_metrics=args.require_holdout_metrics,
        min_holdout_macro_f1=args.min_holdout_macro_f1,
        max_holdout_macro_ece=args.max_holdout_macro_ece,
        min_calibration_macro_f1=args.min_calibration_macro_f1,
        max_calibration_macro_ece=args.max_calibration_macro_ece,
        require_temperature_scaling=args.require_temperature_scaling,
    )
    uvicorn.run(app, host=args.host, port=args.port)


if __name__ == "__main__":
    main()
