#!/usr/bin/env bash
# Read-only inspection of the training corpus Yunke provided (Desktop\Test). Nothing is copied or changed.
D=/mnt/c/Users/victo/Desktop/Test
ls -la "$D" | head -40
tot=0
for f in "$D"/*.mp3; do
  info=$(ffprobe -v error -show_entries format=duration,bit_rate:stream=sample_rate,channels,codec_name -of default=nw=1 "$f" | tr '\n' ' ')
  d=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f"); tot=$(python3 -c "print($tot+$d)")
  echo "$(basename "$f") $info"
done
echo "TOTAL seconds: $tot  minutes: $(python3 -c "print(round($tot/60,1))")"
echo "== tags (first file)"; ffprobe -v error -show_entries format_tags -of default=nw=1 "$D/0041.mp3"
echo "== tags (all, distinct)"; for f in "$D"/*.mp3; do ffprobe -v error -show_entries format_tags -of default=nw=1 "$f"; done | sort | uniq -c | sort -rn | head -20
