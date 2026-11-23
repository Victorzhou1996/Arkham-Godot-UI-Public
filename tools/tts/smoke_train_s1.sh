#!/usr/bin/env bash
# Smoke test: GPT (s1) training for 2 epochs on the one-clip smoke set. Pipeline check only.
set -eo pipefail
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
cd /opt/tts/GPT-SoVITS; export PYTHONPATH=/opt/tts/GPT-SoVITS:/opt/tts/GPT-SoVITS/GPT_SoVITS
EXP=/opt/tts/exp/smoke
python - <<PY
import yaml
c = yaml.safe_load(open("GPT_SoVITS/configs/s1longer-v2.yaml"))
c["pretrained_s1"] = "GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt"
c["train_semantic_path"] = "$EXP/6-name2semantic-0.tsv"
c["train_phoneme_path"] = "$EXP/2-name2text-0.txt"
c["output_dir"] = "$EXP/logs_s1b"
c["train"].update(exp_name="smoke", if_save_latest=True, if_save_every_weights=True,
                  half_weights_save_dir="$EXP/weights_s1b", epochs=2, batch_size=1, gradient_accumulation=1, save_every_n_epoch=1)
yaml.safe_dump(c, open("$EXP/s1.yaml","w"))
print("written")
PY
mkdir -p $EXP/weights_s1b
python GPT_SoVITS/s1_train.py -c $EXP/s1.yaml > $EXP/s1.log 2>&1 || { echo "S1 FAILED"; tail -40 $EXP/s1.log; exit 1; }
echo "S1 OK"; tail -5 $EXP/s1.log; ls -la $EXP/logs_s1 $EXP/weights_s1 2>/dev/null | head -20
