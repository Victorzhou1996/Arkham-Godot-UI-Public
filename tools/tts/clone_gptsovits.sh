#!/usr/bin/env bash
set -e
cd /opt/tts
[ -d GPT-SoVITS/.git ] || git clone --depth 1 https://github.com/RVC-Boss/GPT-SoVITS.git
cd GPT-SoVITS && git log -1 --format='commit %H %cd'
ls
echo "== install.sh (head)"; sed -n 1,60p install.sh
echo "== requirements"; cat requirements.txt | head -80
