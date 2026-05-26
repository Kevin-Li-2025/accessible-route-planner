# AccessCity Accessibility Vision Model

This package trains and serves a small review-only sidewalk accessibility vision model.

The first production target is a fast multi-task classifier, not a route-decision model. It predicts calibrated probabilities from a field image and converts them into AccessCity candidate attributes that still require human/admin review before they can update OSM/accessibility profiles.

## Model Choice

Default: `convnext_tiny`. Use `convnext_small` for the promoted balanced model and `convnext_base` for SOTA-chasing L20 accuracy experiments when the weaker hazard heads need more capacity and the deployment can tolerate a larger checkpoint. Do not promote a larger backbone unless untouched holdout F1 and calibration beat the current release; higher validation F1 alone is not enough.

Why this first:

- fast inference on a single L20 and still practical on CPU for batch review;
- easy to calibrate and regression-test;
- lower hallucination risk than a generative VLM for fixed attributes;
- produces probabilities that can be thresholded per attribute.

The model predicts these tasks:

- `curb_ramp_present`
- `curb_ramp_absent`
- `obstacle_present`
- `surface_problem_present`
- `crosswalk_present`

## Data

The trainer uses Project Sidewalk validated image datasets from Hugging Face:

- `projectsidewalk/sidewalk-validator-ai-dataset-curbramp`
- `projectsidewalk/sidewalk-validator-ai-dataset-nocurbramp`
- `projectsidewalk/sidewalk-validator-ai-dataset-obstacle`
- `projectsidewalk/sidewalk-validator-ai-dataset-surfaceproblem`
- `projectsidewalk/sidewalk-validator-ai-dataset-crosswalk`

Labels are treated as partial multi-task labels. For example, a correct `curbramp` sample supervises only `curb_ramp_present`, while a correct `surfaceproblem` sample supervises only `surface_problem_present`.

For a quick first checkpoint when only positive class exports are available, use `--weak-cross-task-negatives`. It treats positive examples from other Project Sidewalk validators as weak negatives for the current head. That is useful for bootstrapping and smoke benchmarking, but the production-quality model should be trained on the balanced validator exports plus RampNet curb-ramp data.

Hugging Face may rate-limit anonymous dataset downloads. Use an authenticated `HF_TOKEN` when exporting larger subsets. The exporter samples validator images from the repository manifest first, then downloads only the selected files with a configurable throttle; this is slower than local datasets but avoids loading every image into memory and is more reliable for repeatable balanced exports:

```bash
export HF_TOKEN=...
python export_project_sidewalk_subset.py \
  --output-dir data/projectsidewalk-rampnet-balanced \
  --train-per-task 1000 \
  --validation-per-task 200 \
  --test-per-task 200 \
  --hf-download-min-interval 0.08 \
  --hf-download-timeout-seconds 45 \
  --validator-download-workers 8 \
  --include-rampnet-crop \
  --rampnet-crop-train 500 \
  --rampnet-crop-validation 100 \
  --rampnet-crop-test 100 \
  --include-rampnet-panorama \
  --rampnet-panorama-train 500 \
  --rampnet-panorama-validation 100 \
  --rampnet-panorama-test 100
```

The exporter writes separate `train.jsonl`, `validation.jsonl`, and `test.jsonl` files. It downloads selected validator images directly from the Hub file URLs so large balanced exports do not fill the Hugging Face cache with unused images; tune `--validator-download-workers`, `--hf-download-min-interval`, and `--hf-download-timeout-seconds` for the network you are using. Use `validation` only for threshold calibration/model selection and reserve `test` for final holdout reporting. `RampNet crop` rows strengthen curb-ramp positives; `RampNet panorama` rows add large-scale curb-ramp positive/negative labels from `curb_ramp_points_normalized`.

If a long export is interrupted, rerun with `--splits train`, `--splits validation`, or `--splits test` to rebuild only the missing split. Skipped split JSONL files are still loaded into `metadata.json`, so the dataset record remains complete.

When one task is underperforming, add more training rows for that head without changing the validation or final holdout distribution:

```bash
python export_project_sidewalk_subset.py \
  --output-dir data/projectsidewalk-rampnet-expanded \
  --train-per-task 1200 \
  --train-per-task-overrides obstacle_present=2200,surface_problem_present=2200,curb_ramp_absent=2000 \
  --validation-per-task 300 \
  --test-per-task 300 \
  --validator-download-workers 8
```

