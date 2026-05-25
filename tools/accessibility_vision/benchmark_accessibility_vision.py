#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import io
import json
import statistics
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Benchmark AccessCity accessibility vision inference latency.")
    parser.add_argument("--endpoint", default="http://127.0.0.1:8095/v1/accessibility-vision/analyze")
    parser.add_argument("--health-endpoint", default=None)
    parser.add_argument("--image", type=Path, default=None, help="Optional JPEG/PNG image. A synthetic sidewalk image is used when omitted.")
    parser.add_argument("--requests", type=int, default=100)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--photos-per-request", type=int, default=1)
    parser.add_argument("--threshold-floor", type=float, default=0.35)
    parser.add_argument("--timeout-seconds", type=float, default=10.0)
    return parser.parse_args()


def build_synthetic_image() -> bytes:
    image = Image.new("RGB", (640, 480), (214, 228, 218))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 285, 640, 480), fill=(162, 164, 154))
    draw.polygon([(0, 480), (250, 260), (430, 260), (640, 480)], fill=(188, 187, 176))
    draw.rectangle((332, 305, 410, 356), fill=(210, 196, 164))
    draw.line((250, 260, 0, 480), fill=(230, 229, 220), width=6)
    draw.line((430, 260, 640, 480), fill=(230, 229, 220), width=6)
    draw.rectangle((62, 170, 138, 255), fill=(92, 116, 137))
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=92, optimize=True)
    return buffer.getvalue()


def load_image_bytes(path: Path | None) -> bytes:
    if path is None:
        return build_synthetic_image()
    return path.read_bytes()


def post_json(url: str, payload: dict[str, Any], timeout_seconds: float) -> tuple[int, dict[str, Any] | None, float]:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            raw = response.read()
            elapsed_ms = (time.perf_counter() - started) * 1000
            return response.status, json.loads(raw.decode("utf-8")), elapsed_ms
    except urllib.error.HTTPError as exc:
        elapsed_ms = (time.perf_counter() - started) * 1000
        return exc.code, {"error": exc.read().decode("utf-8", errors="replace")}, elapsed_ms
    except Exception as exc:  # noqa: BLE001
        elapsed_ms = (time.perf_counter() - started) * 1000
        return 0, {"error": str(exc)}, elapsed_ms


def get_json(url: str, timeout_seconds: float) -> dict[str, Any] | None:
    try:
        with urllib.request.urlopen(url, timeout=timeout_seconds) as response:
            return json.loads(response.read().decode("utf-8"))
    except Exception:  # noqa: BLE001
        return None


def percentile(values: list[float], pct: float) -> float:
    if not values:
        return 0.0
    sorted_values = sorted(values)
    rank = (len(sorted_values) - 1) * pct
    lower = int(rank)
    upper = min(lower + 1, len(sorted_values) - 1)
    weight = rank - lower
    return sorted_values[lower] * (1 - weight) + sorted_values[upper] * weight


def main() -> None:
    args = parse_args()
    encoded = base64.b64encode(load_image_bytes(args.image)).decode("ascii")
    photos = [
        {
            "imageBase64": encoded,
            "caption": "Sidewalk accessibility benchmark frame.",
            "source": "benchmark",
        }
        for _ in range(max(1, args.photos_per_request))
    ]
    payload = {
        "thresholdFloor": args.threshold_floor,
        "photos": photos,
    }

    health_endpoint = args.health_endpoint
    if health_endpoint is None and args.endpoint.endswith("/v1/accessibility-vision/analyze"):
        health_endpoint = args.endpoint[: -len("/v1/accessibility-vision/analyze")] + "/health"

    before_health = get_json(health_endpoint, args.timeout_seconds) if health_endpoint else None
    started = time.perf_counter()
    latencies: list[float] = []
    inference_latencies: list[float] = []
    queue_waits: list[float] = []
    failures: list[dict[str, Any]] = []

    with ThreadPoolExecutor(max_workers=max(1, args.concurrency)) as pool:
        futures = [pool.submit(post_json, args.endpoint, payload, args.timeout_seconds) for _ in range(args.requests)]
        for future in as_completed(futures):
            status, parsed, latency_ms = future.result()
            if status == 200 and parsed is not None:
                latencies.append(latency_ms)
                inference_latencies.append(float(parsed.get("inferenceLatencyMs", 0.0)))
                queue_waits.append(float(parsed.get("queueWaitMs", 0.0)))
            else:
                failures.append({"status": status, "response": parsed})

    elapsed_seconds = time.perf_counter() - started
    after_health = get_json(health_endpoint, args.timeout_seconds) if health_endpoint else None
    result = {
        "requests": args.requests,
        "concurrency": args.concurrency,
        "successes": len(latencies),
        "failures": len(failures),
        "elapsedSeconds": round(elapsed_seconds, 3),
        "throughputRps": round(len(latencies) / elapsed_seconds, 3) if elapsed_seconds > 0 else 0.0,
        "latencyMs": {
            "min": round(min(latencies), 3) if latencies else 0.0,
            "mean": round(statistics.fmean(latencies), 3) if latencies else 0.0,
            "p50": round(percentile(latencies, 0.50), 3),
            "p95": round(percentile(latencies, 0.95), 3),
            "p99": round(percentile(latencies, 0.99), 3),
            "max": round(max(latencies), 3) if latencies else 0.0,
        },
        "modelInferenceLatencyMs": {
            "p50": round(percentile(inference_latencies, 0.50), 3),
            "p95": round(percentile(inference_latencies, 0.95), 3),
        },
        "queueWaitMs": {
            "p50": round(percentile(queue_waits, 0.50), 3),
            "p95": round(percentile(queue_waits, 0.95), 3),
        },
        "healthBefore": before_health,
        "healthAfter": after_health,
        "sampleFailure": failures[0] if failures else None,
    }
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
