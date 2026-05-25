#!/usr/bin/env bash
set -euo pipefail

: "${ACCESSCITY_L20_HOST:?Set ACCESSCITY_L20_HOST to the GPU host.}"
: "${ACCESSCITY_L20_USER:?Set ACCESSCITY_L20_USER to the GPU username.}"

ACCESSCITY_L20_PORT="${ACCESSCITY_L20_PORT:-22}"
REMOTE_DIR="${ACCESSCITY_L20_REMOTE_DIR:-~/accesscity-vision}"
DATASET_DIR="${ACCESSCITY_VISION_DATASET_DIR:-data/projectsidewalk-rampnet-balanced}"
RUN_DIR="${ACCESSCITY_VISION_RUN_DIR:-runs/project-sidewalk-convnext-small-v2}"
MODEL="${ACCESSCITY_VISION_MODEL:-convnext_small}"
TRAIN_PER_TASK="${ACCESSCITY_VISION_TRAIN_PER_TASK:-1000}"
VALIDATION_PER_TASK="${ACCESSCITY_VISION_VALIDATION_PER_TASK:-200}"
TEST_PER_TASK="${ACCESSCITY_VISION_TEST_PER_TASK:-200}"
TRAIN_PER_TASK_OVERRIDES="${ACCESSCITY_VISION_TRAIN_PER_TASK_OVERRIDES:-}"
VALIDATION_PER_TASK_OVERRIDES="${ACCESSCITY_VISION_VALIDATION_PER_TASK_OVERRIDES:-}"
TEST_PER_TASK_OVERRIDES="${ACCESSCITY_VISION_TEST_PER_TASK_OVERRIDES:-}"
EPOCHS="${ACCESSCITY_VISION_EPOCHS:-18}"
BATCH_SIZE="${ACCESSCITY_VISION_BATCH_SIZE:-48}"
LEARNING_RATE="${ACCESSCITY_VISION_LEARNING_RATE:-8e-5}"
MIN_LEARNING_RATE="${ACCESSCITY_VISION_MIN_LEARNING_RATE:-1e-6}"
IMAGE_SIZE="${ACCESSCITY_VISION_IMAGE_SIZE:-256}"
WARMUP_EPOCHS="${ACCESSCITY_VISION_WARMUP_EPOCHS:-2}"
EMA_DECAY="${ACCESSCITY_VISION_EMA_DECAY:-0.999}"
GRADIENT_CLIP_NORM="${ACCESSCITY_VISION_GRADIENT_CLIP_NORM:-1.0}"
FREEZE_BACKBONE_EPOCHS="${ACCESSCITY_VISION_FREEZE_BACKBONE_EPOCHS:-1}"
EARLY_STOPPING_PATIENCE="${ACCESSCITY_VISION_EARLY_STOPPING_PATIENCE:-5}"
TRAIN_AUGMENTATION="${ACCESSCITY_VISION_TRAIN_AUGMENTATION:-standard}"
HF_DOWNLOAD_MIN_INTERVAL="${ACCESSCITY_VISION_HF_DOWNLOAD_MIN_INTERVAL:-0.08}"
HF_DOWNLOAD_TIMEOUT_SECONDS="${ACCESSCITY_VISION_HF_DOWNLOAD_TIMEOUT_SECONDS:-45}"
VALIDATOR_DOWNLOAD_WORKERS="${ACCESSCITY_VISION_VALIDATOR_DOWNLOAD_WORKERS:-8}"

SSH_TARGET="${ACCESSCITY_L20_USER}@${ACCESSCITY_L20_HOST}"
SSH=(ssh -p "${ACCESSCITY_L20_PORT}" "${SSH_TARGET}")

echo "Syncing accessibility vision tooling to ${SSH_TARGET}:${REMOTE_DIR}"
"${SSH[@]}" "mkdir -p ${REMOTE_DIR}"
rsync -az \
  --exclude data/ \
  --exclude runs/ \
  --exclude models/ \
  --exclude .venv/ \
  --exclude __pycache__/ \
  -e "ssh -p ${ACCESSCITY_L20_PORT}" \
  ./ "${SSH_TARGET}:${REMOTE_DIR}/"

REMOTE_ENV_LINES=()
if [[ -n "${HF_TOKEN:-}" ]]; then
  REMOTE_ENV_LINES+=("export HF_TOKEN=$(printf '%q' "${HF_TOKEN}")")
fi
if [[ -n "${PIP_INDEX_URL:-}" ]]; then
  REMOTE_ENV_LINES+=("export PIP_INDEX_URL=$(printf '%q' "${PIP_INDEX_URL}")")
