#!/usr/bin/env bash
# Narration WAV (24 kHz 16-bit mono, 讯飞「江阅尘-解说」) → Ogg Vorbis for the game. Metadata (source / AI marks) carried over.
SRC="$1"; DST="$2"
ffmpeg -hide_banner -encoders 2>/dev/null | grep -q libvorbis || { echo "no libvorbis"; exit 1; }
cd "$SRC"
ls body-*.wav | xargs -P 8 -I{} sh -c 'ffmpeg -loglevel error -y -i "{}" -map_metadata 0 -c:a libvorbis -q:a 4 "'"$DST"'/$(basename {} .wav).ogg"'
echo "converted $(ls "$DST"/body-*.ogg | wc -l)"; du -sh "$DST"
