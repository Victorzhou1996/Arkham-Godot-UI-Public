#!/usr/bin/env bash
cd /opt/tts/GPT-SoVITS
ls GPT_SoVITS | grep -i "train\|get_\|prepare" ; echo "--"; ls GPT_SoVITS/configs | head -30
echo "-- s2 training config"; sed -n 1,30p GPT_SoVITS/configs/s2.json 2>/dev/null | head -5
