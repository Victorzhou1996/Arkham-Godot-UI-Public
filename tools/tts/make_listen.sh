#!/usr/bin/env bash
# Converts chosen samples to mp3 for listening (evidence/tts/listen) and prints basic level checks.
cd "/mnt/g/Python Project/Arkham UI/evidence/tts"
for v in cv3-plain cv3-soft cv3-sad cv2-plain cv2-ominous; do
  f="samples/ref-default/$v/4-devourer-r3.wav"
  echo "$v $(sox "$f" -n stat 2>&1 | grep -E 'Length|RMS     amplitude|Maximum amplitude' | tr -s ' ' | tr '\n' ' ')"
  ffmpeg -loglevel error -y -i "$f" -codec:a libmp3lame -q:a 3 "listen/结局3-$v.mp3"
done
ls -la listen
