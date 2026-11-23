#!/usr/bin/env bash
for u in https://github.com https://huggingface.co https://hf-mirror.com https://modelscope.cn https://pypi.org/simple/ https://download.pytorch.org/whl/cu128/; do
  c=$(curl -s -o /dev/null -m 15 -w "%{http_code}" "$u"); echo "$u -> $c"
done
df -h /opt | tail -1