For remote GPU experiments, prefer a compact delta export instead of copying a full city-scale image dataset again. Build the expanded dataset locally once, then export only rows that are absent from the current released training set:

```bash
python build_accessibility_vision_delta_export.py \
  --full-dataset-root data/projectsidewalk-rampnet-expanded \
  --base-dataset-root data/projectsidewalk-rampnet-balanced-v5 \
  --output-dir data/projectsidewalk-rampnet-expanded-delta-v6 \
  --tar-output data/projectsidewalk-rampnet-expanded-delta-v6.tar \
  --splits train \
  --tasks curb_ramp_absent,obstacle_present,surface_problem_present \
  --source-kinds validator \
  --max-image-side 256 \
  --jpeg-quality 76 \
  --overwrite
```

On the GPU host, copy the released base dataset, extract the delta images, and append `train-delta.jsonl` to `train.jsonl`. Keep `validation.jsonl` and `test.jsonl` unchanged so macro F1, ECE, and per-task regressions are comparable across releases.

## L20 Setup

```bash
cd ~/accesscity-vision
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements-l20.txt
```

Smoke run:

```bash
python train_accessibility_vision.py \
  --output-dir runs/smoke \
  --epochs 1 \
  --max-train-per-task 128 \
  --max-eval-per-task 64 \
  --batch-size 16
```

Full calibrated pass:

```bash
python train_accessibility_vision.py \
  --model convnext_tiny \
  --dataset-root data/projectsidewalk-rampnet-balanced \
  --output-dir runs/project-sidewalk-convnext-tiny-v1 \
  --epochs 12 \
  --batch-size 64 \
  --learning-rate 1e-4 \
  --weight-decay 0.05 \
  --task-balanced-loss \
  --temperature-scale \
  --calibration-split validation \
  --holdout-split test
```

Training writes `latest_metrics.json` for the raw calibration split, `calibrated_metrics.json` when `--temperature-scale` is enabled, and `holdout_metrics.json` for the final untouched test split. The checkpoint embeds per-task temperatures, calibrated thresholds, macro F1, Brier score, expected calibration error, and confusion counts. Keep `--task-balanced-loss` enabled when RampNet adds many curb-ramp rows; otherwise the model can over-optimize curb ramps and under-train obstacles, surface problems, and crosswalks.

Training also writes `model_card.md`. Treat that file as the release record for the checkpoint: data split sizes, holdout/calibration metrics, per-task thresholds, temperatures, training arguments, and the review-only guardrails that keep the model out of route decisions.

High-accuracy L20 pass:

```bash
python train_accessibility_vision.py \
  --model convnext_small \
  --dataset-root data/projectsidewalk-rampnet-balanced \
  --output-dir runs/project-sidewalk-convnext-small-v2 \
  --epochs 18 \
  --batch-size 48 \
  --image-size 256 \
  --learning-rate 8e-5 \
  --min-learning-rate 1e-6 \
  --weight-decay 0.05 \
  --scheduler cosine \
  --warmup-epochs 2 \
  --freeze-backbone-epochs 1 \
  --gradient-clip-norm 1.0 \
  --ema-decay 0.999 \
  --early-stopping-patience 5 \
  --train-augmentation standard \
  --channels-last \
  --task-balanced-loss \
  --temperature-scale \
  --calibration-split validation \
  --holdout-split test
```

Use this as the default serious training recipe on the L20. It increases capacity, image resolution, training stability, and calibration discipline while keeping release selection tied to untouched holdout metrics. Use `--train-augmentation strong` only as an ablation; in the current Project Sidewalk + RampNet split it underperformed the standard augmentation recipe on holdout F1. Promote a run only if holdout macro F1 improves without a material macro ECE or weaker-head regression. If the larger checkpoint is too slow for interactive photo review, distill it back into `convnext_tiny` after it becomes the teacher.

For SOTA-chasing ablations, run `--model convnext_base --image-size 320 --batch-size 24 --learning-rate 6e-5`. Treat it as a teacher/backbone experiment until its holdout F1, calibration, and inference latency are measured against the current promoted `convnext_small` release.

Current promoted release pattern:

