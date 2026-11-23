#!/usr/bin/env bash
echo "== GitHub repo"; curl -s -m 30 https://api.github.com/repos/RVC-Boss/GPT-SoVITS | python3 -c "import sys,json;d=json.load(sys.stdin);print('size_kb',d.get('size'),'license',(d.get('license') or {}).get('spdx_id'),'pushed',d.get('pushed_at'))"
echo "== HF lj1995/GPT-SoVITS"; curl -s -m 60 "https://huggingface.co/api/models/lj1995/GPT-SoVITS?blobs=true" | python3 -c "
import sys,json
d=json.load(sys.stdin); tot=0
for s in d.get('siblings',[]):
    sz=s.get('size') or 0; tot+=sz
    print(f'{sz/1e6:9.1f} MB  {s[\"rfilename\"]}')
print(f'TOTAL {tot/1e9:.2f} GB; license', (d.get('cardData') or {}).get('license'))" 
