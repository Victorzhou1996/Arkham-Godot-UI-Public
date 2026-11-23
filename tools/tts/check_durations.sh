#!/usr/bin/env bash
cd "$1"; for f in body-*.ogg; do echo "${f%.ogg} $(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f")"; done
