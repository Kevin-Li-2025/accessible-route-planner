#!/usr/bin/env bash
set -euo pipefail

: "${ACCESSCITY_L20_HOST:?Set ACCESSCITY_L20_HOST to the GPU host.}"
: "${ACCESSCITY_L20_USER:?Set ACCESSCITY_L20_USER to the GPU username.}"

ACCESSCITY_L20_PORT="${ACCESSCITY_L20_PORT:-22}"
REMOTE_DIR="${ACCESSCITY_L20_REMOTE_DIR:-~/accesscity-vision}"
DATASET_DIR="${ACCESSCITY_VISION_DATASET_DIR:-data/projectsidewalk-rampnet-balanced}"
RUN_DIR="${ACCESSCITY_VISION_RUN_DIR:-runs/project-sidewalk-convnext-tiny-v1}"
TRAIN_PER_TASK="${ACCESSCITY_VISION_TRAIN_PER_TASK:-1200}"
VALIDATION_PER_TASK="${ACCESSCITY_VISION_VALIDATION_PER_TASK:-300}"
TEST_PER_TASK="${ACCESSCITY_VISION_TEST_PER_TASK:-300}"
EPOCHS="${ACCESSCITY_VISION_EPOCHS:-12}"
BATCH_SIZE="${ACCESSCITY_VISION_BATCH_SIZE:-64}"

SSH_TARGET="${ACCESSCITY_L20_USER}@${ACCESSCITY_L20_HOST}"
SSH=(ssh -p "${ACCESSCITY_L20_PORT}" "${SSH_TARGET}")

echo "Syncing accessibility vision tooling to ${SSH_TARGET}:${REMOTE_DIR}"
"${SSH[@]}" "mkdir -p ${REMOTE_DIR}"
rsync -az --delete -e "ssh -p ${ACCESSCITY_L20_PORT}" ./ "${SSH_TARGET}:${REMOTE_DIR}/"

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

"${SSH[@]}" "cd ${REMOTE_DIR} && ${REMOTE_ENV} && . .venv/bin/activate && python export_project_sidewalk_subset.py \
  --output-dir ${DATASET_DIR} \
  --train-per-task ${TRAIN_PER_TASK} \
  --validation-per-task ${VALIDATION_PER_TASK} \
  --test-per-task ${TEST_PER_TASK} \
  --hf-download-min-interval 0.12 \
  --include-rampnet-crop \
  --rampnet-crop-train 500 \
  --rampnet-crop-validation 100 \
  --rampnet-crop-test 100 \
  --include-rampnet-panorama \
  --rampnet-panorama-train 500 \
  --rampnet-panorama-validation 100 \
  --rampnet-panorama-test 100"

"${SSH[@]}" "cd ${REMOTE_DIR} && . .venv/bin/activate && python train_accessibility_vision.py \
  --dataset-root ${DATASET_DIR} \
  --output-dir ${RUN_DIR} \
  --epochs ${EPOCHS} \
  --batch-size ${BATCH_SIZE} \
  --learning-rate 1e-4 \
  --weight-decay 0.05 \
  --task-balanced-loss \
  --temperature-scale \
  --calibration-split validation \
  --holdout-split test"

"${SSH[@]}" "cd ${REMOTE_DIR} && test -s ${RUN_DIR}/best.pt && test -s ${RUN_DIR}/holdout_metrics.json && test -s ${RUN_DIR}/model_card.md && nvidia-smi --query-gpu=name,memory.total --format=csv,noheader"

echo "Training complete on ${SSH_TARGET}:${REMOTE_DIR}/${RUN_DIR}"
