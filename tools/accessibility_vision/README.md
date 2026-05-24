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

Hugging Face may rate-limit anonymous dataset downloads. Use an authenticated `HF_TOKEN` when exporting larger subsets:

```bash
export HF_TOKEN=...
python export_project_sidewalk_subset.py \
  --output-dir data/projectsidewalk-rampnet-balanced \
  --train-per-task 1200 \
  --validation-per-task 300 \
  --test-per-task 300 \
  --include-rampnet-crop \
  --include-rampnet-panorama \
  --rampnet-panorama-train 5000 \
  --rampnet-panorama-validation 1000 \
  --rampnet-panorama-test 1000
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
  --batch-size 48 \
  --learning-rate 3e-4 \
  --weight-decay 0.05 \
  --calibration-split validation \
  --holdout-split test
```

Training writes `latest_metrics.json` for the calibration split and `holdout_metrics.json` for the final untouched test split. The checkpoint embeds calibrated per-task thresholds, macro F1, Brier score, expected calibration error, and confusion counts.

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
  --port 8095
```

The service exposes:

```http
POST /v1/accessibility-vision/analyze
```

It accepts image URLs or base64 images and returns review-only AccessCity candidates. It never changes route decisions or edge costs.

## Deployment

The Docker image serves an already-trained checkpoint mounted at `/models/accesscity-vision/best.pt`:

```bash
docker build -t accesscity-accessibility-vision tools/accessibility_vision
docker run --gpus all -p 8095:8095 \
  -v "$PWD/data/accessibility-vision-models:/models/accesscity-vision:ro" \
  accesscity-accessibility-vision
```

In Kubernetes, `deploy/kubernetes/vision-deployment.yaml` defines a GPU-backed `accesscity-vision` service. The API config points `AiEnrichment__Provider=local-vision` at `http://accesscity-vision:8095`; the provider fails closed to review-only local rules if the model endpoint is unavailable.
