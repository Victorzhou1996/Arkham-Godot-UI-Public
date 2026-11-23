#!/usr/bin/env bash
cd /opt/tts/GPT-SoVITS
grep -n -i "parallel\|batch\|split\|bucket\|top_k\|early\|sample" GPT_SoVITS/configs/tts_infer.yaml | head -20
echo "== api log timing lines"; grep -a -B1 -A1 "^[0-9.]*	[0-9.]*	[0-9.]*	[0-9.]*$" /opt/tts/gptsovits-api.log | tail -6
