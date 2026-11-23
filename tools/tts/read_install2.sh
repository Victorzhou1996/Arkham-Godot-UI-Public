#!/usr/bin/env bash
cd /opt/tts/GPT-SoVITS
for f in pretrained_models.zip G2PWModel.zip nltk_data.zip open_jtalk_dic_utf_8-1.11.tar.gz; do
  s=$(curl -sIL -m 60 "https://hf-mirror.com/XXXXRT/GPT-SoVITS-Pretrained/resolve/main/$f" | grep -i "^content-length" | tail -1 | tr -d '\r'); echo "$f $s"; done
echo "== install.sh 150-235"; sed -n 150,235p install.sh
echo "== install.sh 290-420 (grep conda/pip/python)"; grep -n "conda \|pip install\|PY_PREFIX=\|python" install.sh | head -40