fi
if [[ -n "${PIP_EXTRA_INDEX_URL:-}" ]]; then
  REMOTE_ENV_LINES+=("export PIP_EXTRA_INDEX_URL=$(printf '%q' "${PIP_EXTRA_INDEX_URL}")")
fi
if [[ "${#REMOTE_ENV_LINES[@]}" -gt 0 ]]; then
  echo "Installing private training environment variables on remote host for this run."
  printf '%s\n' "${REMOTE_ENV_LINES[@]}" | "${SSH[@]}" "umask 077 && mkdir -p ~/.cache/accesscity && cat > ~/.cache/accesscity/vision-training.env"
fi

REMOTE_ENV="{ . ~/.cache/accesscity/vision-training.env 2>/dev/null || true; }"

"${SSH[@]}" "cd ${REMOTE_DIR} && ${REMOTE_ENV} && python3 -m venv .venv && . .venv/bin/activate && python -m pip install --upgrade pip && pip install -r requirements-l20.txt"

EXPORT_ARGS=(
  --output-dir "${DATASET_DIR}"
  --train-per-task "${TRAIN_PER_TASK}"
  --validation-per-task "${VALIDATION_PER_TASK}"
  --test-per-task "${TEST_PER_TASK}"
  --hf-download-min-interval "${HF_DOWNLOAD_MIN_INTERVAL}"
  --hf-download-timeout-seconds "${HF_DOWNLOAD_TIMEOUT_SECONDS}"
  --validator-download-workers "${VALIDATOR_DOWNLOAD_WORKERS}"
  --include-rampnet-crop
  --rampnet-crop-train 500
  --rampnet-crop-validation 100
  --rampnet-crop-test 100
  --include-rampnet-panorama
  --rampnet-panorama-train 500
  --rampnet-panorama-validation 100
  --rampnet-panorama-test 100
)
if [[ -n "${TRAIN_PER_TASK_OVERRIDES}" ]]; then
  EXPORT_ARGS+=(--train-per-task-overrides "${TRAIN_PER_TASK_OVERRIDES}")
fi
if [[ -n "${VALIDATION_PER_TASK_OVERRIDES}" ]]; then
  EXPORT_ARGS+=(--validation-per-task-overrides "${VALIDATION_PER_TASK_OVERRIDES}")
fi
if [[ -n "${TEST_PER_TASK_OVERRIDES}" ]]; then
  EXPORT_ARGS+=(--test-per-task-overrides "${TEST_PER_TASK_OVERRIDES}")
fi
EXPORT_ARGS_REMOTE="$(printf '%q ' "${EXPORT_ARGS[@]}")"

TRAIN_ARGS=(
  --model "${MODEL}"
  --dataset-root "${DATASET_DIR}"
  --output-dir "${RUN_DIR}"
  --epochs "${EPOCHS}"
  --batch-size "${BATCH_SIZE}"
  --learning-rate "${LEARNING_RATE}"
  --min-learning-rate "${MIN_LEARNING_RATE}"
  --image-size "${IMAGE_SIZE}"
  --weight-decay 0.05
  --scheduler cosine
  --warmup-epochs "${WARMUP_EPOCHS}"
  --gradient-clip-norm "${GRADIENT_CLIP_NORM}"
  --ema-decay "${EMA_DECAY}"
  --freeze-backbone-epochs "${FREEZE_BACKBONE_EPOCHS}"
  --early-stopping-patience "${EARLY_STOPPING_PATIENCE}"
  --train-augmentation "${TRAIN_AUGMENTATION}"
  --channels-last
  --task-balanced-loss
  --temperature-scale
  --calibration-split validation
  --holdout-split test
)
TRAIN_ARGS_REMOTE="$(printf '%q ' "${TRAIN_ARGS[@]}")"

"${SSH[@]}" "cd ${REMOTE_DIR} && ${REMOTE_ENV} && . .venv/bin/activate && python export_project_sidewalk_subset.py ${EXPORT_ARGS_REMOTE}"

"${SSH[@]}" "cd ${REMOTE_DIR} && . .venv/bin/activate && python train_accessibility_vision.py ${TRAIN_ARGS_REMOTE}"

"${SSH[@]}" "cd ${REMOTE_DIR} && test -s ${RUN_DIR}/best.pt && test -s ${RUN_DIR}/holdout_metrics.json && test -s ${RUN_DIR}/model_card.md && nvidia-smi --query-gpu=name,memory.total --format=csv,noheader"

echo "Training complete on ${SSH_TARGET}:${REMOTE_DIR}/${RUN_DIR}"
