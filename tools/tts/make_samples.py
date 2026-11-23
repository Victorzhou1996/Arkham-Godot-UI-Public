# Narration listening samples with CosyVoice 2 / 3 (noncore line, 2026-11-19). Runs inside the CosyVoice-TTS WSL distro:
#   wsl -d CosyVoice-TTS -u root -- bash -lc "source /opt/tts/miniforge3/etc/profile.d/conda.sh && conda activate cosyvoice &&
#     cd /opt/tts/CosyVoice && python '/mnt/g/Python Project/Arkham UI/tools/tts/make_samples.py' [ref.wav 'ref transcript']"
# Texts: evidence/tts/text/*.txt (official Chinese story text, markup stripped). Output: evidence/tts/samples/<variant>/<text>.wav
# and evidence/tts/samples/timing.tsv (seconds of audio vs seconds of compute).
import sys, os, glob, time
sys.path.insert(0, os.getcwd())   # run from /opt/tts/CosyVoice; this script lives in the project
sys.path.append('third_party/Matcha-TTS')
import torch, torchaudio
from cosyvoice.cli.cosyvoice import AutoModel

PROJ = '/mnt/g/Python Project/Arkham UI'
TEXTS = sorted(glob.glob(f'{PROJ}/evidence/tts/text/*.txt'))
OUT = f'{PROJ}/evidence/tts/samples'
REF = sys.argv[1] if len(sys.argv) > 1 else './asset/zero_shot_prompt.wav'
REF_TEXT = sys.argv[2] if len(sys.argv) > 2 else '希望你以后能够做的比我还好呦。'
TAG = sys.argv[3] if len(sys.argv) > 3 else 'ref-default'
SYS = 'You are a helpful assistant.'

def run(model, name, fn):
    os.makedirs(f'{OUT}/{TAG}/{name}', exist_ok=True)
    for t in TEXTS:
        text = open(t, encoding='utf-8').read().strip()
        start = time.time()
        parts = [j['tts_speech'] for j in fn(model, text)]
        wav = torch.cat(parts, dim=1)
        took = time.time() - start
        secs = wav.shape[1] / model.sample_rate
        base = os.path.splitext(os.path.basename(t))[0]
        torchaudio.save(f'{OUT}/{TAG}/{name}/{base}.wav', wav, model.sample_rate)
        line = f'{TAG}\t{name}\t{base}\t{secs:.1f}s audio\t{took:.1f}s compute\tRTF {took / secs:.2f}'
        print(line, flush=True)
        with open(f'{OUT}/timing.tsv', 'a', encoding='utf-8') as f: f.write(line + '\n')

os.makedirs(OUT, exist_ok=True)
cv3 = AutoModel(model_dir='pretrained_models/Fun-CosyVoice3-0.5B')
run(cv3, 'cv3-plain', lambda m, x: m.inference_zero_shot(f'{SYS}<|endofprompt|>' + x, f'{SYS}<|endofprompt|>' + REF_TEXT, REF, stream=False, speed=0.9))
run(cv3, 'cv3-soft', lambda m, x: m.inference_instruct2(x, f'{SYS} Please say a sentence in a very soft voice.<|endofprompt|>', REF, stream=False, speed=0.9))
run(cv3, 'cv3-sad', lambda m, x: m.inference_instruct2(x, f'{SYS} 请非常伤心地说一句话。<|endofprompt|>', REF, stream=False, speed=0.9))
del cv3; torch.cuda.empty_cache()
cv2 = AutoModel(model_dir='pretrained_models/CosyVoice2-0.5B')
run(cv2, 'cv2-plain', lambda m, x: m.inference_zero_shot(x, REF_TEXT, REF, stream=False, speed=0.9))
run(cv2, 'cv2-ominous', lambda m, x: m.inference_instruct2(x, '用低沉、缓慢、带着不安和神秘感的语气讲述<|endofprompt|>', REF, stream=False, speed=0.9))
print('DONE')
