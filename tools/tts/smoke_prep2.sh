#!/usr/bin/env bash
# Pipeline smoke set (NOT a voice model, NOT user data): 4 clips of our own CosyVoice output with known text, 30-53 s.
set -eo pipefail
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
cd /opt/tts/GPT-SoVITS; export PYTHONPATH=/opt/tts/GPT-SoVITS:/opt/tts/GPT-SoVITS/GPT_SoVITS
EXP=/opt/tts/exp/smoke; P=/mnt/g/Python\ Project/Arkham\ UI/evidence/tts
rm -rf $EXP/wavs $EXP/list.txt $EXP/2-name2text-0.txt $EXP/3-bert $EXP/4-cnhubert $EXP/5-wav32k $EXP/6-name2semantic-0.tsv; mkdir -p $EXP/wavs
i=0
for pair in "2-gathering-intro cv3-plain" "2-gathering-intro cv2-plain" "4-devourer-r3 cv3-plain" "4-devourer-r3 cv2-plain"; do
  set -- $pair; i=$((i+1))
  cp "$P/samples/ref-default/$2/$1.wav" "$EXP/wavs/s$i.wav"
  echo "$EXP/wavs/s$i.wav|smoke|zh|$(cat "$P/text/$1.txt")" >> $EXP/list.txt
done
export exp_name=smoke opt_dir=$EXP inp_text=$EXP/list.txt inp_wav_dir=$EXP/wavs version=v2 is_half=True
export bert_pretrained_dir=GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large
export cnhubert_base_dir=GPT_SoVITS/pretrained_models/chinese-hubert-base
export sv_path=GPT_SoVITS/pretrained_models/sv/pretrained_eres2netv2w24s4ep4.ckpt
export _CUDA_VISIBLE_DEVICES=0 CUDA_VISIBLE_DEVICES=0 i_part=0 all_parts=1
export s2config_path=GPT_SoVITS/configs/s2.json pretrained_s2G=GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth
python GPT_SoVITS/prepare_datasets/1-get-text.py > $EXP/p1.log 2>&1 && echo "step1 ok"
python GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py > $EXP/p2.log 2>&1 && echo "step2 ok"
python GPT_SoVITS/prepare_datasets/3-get-semantic.py > $EXP/p3.log 2>&1 && echo "step3 ok"
wc -l $EXP/2-name2text-0.txt $EXP/6-name2semantic-0.tsv
