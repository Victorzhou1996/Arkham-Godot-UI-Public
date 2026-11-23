#!/usr/bin/env bash
cd /opt/tts/GPT-SoVITS
ls GPT_SoVITS/prepare_datasets
echo "== env vars used"; grep -h -o "os.environ\[\"[a-zA-Z_]*\"\]\|os.environ.get(\"[a-zA-Z_]*\"" GPT_SoVITS/prepare_datasets/*.py | sort | uniq -c
echo "== s1.yaml key fields"; grep -n "batch_size\|epochs\|save_every\|exp_name\|pretrained_s1\|max_sec\|precision" GPT_SoVITS/configs/s1.yaml | head -12
echo "== s2.json batch"; grep -n "batch_size\|epochs\|save_every_epoch\|pretrained_s" GPT_SoVITS/configs/s2.json | head -8
