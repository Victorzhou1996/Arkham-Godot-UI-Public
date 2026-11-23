#!/usr/bin/env bash
REF=/opt/tts/CosyVoice/asset/zero_shot_prompt.wav
curl -s -m 600 -X POST http://127.0.0.1:9880/tts -H 'Content-Type: application/json' -d "{\"text\":\"夜深了，书房里只剩下一盏昏黄的灯。窗外的风吹过老槐树。\",\"text_lang\":\"zh\",\"ref_audio_path\":\"$REF\",\"prompt_text\":\"希望你以后能够做的比我还好呦。\",\"prompt_lang\":\"zh\",\"media_type\":\"wav\"}" -o /opt/tts/prof.wav &
PID=$!
for i in 1 2 3 4 5 6; do sleep 4; echo "t=$((i*4))s gpu: $(nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv,noheader)  cpu: $(top -bn1 | sed -n 3p)"; done
wait $PID; echo done
