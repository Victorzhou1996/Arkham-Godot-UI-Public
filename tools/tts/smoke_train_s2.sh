#!/usr/bin/env bash
# Smoke test: SoVITS (s2) training, 1 epoch, on the 3-clip pipeline smoke set (pipeline check only).
set -eo pipefail
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
cd /opt/tts/GPT-SoVITS; export PYTHONPATH=/opt/tts/GPT-SoVITS:/opt/tts/GPT-SoVITS/GPT_SoVITS
EXP=/opt/tts/exp/smoke
mkdir -p $EXP/logs_s2_v2
cp -f $EXP/2-name2text-0.txt $EXP/2-name2text.txt
python - <<PY
import json
c = json.load(open("GPT_SoVITS/configs/s2.json"))
c["train"].update(batch_size=1, epochs=1, gpu_numbers="0", if_save_latest=False, if_save_every_weights=False,
                  save_every_epoch=1, pretrained_s2G="GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth",
                  pretrained_s2D="GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2D2333k.pth", fp16_run=True)
c["data"]["exp_dir"] = "$EXP"
c["model"]["version"] = "v2"
json.dump(c, open("$EXP/s2.json","w"), indent=1)
print("written")
PY
python GPT_SoVITS/s2_train.py -c $EXP/s2.json > $EXP/s2.log 2>&1 || { echo "S2 FAILED"; grep -a "Error" $EXP/s2.log | tail -5; exit 1; }
echo "S2 OK"; tail -3 $EXP/s2.log | cut -c1-200; find $EXP/logs_s2_v2 -maxdepth 1 -type f | head