- dataset: `projectsidewalk-rampnet-best-v7`, with train-only expansion for curb ramps, obstacles, and surface problems;
- checkpoint: `convnext_small`, 18 epoch budget with early stopping at epoch 15, best epoch 10, task-balanced loss, EMA, cosine warmup, temperature scaling;
- calibration: macro F1 `0.8699`, macro ECE `0.0662`;
- untouched test holdout: macro F1 `0.8174`, macro ECE `0.0808`;
- strongest gains over the prior promoted tiny checkpoint: `curb_ramp_present` F1 `0.9077 -> 0.9349`, `crosswalk_present` F1 `0.8920 -> 0.9065`, and macro F1 `0.8021 -> 0.8174`;
- known weak heads to target next: `obstacle_present` F1 `0.7163` and `surface_problem_present` F1 `0.7024`; these need more reviewed city-specific hard examples rather than reusing final holdout rows.

Current highest-accuracy candidate:

- ensemble: promoted `convnext_small` + calibrated `convnext_tiny`, with probabilities averaged and thresholds selected on validation only;
- untouched test holdout: macro F1 `0.8212`, macro ECE `0.0752`;
- L20 smoke benchmark: 120 requests at 16 concurrency, 0 failures, 114.45 req/s, p50 `102.8 ms`, p95 `372.8 ms`, p99 `377.0 ms`;
- use it when the GPU budget can tolerate two forward passes per request. Keep the single `convnext_small` checkpoint as the lower-latency default until the ensemble latency benchmark is acceptable for the review workflow.

Latest SOTA-chasing ablation:

- `convnext_base` at 320 px reached holdout macro F1 `0.8112`, macro ECE `0.0858`;
- it improved some strong heads, but underperformed the promoted single model and the two-model ensemble, so it should stay as a teacher/backbone experiment rather than a production promotion.

Remote L20 runner:

```bash
export ACCESSCITY_L20_HOST=...
export ACCESSCITY_L20_PORT=...
export ACCESSCITY_L20_USER=...
export HF_TOKEN=...
# Optional when the GPU box is slow to reach PyPI or download.pytorch.org:
export PIP_INDEX_URL=...
export PIP_EXTRA_INDEX_URL=...
# Optional class-focused expansion for weaker heads:
export ACCESSCITY_VISION_TRAIN_PER_TASK_OVERRIDES=obstacle_present=2200,surface_problem_present=2200,curb_ramp_absent=2000
./run_l20_training.sh
```

The runner syncs this tool directory to the GPU box, exports a balanced Project Sidewalk + RampNet dataset, trains a calibrated high-accuracy ConvNeXt checkpoint with EMA, cosine warmup, standard augmentation, and untouched holdout evaluation, then verifies that `best.pt`, `holdout_metrics.json`, and `model_card.md` exist. It never stores credentials in the repository.

Hard-example mining for the weaker heads:

```bash
python mine_accessibility_vision_errors.py \
  --checkpoint runs/project-sidewalk-convnext-tiny-v1/best.pt \
  --dataset-root data/projectsidewalk-rampnet-balanced \
  --split test \
  --tasks obstacle_present,surface_problem_present \
  --output-dir runs/project-sidewalk-convnext-tiny-v1/hard-examples/test
```

The miner writes `summary.json`, `predictions.jsonl`, `hard_examples.jsonl`, copied review images, and contact sheets for false positives, false negatives, uncertain cases, and confident wrong predictions. It also reports threshold recommendations for precision targets such as 70%, 80%, and 90%. Use those outputs to drive the next labeling batch; do not train on the final holdout split.

Build a human review queue from mined hard examples:

```bash
python build_accessibility_vision_review_queue.py \
  --hard-examples runs/project-sidewalk-convnext-tiny-v1/hard-examples/production/hard_examples.jsonl \
  --output-dir runs/project-sidewalk-convnext-tiny-v1/review-queue/production \
  --limit-per-task-bucket 100
```

After reviewers fill `reviewed_target` with `0` or `1` and set `review_decision` to a non-empty value such as `corrected`, merge the reviewed labels into a training dataset copy:

```bash
python merge_accessibility_vision_reviews.py \
  --dataset-root data/projectsidewalk-rampnet-balanced \
  --reviews runs/project-sidewalk-convnext-tiny-v1/review-queue/production/review_queue.csv \
  --output-dir data/projectsidewalk-rampnet-balanced-reviewed-v1 \
  --target-split train \
  --sample-weight 3.0
```

