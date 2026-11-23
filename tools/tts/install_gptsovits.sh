#!/usr/bin/env bash
# GPT-SoVITS install inside the CosyVoice-TTS WSL distro (noncore line, 2026-11-19). Separate conda env "gptsovits".
# Uses the official install.sh: --device CU128 (RTX 4080), --source HF-Mirror (reachable from here; HF direct is also reachable).
set -eo pipefail
source /opt/tts/miniforge3/etc/profile.d/conda.sh
conda env list | grep -q '^gptsovits ' || conda create -y -n gptsovits python=3.10
conda activate gptsovits
cd /opt/tts/GPT-SoVITS
bash install.sh --device CU128 --source HF-Mirror
echo "INSTALL_EXIT=$?"
