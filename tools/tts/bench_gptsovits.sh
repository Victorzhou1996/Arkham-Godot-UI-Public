#!/usr/bin/env bash
REF=/opt/tts/CosyVoice/asset/zero_shot_prompt.wav
for n in 1 2; do
  curl -s -m 600 -X POST http://127.0.0.1:9880/tts -H 'Content-Type: application/json' -d "{\"text\":\"夜深了，书房里只剩下一盏昏黄的灯。窗外的风吹过老槐树，沙沙作响，仿佛有人在低声读着一封未寄出的信。\",\"text_lang\":\"zh\",\"ref_audio_path\":\"$REF\",\"prompt_text\":\"希望你以后能够做的比我还好呦。\",\"prompt_lang\":\"zh\",\"media_type\":\"wav\"}" -o /opt/tts/bench$n.wav -w "run$n http=%{http_code} time=%{time_total}s\n"
  echo "audio seconds: $(python3 -c "import wave,sys;w=wave.open('/opt/tts/bench$n.wav');print(round(w.getnframes()/w.getframerate(),2), w.getframerate())")"
done
nvidia-smi --query-gpu=memory.used,utilization.gpu --format=csv,noheader
