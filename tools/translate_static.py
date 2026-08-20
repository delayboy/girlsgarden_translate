# -*- coding: utf-8 -*-
"""静态候选（bundle 扫描 + 字符串字面量）→ Google 粗翻入库（默认 python 环境）。

用法:
    python tools/translate_static.py --dry-run    # 只看清单不调 API
    python tools/translate_static.py              # 全量粗翻并入词典
    python tools/translate_static.py --sleep 0.5  # 调慢防 429

与 translate_pending_google.py 的差异：候选来自 static_scan/static_candidates.json
（bundle 原文本来就是日文），**不要求含假名**——纯汉字日文（設定/価格）正是本轮
要补的盲区。哨兵占位/术语回填/校验作废/只补缺不覆盖/sidecar 记录全部复用
translate_pending_google（tg）与 translate_pending（tp），Ctrl-C 保存已完成部分。
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import translate_pending as tp
import translate_pending_google as tg

from deep_translator import GoogleTranslator

ROOT = Path(__file__).resolve().parent.parent
CAND_DEFAULT = ROOT / "static_scan" / "static_candidates.json"


def main():
    ap = argparse.ArgumentParser(description="静态候选 Google 粗翻 → UI 词典")
    ap.add_argument("--candidates", type=Path, default=CAND_DEFAULT)
    ap.add_argument("--dict", type=Path, default=tp.DICT_DEFAULT)
    ap.add_argument("--log", type=Path, default=tg.LOG_DEFAULT)
    ap.add_argument("--max-chars", type=int, default=80)
    ap.add_argument("--sleep", type=float, default=0.3)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    for p in (args.candidates, args.dict):
        if not p.exists():
            raise SystemExit(f"文件不存在: {p}")

    cands = json.loads(args.candidates.read_text(encoding="utf-8"))
    target = tp.load_json(args.dict)
    pats = [re.compile(p["re"]) for p in target.get("patterns", [])]
    known = set(target.get("exact", {})) | set(target.get("exact", {}).values())

    todo = [
        k for k in cands
        if not k.startswith("_") and k not in known
           and len(k) <= args.max_chars and not any(p.search(k) for p in pats)
    ]
    print(f"候选 {len(cands)} | 待翻 {len(todo)}（词典已有/patterns 覆盖/超长已剔除）")
    if args.dry_run:
        for k in todo[:60]:
            print("  ", k.replace("\n", "\\n")[:70])
        if len(todo) > 60:
            print(f"  …共 {len(todo)} 条")
        return
    if not todo:
        print("没有需要翻译的条目。")
        return

    tr = GoogleTranslator(source="ja", target="zh-CN")
    results, failed = {}, []
    total_saved = 0

    def checkpoint():
        """增量落盘：超时/中断最多丢 100 条，重跑自动续。"""
        nonlocal results, total_saved
        if not results:
            return
        target["exact"].update(results)
        tp.save_dict(args.dict, target)
        log = tg.load_log(args.log)
        log["keys"] = list(dict.fromkeys(log.get("keys", []) + list(results)))
        with open(args.log, "w", encoding="utf-8") as f:
            json.dump(log, f, ensure_ascii=False, indent=2)
        total_saved += len(results)
        results = {}

    try:
        for idx, key in enumerate(todo, 1):
            final, reason = tg.translate_entry(tr, key)
            if final is None:
                failed.append(key)
                print(f"[{idx}/{len(todo)}] ✗ {reason}: {key.replace(chr(10), chr(92) + chr(110))[:40]}")
            else:
                results[key] = final
                print(f"[{idx}/{len(todo)}] √ {final}: {key.replace(chr(10), chr(92) + chr(110))[:40]}")
                if len(results) >= 100:
                    checkpoint()
                if idx % 25 == 0:
                    print(f"[{idx}/{len(todo)}] 本轮+{len(results)} 已存 {total_saved} 失败 {len(failed)}")
            time.sleep(args.sleep)
    except KeyboardInterrupt:
        print("\n检测到 Ctrl-C，保存已完成部分…")
    finally:
        checkpoint()

    print(f"\n完成：静态粗翻入库 {total_saved} 条 → {args.dict}")
    print(f"草稿清单 → {args.log}")
    if failed:
        print(f"失败 {len(failed)} 条（重跑自动重试）：")
        for k in failed[:30]:
            print("  ", k.replace("\n", "\\n"))
    print("游戏内按 F10 热重载词典即可生效。")


if __name__ == "__main__":
    main()
