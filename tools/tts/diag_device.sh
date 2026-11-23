#!/usr/bin/env bash
cd /opt/tts/GPT-SoVITS
grep -n "print(.*False\|is_half\b" api_v2.py | head -5
grep -rn "self.t2s_model\b.*to(\|\.to(self.configs.device)\|self.configs.device" GPT_SoVITS/TTS_infer_pack/TTS.py | head -8
# quick GPU probe while nothing else runs
source /opt/tts/miniforge3/etc/profile.d/conda.sh; conda activate gptsovits
python -c "import torch;x=torch.randn(4096,4096,device='cuda',dtype=torch.float16);torch.cuda.synchronize();import time;t=time.time();[x@x for _ in range(50)];torch.cuda.synchronize();print('fp16 matmul x50 on cuda:',round(time.time()-t,3),'s')"
