#!/usr/bin/env bash
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
python - <<'PY' 2>&1 | grep -v "^  File\|^    " | tail -12
import torchaudio
PY
echo "== ffmpeg libs in env"; ls $CONDA_PREFIX/lib | grep -E "^libavutil|^libavcodec|^libavformat|^libtorchaudio" | head
ffmpeg -version 2>/dev/null | head -1
echo "== torchaudio lib deps"; ldd $CONDA_PREFIX/lib/python3.10/site-packages/torchaudio/lib/libtorchaudio.so 2>/dev/null | grep -i "not found" | head
ldd $CONDA_PREFIX/lib/python3.10/site-packages/torchaudio/lib/_torchaudio.so 2>/dev/null | grep -i "not found" | head
