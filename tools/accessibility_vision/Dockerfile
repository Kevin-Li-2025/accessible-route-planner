FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements-l20.txt /app/requirements-l20.txt
RUN python3 -m pip install --upgrade pip \
    && python3 -m pip install -r /app/requirements-l20.txt

COPY serve_accessibility_vision.py /app/serve_accessibility_vision.py

RUN useradd --create-home --uid 10001 accesscity \
    && chown -R accesscity:accesscity /app

USER accesscity

ENV VISION_MODEL_CHECKPOINT=/models/accesscity-vision/best.pt \
    VISION_HOST=0.0.0.0 \
    VISION_PORT=8095 \
    VISION_DEVICE=auto

EXPOSE 8095

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${VISION_PORT}/health" >/dev/null || exit 1

CMD ["sh", "-c", "python3 /app/serve_accessibility_vision.py --checkpoint \"$VISION_MODEL_CHECKPOINT\" --host \"$VISION_HOST\" --port \"$VISION_PORT\" --device \"$VISION_DEVICE\""]
