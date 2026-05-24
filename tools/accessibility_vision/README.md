# AccessCity Accessibility Vision Model

This package trains and serves a small review-only sidewalk accessibility vision model.

The first production target is a fast multi-task classifier, not a route-decision model. It predicts calibrated probabilities from a field image and converts them into AccessCity candidate attributes that still require human/admin review before they can update OSM/accessibility profiles.

## Model Choice

Default: `convnext_tiny`.

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
  --hf-download-min-interval 0.12 \
  --include-rampnet-crop \
  --rampnet-crop-train 500 \
  --rampnet-crop-validation 100 \
  --rampnet-crop-test 100 \
  --include-rampnet-panorama \
  --rampnet-panorama-train 500 \
  --rampnet-panorama-validation 100 \
  --rampnet-panorama-test 100
```

The export format always writes separate `train.jsonl`, `validation.jsonl`, and `test.jsonl` files. Use `validation` only for threshold calibration/model selection and reserve `test` for final holdout reporting. `RampNet crop` rows strengthen curb-ramp positives; `RampNet panorama` rows add large-scale curb-ramp positive/negative labels from `curb_ramp_points_normalized`.

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
  --max-batch-wait-ms 1
```

The service exposes:

```http
POST /v1/accessibility-vision/analyze
```

It accepts image URLs or base64 images and returns review-only AccessCity candidates. It never changes route decisions or edge costs. The server uses a small micro-batch queue so concurrent requests can share one GPU forward pass; `/health` reports pending requests, observed batch sizes, and queue wait timing.

## Deployment

The Docker image serves an already-trained checkpoint mounted at `/models/accesscity-vision/best.pt`:

```bash
docker build -t accesscity-accessibility-vision tools/accessibility_vision
docker run --gpus all -p 8095:8095 \
  -v "$PWD/data/accessibility-vision-models:/models/accesscity-vision:ro" \
  accesscity-accessibility-vision
```

In Kubernetes, `deploy/kubernetes/vision-deployment.yaml` defines a GPU-backed `accesscity-vision` service. The API config points `AiEnrichment__Provider=local-vision` at `http://accesscity-vision:8095`; the provider fails closed to review-only local rules if the model endpoint is unavailable.
