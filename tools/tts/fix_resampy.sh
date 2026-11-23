#!/usr/bin/env bash
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
python -m pip install resampy 2>&1 | tail -1
python -c "import resampy;print('resampy ok', resampy.__version__)"
