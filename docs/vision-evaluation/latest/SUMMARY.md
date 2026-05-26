# Latest Accessibility Vision Evaluation

Generated on 2026-05-26 using the remote L20 GPU host.

## What Ran

- Runtime: Python 3.12.3, PyTorch CUDA available, NVIDIA L20.
- Dataset: `projectsidewalk-rampnet-best-v7`, validation calibration split and untouched test holdout.
- Classifier ensemble checkpoints:
  - `accesscity-vision-convnext-small-v7`
  - `projectsidewalk-rampnet-best-convnext-tiny-v7-ema`
  - `projectsidewalk-rampnet-sota-convnext-base-v8`
- Serving test: local `serve_accessibility_vision.py` endpoint, 160 requests, concurrency 16.
- Detection test: real RampNet attempt plus synthetic detector smoke.

## Results

| Area | Result |
| --- | ---: |
| Holdout macro F1 | 0.8178 |
| Holdout macro ECE | 0.0809 |
| Serving failures | 0 |
| Real-attempt serving p95 | 394.359 ms |
| Smoke-run serving p95 | 117.781 ms |
| Smoke-run throughput | 171.916 rps |

## Per-Task Holdout

| Task | AP | AUROC | F1 | Precision | Recall | ECE |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| curb_ramp_present | 0.9784 | 0.9668 | 0.9352 | 0.9189 | 0.9520 | 0.0319 |
| curb_ramp_absent | 0.8963 | 0.9054 | 0.8152 | 0.7748 | 0.8600 | 0.0809 |
| obstacle_present | 0.7699 | 0.7794 | 0.7328 | 0.6439 | 0.8500 | 0.0993 |
| surface_problem_present | 0.7712 | 0.7821 | 0.7010 | 0.7234 | 0.6800 | 0.1297 |
| crosswalk_present | 0.9603 | 0.9601 | 0.9048 | 0.8636 | 0.9500 | 0.0627 |

## Interpretation

The classifier is useful enough for review-queue ranking and admin assist flows, especially curb-ramp and crosswalk tasks. It should not directly update route edge costs. The weak heads remain `obstacle_present` and `surface_problem_present`; they need more reviewed, city-diverse data and a detector/segmentation path rather than only crop classification.

The real RampNet detector was not fully evaluated on this run because the GPU host could not reach Hugging Face and the official RampNet model/data were not present in the local cache. The smoke detector run passed, but it is only a pipeline check and must not be reported as production accuracy.

## Artifacts

- `full_evaluation_real_attempt.json`
- `full_evaluation_real_attempt.md`
- `full_evaluation_smoke.json`
- `full_evaluation_smoke.md`