The merge tool refuses reviews sourced from `test` or `holdout` by default. This is intentional: final holdout can diagnose weak categories but must not feed training.

Positive-only bootstrap:

```bash
python train_accessibility_vision.py \
  --dataset-root data/projectsidewalk-subset \
  --output-dir runs/projectsidewalk-weak-convnext-tiny-v1 \
  --epochs 8 \
  --batch-size 48 \
  --learning-rate 3e-4 \
  --weight-decay 0.05 \
  --weak-cross-task-negatives
```

Serve:

```bash
python serve_accessibility_vision.py \
  --checkpoint runs/project-sidewalk-convnext-tiny-v1/best.pt \
  --host 0.0.0.0 \
  --port 8095 \
  --max-batch-images 32 \
  --max-batch-wait-ms 1 \
  --require-holdout-metrics \
  --require-temperature-scaling \
  --min-holdout-macro-f1 0.70 \
  --max-holdout-macro-ece 0.12
```

Evaluate and serve an averaged ensemble candidate:

```bash
python evaluate_accessibility_vision_ensemble.py \
  --dataset-root data/projectsidewalk-rampnet-best-v7 \
  --checkpoint models/accesscity-vision-current/best.pt \
  --checkpoint runs/projectsidewalk-rampnet-best-convnext-tiny-v7-ema-20260525T231819Z/best.pt \
  --output-dir runs/projectsidewalk-rampnet-ensemble-current-tiny-v8 \
  --device cuda

python serve_accessibility_vision.py \
  --checkpoint models/accesscity-vision-current/best.pt \
  --checkpoint runs/projectsidewalk-rampnet-best-convnext-tiny-v7-ema-20260525T231819Z/best.pt \
  --ensemble-metrics runs/projectsidewalk-rampnet-ensemble-current-tiny-v8/ensemble_metrics.json \
  --host 0.0.0.0 \
  --port 8095 \
  --require-holdout-metrics \
  --require-temperature-scaling \
  --min-holdout-macro-f1 0.70 \
  --max-holdout-macro-ece 0.12
```

The service exposes:

```http
POST /v1/accessibility-vision/analyze
```

It accepts image URLs or base64 images and returns review-only AccessCity candidates. It never changes route decisions or edge costs. The server uses a small micro-batch queue so concurrent requests can share one GPU forward pass; `/health` reports pending requests, observed batch sizes, and queue wait timing.

The server fails startup when a production quality gate is enabled and the checkpoint is missing holdout metrics, has holdout macro F1 below the configured floor, has macro ECE above the configured ceiling, or lacks temperature scaling when `--require-temperature-scaling` is set. This prevents an uncalibrated smoke checkpoint from being mounted into the production inference service.

Latency benchmark:

```bash
python benchmark_accessibility_vision.py \
  --endpoint http://127.0.0.1:8095/v1/accessibility-vision/analyze \
  --requests 500 \
  --concurrency 32 \
  --photos-per-request 1
```

The benchmark prints throughput, p50/p95/p99 end-to-end latency, model inference latency, queue wait time, health payloads, and sample failures. Run it after every new checkpoint because higher accuracy is not useful if upload review becomes slow enough that users stop submitting useful photos.

## Deployment

The Docker image serves an already-trained checkpoint mounted at `/models/accesscity-vision/best.pt`:

```bash
docker build -t accesscity-accessibility-vision tools/accessibility_vision
docker run --gpus all -p 8095:8095 \
  -v "$PWD/data/accessibility-vision-models:/models/accesscity-vision:ro" \
  accesscity-accessibility-vision
```

For ensemble serving in Docker or Kubernetes, set `VISION_MODEL_CHECKPOINTS` to a comma-separated checkpoint list and set `VISION_ENSEMBLE_METRICS` to the evaluator output JSON mounted alongside the checkpoints.

In Kubernetes, `deploy/kubernetes/vision-deployment.yaml` defines a GPU-backed `accesscity-vision` service. The API config points `AiEnrichment__Provider=local-vision` at `http://accesscity-vision:8095`; the provider fails closed to review-only local rules if the model endpoint is unavailable.
