#!/usr/bin/env bash
# Smoke test of GPT-SoVITS data preparation on ONE short clip (pipeline check only; not a voice model).
set -eo pipefail
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
cd /opt/tts/GPT-SoVITS; export PYTHONPATH=/opt/tts/GPT-SoVITS:/opt/tts/GPT-SoVITS/GPT_SoVITS
EXP=/opt/tts/exp/smoke; mkdir -p $EXP/wavs
rm -rf $EXP/wavs $EXP/list.txt; mkdir -p $EXP/wavs
for i in 1 2 3 4; do cp /opt/tts/CosyVoice/asset/zero_shot_prompt.wav $EXP/wavs/p$i.wav; echo "$EXP/wavs/p$i.wav|smoke|zh|希望你以后能够做的比我还好呦。" >> $EXP/list.txt; done
export exp_name=smoke opt_dir=$EXP inp_text=$EXP/list.txt inp_wav_dir=$EXP/wavs version=v2 is_half=True
export bert_pretrained_dir=GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large
export cnhubert_base_dir=GPT_SoVITS/pretrained_models/chinese-hubert-base
export sv_path=GPT_SoVITS/pretrained_models/sv/pretrained_eres2netv2w24s4ep4.ckpt
export _CUDA_VISIBLE_DEVICES=0 CUDA_VISIBLE_DEVICES=0 i_part=0 all_parts=1
echo "== step 1 text"; python GPT_SoVITS/prepare_datasets/1-get-text.py 2>&1 | tail -5
echo "== step 2 hubert"; python GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py 2>&1 | tail -5
echo "== step 3 semantic"; export s2config_path=GPT_SoVITS/configs/s2.json pretrained_s2G=GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth; python GPT_SoVITS/prepare_datasets/3-get-semantic.py 2>&1 | tail -5
ls -la $EXP $EXP/* | head -40
