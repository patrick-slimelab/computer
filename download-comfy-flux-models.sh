#!/usr/bin/env bash
set -euo pipefail

VOLUME_NAME="computer_comfy_models"

fetch() {
  local url="$1"
  local out="$2"
  echo "==> Downloading $out"
  docker run --rm -v "${VOLUME_NAME}:/models" curlimages/curl:8.12.1 \
    -fL --retry 5 --retry-delay 5 "$url" -o "/models/$out"
}

echo "Preparing model folders in volume: ${VOLUME_NAME}"
docker run --rm -v "${VOLUME_NAME}:/models" alpine:3.20 sh -lc 'mkdir -p /models/unet /models/clip /models/vae'

fetch "https://huggingface.co/Comfy-Org/flux1-schnell/resolve/main/flux1-schnell-fp8.safetensors" "unet/flux1-schnell-fp8.safetensors"
fetch "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/clip_l.safetensors" "clip/clip_l.safetensors"
fetch "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/t5xxl_fp16.safetensors" "clip/t5xxl_fp16.safetensors"
fetch "https://huggingface.co/Kijai/flux-fp8/resolve/main/flux-vae-bf16.safetensors" "vae/flux-vae-bf16.safetensors"

echo "Done."
