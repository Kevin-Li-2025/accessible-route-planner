#!/usr/bin/env python3
from __future__ import annotations

import argparse
import importlib.util
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent
DEFAULT_TASKS = [
    "curb_ramp_present",
    "curb_ramp_absent",
    "obstacle_present",
    "surface_problem_present",
    "crosswalk_present",
]


@dataclass
class StepResult:
    name: str
    status: str
    reason: str | None = None
    command: list[str] | None = None
    output_path: str | None = None
    stdout_path: str | None = None
    stderr_path: str | None = None
    elapsed_seconds: float | None = None
    exit_code: int | None = None

    def to_json(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "status": self.status,
            "reason": self.reason,
            "command": self.command,
            "outputPath": self.output_path,
            "stdoutPath": self.stdout_path,
            "stderrPath": self.stderr_path,
            "elapsedSeconds": self.elapsed_seconds,
            "exitCode": self.exit_code,
        }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Run a complete, auditable AccessCity accessibility-vision evaluation pass. "
            "The runner orchestrates classifier/ensemble holdout evaluation, RampNet-style "
            "detection evaluation, serving latency, release gates, and a Markdown report."
        )
    )
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--dataset-root", type=Path, default=None)
    parser.add_argument("--checkpoint", dest="checkpoints", type=Path, action="append", default=[])
    parser.add_argument("--calibration-split", default="validation")
    parser.add_argument("--holdout-split", default="test")
    parser.add_argument("--batch-size", type=int, default=48)
    parser.add_argument("--num-workers", type=int, default=2)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--weighting", choices=["uniform", "validation"], default="uniform")
    parser.add_argument("--serve-endpoint", default="")
    parser.add_argument("--serve-health-endpoint", default="")
    parser.add_argument("--latency-requests", type=int, default=120)
    parser.add_argument("--latency-concurrency", type=int, default=16)
    parser.add_argument("--latency-image", type=Path, default=None)
    parser.add_argument("--rampnet-dataset", default="projectsidewalk/rampnet-dataset")
    parser.add_argument("--rampnet-model", default="projectsidewalk/rampnet-model")
    parser.add_argument("--rampnet-split", default="validation")
    parser.add_argument("--rampnet-max-examples", type=int, default=256)
    parser.add_argument("--rampnet-include-cities", default="")
    parser.add_argument("--rampnet-exclude-cities", default="")
    parser.add_argument("--rampnet-synthetic-smoke", action="store_true")
    parser.add_argument("--skip-classifier", action="store_true")
    parser.add_argument("--skip-rampnet", action="store_true")
    parser.add_argument("--skip-latency", action="store_true")
    parser.add_argument("--min-holdout-macro-f1", type=float, default=0.70)
    parser.add_argument("--max-holdout-macro-ece", type=float, default=0.12)
    parser.add_argument("--min-rampnet-point-ap", type=float, default=0.10)
    parser.add_argument("--max-serving-p95-ms", type=float, default=500.0)
    parser.add_argument("--strict", action="store_true", help="Exit non-zero when any production gate is missing or fails.")
    return parser.parse_args()


def has_module(name: str) -> bool:
    return importlib.util.find_spec(name) is not None


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def read_json(path: Path) -> dict[str, Any] | None:
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return None


