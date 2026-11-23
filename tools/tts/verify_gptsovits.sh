#!/usr/bin/env bash
source /opt/tts/miniforge3/etc/profile.d/conda.sh
conda activate gptsovits
cd /opt/tts/GPT-SoVITS
python -c "import torch;print('torch',torch.__version__,'cuda',torch.cuda.is_available(),torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')"
echo "== pretrained"; du -sh GPT_SoVITS/pretrained_models 2>/dev/null; ls GPT_SoVITS/pretrained_models | head -40
echo "== config"; sed -n 1,40p GPT_SoVITS/configs/tts_infer.yaml | grep -n "version\|t2s_weights_path\|vits_weights_path\|bert_base_path\|cnhuhbert" | head
# start API in background
nohup python api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS/configs/tts_infer.yaml > /opt/tts/gptsovits-api.log 2>&1 &
echo "api pid $!"
for i in $(seq 1 60); do sleep 5; if curl -s -m 3 http://127.0.0.1:9880/docs -o /dev/null -w "%{http_code}" | grep -q 200; then echo "api up after $((i*5))s"; break; fi; done
REF=/opt/tts/CosyVoice/asset/zero_shot_prompt.wav
ffprobe -v error -show_entries format=duration -of csv=p=0 "$REF"
curl -s -m 600 -X POST http://127.0.0.1:9880/tts -H 'Content-Type: application/json' -d "{\"text\":\"这是一段测试语音，用来检查安装是否成功。\",\"text_lang\":\"zh\",\"ref_audio_path\":\"$REF\",\"prompt_text\":\"希望你以后能够做的比我还好呦。\",\"prompt_lang\":\"zh\",\"media_type\":\"wav\"}" -o /opt/tts/gptsovits-test.wav -w "http=%{http_code} time=%{time_total}s\n"
ls -la /opt/tts/gptsovits-test.wav; tail -5 /opt/tts/gptsovits-api.log
