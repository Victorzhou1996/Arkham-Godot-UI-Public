#!/usr/bin/env bash
# Transcribe a few 40 s excerpts (read-only on the source) to see what the corpus is.
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
D=/mnt/c/Users/victo/Desktop/Test; W=/opt/tts/corpus-peek; mkdir -p $W
for spec in "0041 0" "0041 400" "0048 300" "0055 600"; do set -- $spec
  ffmpeg -loglevel error -y -ss $2 -t 40 -i "$D/$1.mp3" -ac 1 -ar 16000 "$W/$1-$2.wav"; done
python - <<'PY' 2>&1 | grep -v "^\s*$" | tail -20
import glob
from funasr import AutoModel
m = AutoModel(model="iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-pytorch", vad_model="fsmn-vad", punc_model="ct-punc", disable_update=True, disable_pbar=True)
for f in sorted(glob.glob('/opt/tts/corpus-peek/*.wav')):
    r = m.generate(input=f)
    print("PEEK", f.split('/')[-1], r[0]['text'][:220])
PY
echo "== loudness / music check"
for f in $W/*.wav; do echo "$(basename $f) $(ffmpeg -hide_banner -i $f -af astats=metadata=0,silencedetect=n=-40dB:d=0.4 -f null - 2>&1 | grep -E 'RMS level dB|Noise floor dB|silence_end' | head -4 | tr -s ' ' | tr '\n' ' ')"; done
