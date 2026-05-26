# Accessibility Vision Evaluation

AccessCity's vision model is review-only. It can propose accessibility attributes from field photos, but it must not generate routes or dynamically change routing edge costs. A complete evaluation must prove classifier quality, calibration, detection behavior, latency, and data-split discipline before a checkpoint is promoted.

## Evaluation Contract

A release-grade evaluation includes all of the following:

- Classifier holdout metrics on an untouched `test` split: AP, AUROC, F1, precision, recall, threshold, Brier score, ECE, and confusion counts per task.
- Macro quality gates: holdout macro F1 and macro ECE.
- City-shift reporting when the dataset is city-split, kept separate from random holdout numbers.
- RampNet-style detection metrics for curb ramps on panorama/keypoint data: point AP, precision, recall, F1, image-level AP/AUROC/F1/ECE, latency p50/p95/p99, and per-city slices.
- Serving latency against the deployed local model endpoint: throughput, failures, p50/p95/p99, model inference latency, and queue wait.
- A machine-readable `full_evaluation.json` and human-readable `full_evaluation.md`.

## Full Evaluation Runner

Use the unified runner after training or before promoting a mounted model:

```bash
python tools/accessibility_vision/run_full_accessibility_vision_eval.py \
  --output-dir runs/accessibility-vision-full-eval/current \
  --dataset-root data/projectsidewalk-rampnet-best-v7 \
  --checkpoint models/accesscity-vision-current/best.pt \
  --checkpoint runs/projectsidewalk-rampnet-best-convnext-tiny-v7-ema-20260525T231819Z/best.pt \
  --serve-endpoint http://127.0.0.1:8095/v1/accessibility-vision/analyze \
  --rampnet-max-examples 512 \
  --min-holdout-macro-f1 0.70 \
  --max-holdout-macro-ece 0.12 \
  --max-serving-p95-ms 500 \
  --strict
```

The runner delegates to the existing classifier ensemble evaluator, RampNet detection evaluator, and serving latency benchmark. It writes:

```text
runs/accessibility-vision-full-eval/current/full_evaluation.json
runs/accessibility-vision-full-eval/current/full_evaluation.md
runs/accessibility-vision-full-eval/current/classifier/ensemble_metrics.json
runs/accessibility-vision-full-eval/current/rampnet_detection/rampnet_detection_metrics.json
runs/accessibility-vision-full-eval/current/serving_latency/benchmark_accessibility_vision.json
```

## Offline Smoke Check

When the real checkpoint/dataset is unavailable, run an offline smoke check. This verifies the evaluation pipeline and report rendering, but it is not a production accuracy result:

```bash
python tools/accessibility_vision/run_full_accessibility_vision_eval.py \
  --output-dir /tmp/accesscity-vision-full-eval \
  --rampnet-synthetic-smoke
```

Expected gate state for a smoke-only local run:

- `classifier_holdout`: `missing` unless `--dataset-root` and `--checkpoint` are supplied.
- `rampnet_point_detection_ap`: `smoke_only`.
- `serving_latency`: `missing` unless `--serve-endpoint` is supplied.

## Promotion Rules

- Select thresholds on validation only; never tune on the final holdout.
- Do not merge holdout errors back into training. Use holdout only to diagnose future data collection.
- Promote an ensemble only if it improves untouched holdout quality and stays within serving latency budget.
- Keep city-shift performance separate from random split performance. A strong random split does not prove cross-city generalization.
- Treat `obstacle_present` and `surface_problem_present` as the priority weak heads until they are improved with reviewed, city-diverse examples.
- Deploy with `serve_accessibility_vision.py --require-holdout-metrics --require-temperature-scaling` and enforce the same F1/ECE gates used in this report.

## Current Local Validation

On this workstation, the full runner was validated with synthetic RampNet smoke because no real checkpoint or exported Project Sidewalk dataset is checked into the repo. The generated report correctly marks the classifier and serving checks as missing, and marks RampNet as `smoke_only`. That is the desired behavior: the tooling refuses to convert an offline smoke check into a production accuracy claim.

## Current GPU Validation

The latest L20 GPU evaluation artifacts live under `docs/vision-evaluation/latest/`.

- Classifier holdout passed: macro F1 `0.8178`, macro ECE `0.0809`.
- Serving latency passed with zero failed requests: p95 `394.359 ms` on the real RampNet attempt and p95 `117.781 ms` on the smoke run.
- Real RampNet detector evaluation is still missing because the GPU host could not reach Hugging Face and the official RampNet model/data were not cached locally.
- Synthetic RampNet smoke passed, but that is only a pipeline check, not a production accuracy metric.
