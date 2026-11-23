#!/usr/bin/env bash
# torchcodec +cu128 needs CUDA NPP libs that are not installed (libnppicc.so.12). GPT-SoVITS only uses it to read audio: CPU build is enough.
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
python -m pip uninstall -y torchcodec >/dev/null 2>&1
python -m pip install --no-deps "torchcodec==0.11.1" --index-url https://download.pytorch.org/whl/cpu 2>&1 | tail -1
python -c "import torchcodec,torchaudio;print('torchcodec',torchcodec.__version__)" 2>&1 | tail -1
