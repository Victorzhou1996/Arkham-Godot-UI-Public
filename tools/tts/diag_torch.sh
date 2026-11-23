#!/usr/bin/env bash
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
python -m pip list 2>/dev/null | grep -i "^torch\|^torchaudio\|^torchcodec\|^onnxruntime"
python -c "import torch;print('torch',torch.__version__,'cuda',torch.version.cuda,torch.cuda.is_available())" 2>&1 | tail -1
python -c "import torchaudio" 2>&1 | tail -2
