#!/usr/bin/env bash
D=/mnt/c/Users/victo/Desktop/Test
O="/mnt/c/Users/victo/AppData/Local/Temp/claude/3d82763ff728b71a/18aeb64a-91c9-4f17-b4b5-4c25f655bdd5/scratchpad/corpus"
ffmpeg -loglevel error -y -i "$D/0041.mp3" -an -frames:v 1 "$O/cover-0041.png"
ffmpeg -loglevel error -y -i "$D/0055.mp3" -an -frames:v 1 "$O/cover-0055.png"
python3 - <<'PY'
import struct
f=open('/mnt/c/Users/victo/Desktop/Test/0041.mp3','rb').read(4096)
i=f.find(b'TDAT')
fr=f[i:i+80]; size=struct.unpack('>I',fr[4:8])[0]; body=fr[10:10+size]
enc=body[0]; raw=body[1:]
print('enc',enc,'raw',raw)
for e in ('utf-16','utf-16-le','utf-8','gbk'):
    try: print(e, raw.decode(e))
    except Exception as x: print(e,'fail')
PY
ls -la "$O"