def run_command(name: str, command: list[str], output_dir: Path) -> StepResult:
    output_dir.mkdir(parents=True, exist_ok=True)
    stdout_path = output_dir / f"{name}.stdout.txt"
    stderr_path = output_dir / f"{name}.stderr.txt"
    started = time.perf_counter()
    completed = subprocess.run(
        command,
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    elapsed = time.perf_counter() - started
    stdout_path.write_text(completed.stdout, encoding="utf-8")
    stderr_path.write_text(completed.stderr, encoding="utf-8")
    return StepResult(
        name=name,
        status="passed" if completed.returncode == 0 else "failed",
        command=command,
        stdout_path=str(stdout_path),
        stderr_path=str(stderr_path),
        elapsed_seconds=round(elapsed, 3),
        exit_code=completed.returncode,
    )


def endpoint_is_live(url: str, timeout_seconds: float = 2.0) -> bool:
    if not url:
        return False
    request = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            return 200 <= response.status < 500
    except (urllib.error.URLError, TimeoutError, ValueError):
        return False


def normalize_device(device: str) -> str:
    if device != "auto":
        return device
    return "cuda" if has_module("torch") and _torch_cuda_available() else "cpu"


def _torch_cuda_available() -> bool:
    try:
        import torch

        return bool(torch.cuda.is_available())
    except Exception:
        return False


def run_classifier_eval(args: argparse.Namespace, output_dir: Path) -> StepResult:
    if args.skip_classifier:
        return StepResult("classifier_holdout", "skipped", "Disabled by --skip-classifier.")
    if not args.dataset_root:
        return StepResult("classifier_holdout", "skipped", "No --dataset-root supplied.")
    if not args.checkpoints:
        return StepResult("classifier_holdout", "skipped", "No --checkpoint supplied.")
    if not args.dataset_root.exists():
        return StepResult("classifier_holdout", "skipped", f"Dataset root not found: {args.dataset_root}")
    missing = [str(path) for path in args.checkpoints if not path.exists()]
    if missing:
        return StepResult("classifier_holdout", "skipped", f"Checkpoint file(s) not found: {', '.join(missing)}")
    if not has_module("torchvision"):
        return StepResult("classifier_holdout", "skipped", "torchvision is not installed in this environment.")

    classifier_dir = output_dir / "classifier"
    command = [
        sys.executable,
        str(ROOT / "evaluate_accessibility_vision_ensemble.py"),
        "--dataset-root",
        str(args.dataset_root),
        "--output-dir",
        str(classifier_dir),
        "--calibration-split",
        args.calibration_split,
        "--holdout-split",
        args.holdout_split,
        "--batch-size",
        str(args.batch_size),
        "--num-workers",
        str(args.num_workers),
        "--device",
        normalize_device(args.device),
        "--weighting",
        args.weighting,
    ]
    for checkpoint in args.checkpoints:
        command.extend(["--checkpoint", str(checkpoint)])
    result = run_command("classifier_holdout", command, output_dir / "logs")
    metrics_path = classifier_dir / "ensemble_metrics.json"
    result.output_path = str(metrics_path) if metrics_path.exists() else None
    return result


def run_rampnet_eval(args: argparse.Namespace, output_dir: Path) -> StepResult:
    if args.skip_rampnet:
        return StepResult("rampnet_detection", "skipped", "Disabled by --skip-rampnet.")
    if not has_module("torch"):
        return StepResult("rampnet_detection", "skipped", "torch is not installed in this environment.")
    if not has_module("transformers") and not args.rampnet_synthetic_smoke:
        return StepResult("rampnet_detection", "skipped", "transformers is not installed.")

    rampnet_dir = output_dir / ("rampnet_detection_smoke" if args.rampnet_synthetic_smoke else "rampnet_detection")
    command = [
        sys.executable,
        str(ROOT / "evaluate_rampnet_detection.py"),
        "--dataset",
        args.rampnet_dataset,
        "--model",
        args.rampnet_model,
        "--split",
        args.rampnet_split,
        "--output-dir",
        str(rampnet_dir),
        "--max-examples",
        str(args.rampnet_max_examples),
        "--device",
        normalize_device(args.device),
    ]
    if args.rampnet_include_cities:
        command.extend(["--include-cities", args.rampnet_include_cities])
    if args.rampnet_exclude_cities:
        command.extend(["--exclude-cities", args.rampnet_exclude_cities])
    if args.rampnet_synthetic_smoke:
        command.append("--synthetic-smoke")

    result = run_command("rampnet_detection", command, output_dir / "logs")
    metrics_path = rampnet_dir / "rampnet_detection_metrics.json"
    result.output_path = str(metrics_path) if metrics_path.exists() else None
    if args.rampnet_synthetic_smoke and result.status == "passed":
        result.reason = "Synthetic smoke only; not a production detection metric."
    return result


def run_latency_eval(args: argparse.Namespace, output_dir: Path) -> StepResult:
    if args.skip_latency:
        return StepResult("serving_latency", "skipped", "Disabled by --skip-latency.")
    if not args.serve_endpoint:
        return StepResult("serving_latency", "skipped", "No --serve-endpoint supplied.")
    health_url = args.serve_health_endpoint
    if not health_url and args.serve_endpoint.endswith("/v1/accessibility-vision/analyze"):
        health_url = args.serve_endpoint[: -len("/v1/accessibility-vision/analyze")] + "/health"
    if health_url and not endpoint_is_live(health_url):
        return StepResult("serving_latency", "skipped", f"Health endpoint is not reachable: {health_url}")

    latency_dir = output_dir / "serving_latency"
    latency_dir.mkdir(parents=True, exist_ok=True)
    output_path = latency_dir / "benchmark_accessibility_vision.json"
    command = [
        sys.executable,
        str(ROOT / "benchmark_accessibility_vision.py"),
        "--endpoint",
        args.serve_endpoint,
        "--requests",
        str(args.latency_requests),
        "--concurrency",
        str(args.latency_concurrency),
    ]
    if health_url:
        command.extend(["--health-endpoint", health_url])
    if args.latency_image:
        command.extend(["--image", str(args.latency_image)])
    result = run_command("serving_latency", command, output_dir / "logs")
    stdout = Path(result.stdout_path or "")
    if stdout.exists() and result.status == "passed":
        try:
            parsed = json.loads(stdout.read_text(encoding="utf-8"))
            write_json(output_path, parsed)
            result.output_path = str(output_path)
        except json.JSONDecodeError:
            result.status = "failed"
            result.reason = "Latency benchmark did not emit JSON."
    return result


def load_classifier_metrics(step: StepResult) -> dict[str, Any] | None:
    if not step.output_path:
        return None
    metrics = read_json(Path(step.output_path))
    if not metrics:
        return None
    return metrics.get("holdoutMetrics") or metrics.get("holdout_metrics") or metrics


def load_rampnet_metrics(step: StepResult) -> dict[str, Any] | None:
    if not step.output_path:
        return None
    metrics = read_json(Path(step.output_path))
    if not metrics:
        return None
    return metrics.get("overall") or metrics


def load_latency_metrics(step: StepResult) -> dict[str, Any] | None:
    if not step.output_path:
        return None
    return read_json(Path(step.output_path))


def metric_value(payload: dict[str, Any] | None, path: list[str], default: Any = None) -> Any:
    current: Any = payload
    for key in path:
        if not isinstance(current, dict) or key not in current:
            return default
        current = current[key]
    return current


def gate_result(name: str, status: str, detail: str, value: Any = None, threshold: Any = None) -> dict[str, Any]:
    return {
        "name": name,
        "status": status,
        "value": value,
        "threshold": threshold,
        "detail": detail,
    }


def build_gates(
    args: argparse.Namespace,
    steps: list[StepResult],
    classifier_metrics: dict[str, Any] | None,
    rampnet_metrics: dict[str, Any] | None,
    latency_metrics: dict[str, Any] | None,
) -> list[dict[str, Any]]:
    gates: list[dict[str, Any]] = []
    classifier_step = next(step for step in steps if step.name == "classifier_holdout")
    if classifier_step.status == "passed" and classifier_metrics:
        macro_f1 = metric_value(classifier_metrics, ["macro_f1"])
        macro_ece = metric_value(classifier_metrics, ["macro_ece"])
        gates.append(
            gate_result(
                "classifier_holdout_macro_f1",
                "passed" if macro_f1 is not None and float(macro_f1) >= args.min_holdout_macro_f1 else "failed",
                "Untouched holdout macro F1 must meet the release floor.",
                macro_f1,
                f">= {args.min_holdout_macro_f1}",
            )
        )
        gates.append(
            gate_result(
                "classifier_holdout_macro_ece",
                "passed" if macro_ece is not None and float(macro_ece) <= args.max_holdout_macro_ece else "failed",
                "Calibration error must stay under the release ceiling.",
                macro_ece,
                f"<= {args.max_holdout_macro_ece}",
            )
        )
    else:
        gates.append(
            gate_result(
                "classifier_holdout",
                "missing",
                classifier_step.reason or "Classifier holdout did not run.",
            )
        )

    rampnet_step = next(step for step in steps if step.name == "rampnet_detection")
    if rampnet_step.status == "passed" and rampnet_metrics and not args.rampnet_synthetic_smoke:
        point_ap = metric_value(rampnet_metrics, ["pointDetection", "averagePrecision"])
        gates.append(
            gate_result(
                "rampnet_point_detection_ap",
                "passed" if point_ap is not None and float(point_ap) >= args.min_rampnet_point_ap else "failed",
                "RampNet-style point detection AP must meet the configured floor.",
                point_ap,
                f">= {args.min_rampnet_point_ap}",
            )
        )
    elif rampnet_step.status == "passed" and args.rampnet_synthetic_smoke:
        gates.append(
            gate_result(
                "rampnet_point_detection_ap",
                "smoke_only",
                "Synthetic smoke passed, but no real RampNet holdout was evaluated.",
            )
        )
    else:
        gates.append(
            gate_result(
                "rampnet_point_detection_ap",
                "missing",
                rampnet_step.reason or "RampNet detection evaluation did not run.",
            )
        )

    latency_step = next(step for step in steps if step.name == "serving_latency")
    if latency_step.status == "passed" and latency_metrics:
        p95 = metric_value(latency_metrics, ["latencyMs", "p95"])
        failures = metric_value(latency_metrics, ["failures"], 0)
        gates.append(
            gate_result(
                "serving_latency_p95",
                "passed" if p95 is not None and float(p95) <= args.max_serving_p95_ms else "failed",
                "Interactive review endpoint p95 should stay below the release ceiling.",
                p95,
                f"<= {args.max_serving_p95_ms} ms",
            )
        )
        gates.append(
            gate_result(
                "serving_latency_failures",
                "passed" if int(failures) == 0 else "failed",
                "Serving benchmark should have zero failed requests.",
                failures,
                "== 0",
            )
        )
    else:
        gates.append(
            gate_result(
                "serving_latency",
                "missing",
                latency_step.reason or "Serving latency benchmark did not run.",
            )
        )

    return gates


def summarize_task_table(metrics: dict[str, Any] | None) -> list[dict[str, Any]]:
    if not metrics:
        return []
    tasks = metrics.get("tasks") or {}
    rows = []
    for task in DEFAULT_TASKS:
        value = tasks.get(task)
        if not isinstance(value, dict):
            continue
        rows.append(
            {
                "task": task,
                "count": value.get("count"),
                "positiveRate": value.get("positive_rate"),
                "threshold": value.get("threshold"),
                "averagePrecision": value.get("average_precision"),
                "auroc": value.get("roc_auc"),
                "f1": value.get("f1"),
                "precision": value.get("precision"),
                "recall": value.get("recall"),
                "ece": value.get("ece"),
            }
        )
    return rows


def markdown_metric(value: Any) -> str:
    if value is None:
        return "n/a"
    if isinstance(value, float):
        return f"{value:.4f}"
    return str(value)


def write_markdown_report(
    path: Path,
    args: argparse.Namespace,
    steps: list[StepResult],
    gates: list[dict[str, Any]],
    classifier_metrics: dict[str, Any] | None,
    rampnet_metrics: dict[str, Any] | None,
    latency_metrics: dict[str, Any] | None,
) -> None:
    task_rows = summarize_task_table(classifier_metrics)
    lines = [
        "# AccessCity Accessibility Vision Full Evaluation",
        "",
        "This report is generated by `tools/accessibility_vision/run_full_accessibility_vision_eval.py`.",
        "The vision model is review-only: results must not directly alter route decisions or edge costs.",
        "",
        "## Inputs",
        "",
        f"- Dataset root: `{args.dataset_root}`" if args.dataset_root else "- Dataset root: not supplied",
        f"- Checkpoints: {', '.join(f'`{path}`' for path in args.checkpoints) if args.checkpoints else 'not supplied'}",
        f"- Classifier split: calibration=`{args.calibration_split}`, holdout=`{args.holdout_split}`",
        f"- RampNet split: `{args.rampnet_split}`",
        f"- Serving endpoint: `{args.serve_endpoint}`" if args.serve_endpoint else "- Serving endpoint: not supplied",
        "",
        "## Release Gates",
        "",
        "| Gate | Status | Value | Threshold | Detail |",
        "| --- | --- | ---: | ---: | --- |",
    ]
    for gate in gates:
        lines.append(
            f"| {gate['name']} | {gate['status']} | {markdown_metric(gate.get('value'))} | "
            f"{markdown_metric(gate.get('threshold'))} | {gate['detail']} |"
        )

    lines.extend(["", "## Step Status", "", "| Step | Status | Reason | Output |", "| --- | --- | --- | --- |"])
    for step in steps:
        lines.append(
            f"| {step.name} | {step.status} | {step.reason or ''} | `{step.output_path or ''}` |"
        )

    lines.extend(["", "## Classifier Holdout", ""])
    if classifier_metrics:
        lines.extend(
            [
                f"- Macro F1: `{markdown_metric(classifier_metrics.get('macro_f1'))}`",
                f"- Macro ECE: `{markdown_metric(classifier_metrics.get('macro_ece'))}`",
                "",
                "| Task | Count | AP | AUROC | F1 | Precision | Recall | ECE | Threshold |",
                "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
            ]
        )
        for row in task_rows:
            lines.append(
                f"| {row['task']} | {markdown_metric(row.get('count'))} | "
                f"{markdown_metric(row.get('averagePrecision'))} | {markdown_metric(row.get('auroc'))} | "
                f"{markdown_metric(row.get('f1'))} | {markdown_metric(row.get('precision'))} | "
                f"{markdown_metric(row.get('recall'))} | {markdown_metric(row.get('ece'))} | "
                f"{markdown_metric(row.get('threshold'))} |"
            )
    else:
        lines.append("Classifier holdout was not run. Supply `--dataset-root` and one or more `--checkpoint` files.")

    lines.extend(["", "## RampNet-Style Detection", ""])
    if rampnet_metrics:
        lines.extend(
            [
                f"- Point AP: `{markdown_metric(metric_value(rampnet_metrics, ['pointDetection', 'averagePrecision']))}`",
                f"- Point F1: `{markdown_metric(metric_value(rampnet_metrics, ['pointDetection', 'f1']))}`",
                f"- Image AP: `{markdown_metric(metric_value(rampnet_metrics, ['imageLevel', 'averagePrecision']))}`",
                f"- Image AUROC: `{markdown_metric(metric_value(rampnet_metrics, ['imageLevel', 'auroc']))}`",
                f"- Image F1: `{markdown_metric(metric_value(rampnet_metrics, ['imageLevel', 'f1']))}`",
                f"- Image ECE: `{markdown_metric(metric_value(rampnet_metrics, ['imageLevel', 'ece']))}`",
                f"- Latency p95 ms: `{markdown_metric(metric_value(rampnet_metrics, ['latencyMs', 'p95']))}`",
            ]
        )
        if args.rampnet_synthetic_smoke:
            lines.append("- Note: this was a synthetic smoke run and is not a production accuracy metric.")
    else:
        lines.append("RampNet detection evaluation was not run.")

    lines.extend(["", "## Serving Latency", ""])
    if latency_metrics:
        lines.extend(
            [
                f"- Requests: `{markdown_metric(latency_metrics.get('requests'))}`",
                f"- Concurrency: `{markdown_metric(latency_metrics.get('concurrency'))}`",
                f"- Failures: `{markdown_metric(latency_metrics.get('failures'))}`",
                f"- Throughput RPS: `{markdown_metric(latency_metrics.get('throughputRps'))}`",
                f"- p50 ms: `{markdown_metric(metric_value(latency_metrics, ['latencyMs', 'p50']))}`",
                f"- p95 ms: `{markdown_metric(metric_value(latency_metrics, ['latencyMs', 'p95']))}`",
                f"- p99 ms: `{markdown_metric(metric_value(latency_metrics, ['latencyMs', 'p99']))}`",
            ]
        )
    else:
        lines.append("Serving latency was not run. Start `serve_accessibility_vision.py` and pass `--serve-endpoint`.")

    lines.extend(
        [
            "",
            "## Interpretation",
            "",
            "- Promote a model only from untouched holdout metrics, not validation F1.",
            "- Keep city-shift results separate from random split results.",
            "- Weak heads, especially obstacle and surface-problem classes, should be improved with new reviewed data rather than final holdout reuse.",
            "- This model may propose review candidates; it must not generate routes or dynamically change route edge costs.",
        ]
    )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    args = parse_args()
    args.output_dir = args.output_dir.resolve()
    args.dataset_root = args.dataset_root.resolve() if args.dataset_root else None
    args.checkpoints = [path.resolve() for path in args.checkpoints]
    args.latency_image = args.latency_image.resolve() if args.latency_image else None
    args.output_dir.mkdir(parents=True, exist_ok=True)

    steps = [
        run_classifier_eval(args, args.output_dir),
        run_rampnet_eval(args, args.output_dir),
        run_latency_eval(args, args.output_dir),
    ]

    classifier_metrics = load_classifier_metrics(steps[0])
    rampnet_metrics = load_rampnet_metrics(steps[1])
    latency_metrics = load_latency_metrics(steps[2])
    gates = build_gates(args, steps, classifier_metrics, rampnet_metrics, latency_metrics)

    report = {
        "schemaVersion": "accesscity-accessibility-vision-eval.v1",
        "generatedAtUnix": int(time.time()),
        "inputs": {
            "datasetRoot": str(args.dataset_root) if args.dataset_root else None,
            "checkpoints": [str(path) for path in args.checkpoints],
            "calibrationSplit": args.calibration_split,
            "holdoutSplit": args.holdout_split,
            "rampnetSyntheticSmoke": args.rampnet_synthetic_smoke,
            "serveEndpoint": args.serve_endpoint or None,
        },
        "environment": {
            "python": sys.version.split()[0],
            "torchInstalled": has_module("torch"),
            "torchvisionInstalled": has_module("torchvision"),
            "transformersInstalled": has_module("transformers"),
            "cudaAvailable": _torch_cuda_available(),
        },
        "steps": [step.to_json() for step in steps],
        "gates": gates,
        "classifierTaskMetrics": summarize_task_table(classifier_metrics),
        "classifierSummary": {
            "macroF1": classifier_metrics.get("macro_f1") if classifier_metrics else None,
            "macroEce": classifier_metrics.get("macro_ece") if classifier_metrics else None,
        },
        "rampnetSummary": {
            "pointAveragePrecision": metric_value(rampnet_metrics, ["pointDetection", "averagePrecision"]),
            "pointF1": metric_value(rampnet_metrics, ["pointDetection", "f1"]),
            "imageAveragePrecision": metric_value(rampnet_metrics, ["imageLevel", "averagePrecision"]),
            "imageAuroc": metric_value(rampnet_metrics, ["imageLevel", "auroc"]),
            "imageF1": metric_value(rampnet_metrics, ["imageLevel", "f1"]),
            "imageEce": metric_value(rampnet_metrics, ["imageLevel", "ece"]),
            "latencyP95Ms": metric_value(rampnet_metrics, ["latencyMs", "p95"]),
        },
        "servingLatencySummary": {
            "failures": latency_metrics.get("failures") if latency_metrics else None,
            "throughputRps": latency_metrics.get("throughputRps") if latency_metrics else None,
            "p95Ms": metric_value(latency_metrics, ["latencyMs", "p95"]),
            "p99Ms": metric_value(latency_metrics, ["latencyMs", "p99"]),
        },
    }

    full_json = args.output_dir / "full_evaluation.json"
    full_md = args.output_dir / "full_evaluation.md"
    write_json(full_json, report)
    write_markdown_report(full_md, args, steps, gates, classifier_metrics, rampnet_metrics, latency_metrics)

    print(json.dumps({"report": str(full_json), "markdown": str(full_md), "gates": gates}, indent=2))
    if args.strict and any(gate["status"] != "passed" for gate in gates):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
