#!/usr/bin/env bash
cd /opt/tts/GPT-SoVITS
grep -n "run_wget_quiet\|hf-mirror\|HF-Mirror\|--source\|--device\|modelscope\|nltk\|UVR5\|G2PW\|pretrained_models\|mkdir" install.sh | head -120
