#!/usr/bin/env bash
# torchaudio from PyPI is a CUDA 13 build (libcudart.so.13) while torch is +cu128: take the matching cu128 wheel, no deps.
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
python -m pip install --no-deps --force-reinstall "torchaudio==2.11.0" --index-url https://download.pytorch.org/whl/cu128 2>&1 | tail -2
python -c "import torch,torchaudio;print('torch',torch.__version__,torch.cuda.is_available(),'torchaudio',torchaudio.__version__);import torchaudio.functional as F;print('ok')" 2>&1 | tail -3
