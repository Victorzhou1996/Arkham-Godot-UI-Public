#!/usr/bin/env bash
# CosyVoice setup inside the dedicated WSL2 distro "CosyVoice-TTS" (noncore line, 2026-11-19).
# Run: wsl.exe -d CosyVoice-TTS -u root -- bash "/mnt/g/Python Project/Arkham UI/tools/tts/setup-cosyvoice-wsl.sh"
# Everything lives under /opt/tts inside the distro; nothing on Windows is touched.
# Python env: Miniforge (conda-forge only, no Anaconda default channels / ToS); code: official FunAudioLLM/CosyVoice;
# weights: official ModelScope repos.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
ROOT=/opt/tts
mkdir -p "$ROOT"; cd "$ROOT"
step() { echo; echo "=== $(date '+%H:%M:%S') $*"; }

step "apt packages"
apt-get update -qq
apt-get install -y -qq git git-lfs build-essential sox libsox-dev ffmpeg curl ca-certificates >/dev/null

step "Miniforge"
if [ ! -x "$ROOT/miniforge3/bin/conda" ]; then
  curl -fsSL -o miniforge.sh https://github.com/conda-forge/miniforge/releases/latest/download/Miniforge3-Linux-x86_64.sh
  bash miniforge.sh -b -p "$ROOT/miniforge3"
fi
source "$ROOT/miniforge3/etc/profile.d/conda.sh"

step "conda env cosyvoice (python 3.10)"
conda env list | grep -q '^cosyvoice ' || conda create -y -n cosyvoice python=3.10
conda activate cosyvoice
conda install -y -c conda-forge pynini==2.1.5

step "CosyVoice code"
if [ ! -d "$ROOT/CosyVoice/.git" ]; then git clone --recursive https://github.com/FunAudioLLM/CosyVoice.git "$ROOT/CosyVoice"; fi
cd "$ROOT/CosyVoice"; git submodule update --init --recursive
git log -1 --format='commit %H %cd'

step "pip requirements"
# openai-whisper 20231117 still imports pkg_resources at build time (gone from new setuptools): old setuptools, no isolation
pip install "setuptools<70" wheel
pip install --no-build-isolation openai-whisper==20231117
pip install -r requirements.txt
# deepspeed is for training only; importing it needs a full CUDA toolkit (CUDA_HOME) and breaks transformers/modelscope imports
pip uninstall -y deepspeed
python -c "import torch;print('torch',torch.__version__,'cuda',torch.cuda.is_available(),torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')"

step "models (ModelScope)"
pip install -q modelscope
python - <<'EOF'
from modelscope import snapshot_download
for repo, local in [("iic/CosyVoice2-0.5B", "pretrained_models/CosyVoice2-0.5B"),
                    ("FunAudioLLM/Fun-CosyVoice3-0.5B-2512", "pretrained_models/Fun-CosyVoice3-0.5B")]:
    try:
        print("download", repo, "->", snapshot_download(repo, local_dir=local))
    except Exception as e:
        print("FAILED", repo, e)
EOF
du -sh pretrained_models/* || true
step "done"
