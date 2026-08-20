# -*- coding: utf-8 -*-
"""清理运行时捕获文件：剔除 TextViewText 打字机噪声、无假名串（已译中文/纯汉字误报）。
用法: python-ba clean_capture.py <zh_Hans.pending.json 路径>
"""
import io
import json
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

# 假名判据（与插件 ShouldCapture 一致，排除 U+30FB 中点）
KANA = re.compile(r"[\u3040-\u309F\u30A1-\u30FA\u30FC-\u30FF\uFF66-\uFF9D]")


def has_kana(s: str) -> bool:
    return bool(KANA.search(s))


def main(path: str) -> None:
    with open(path, encoding="utf-8") as f:
        data = json.load(f)

    removed_textview = 0
    removed_no_kana = 0
    out = {}
    for k, v in data.items():
        if k.startswith("_"):
            out[k] = v
            continue
        comps = v.get("components", []) if isinstance(v, dict) else []
        if any(c == "TextViewText" for c in comps):
            removed_textview += 1
            continue
        if not has_kana(k):
            removed_no_kana += 1
            continue
        out[k] = v

    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)

    print(
        f"kept={len(out)} removed_textview={removed_textview} "
        f"removed_no_kana={removed_no_kana}"
    )


if __name__ == "__main__":
    main(sys.argv[1])
