# -*- coding: utf-8 -*-
"""静态扫描 bundle 的日文候选词表（里程碑 2 主力，python-ba 运行）。

用法（python-ba 环境，工作目录任意）:
    python-ba tools/static_scan_bundles.py                  # 全量扫描
    python-ba tools/static_scan_bundles.py --sample 50      # 末尾多抽样目检
    python-ba tools/static_scan_bundles.py --max-chars 100  # 放宽长度上限

原理: UnityPy 加载 bundle（无版本号 → FALLBACK_UNITY_VERSION），只取 MonoBehaviour
的原始序列化字节。类型树不可用（python-ba 无 pythonnet），改用 decode(ignore) 后
按"可打印文本段"正则切串——长度前缀/浮点等二进制中间字段会留下控制字符自然断开，
不会粘连相邻字符串。剧情文本运行时从 MasterData 加载、不烘焙进 bundle，所以这里
扫到的日文以 UI prefab 文案为主（含纯汉字日文，运行时捕获抓不到的盲区）。

产出: static_scan/static_candidates.json  {原文: {"count": N, "bundles": M}}
过滤: 已在词典 exact/已译值/patterns 命中的跳过；长度 > max-chars 跳过。
"""
import argparse
import json
import random
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import translate_pending as tp  # 复用词典路径常量

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.59f2"

ROOT = Path(__file__).resolve().parent.parent
BUNDLES_DEFAULT = (
    ROOT / "muv_luv_girlsgarden_cl" / "muv_luv_girlsgarden_cl_Data"
    / "StreamingAssets" / "aa" / "StandaloneWindows64"
)
OUT_DEFAULT = ROOT / "static_scan" / "static_candidates.json"

# 日文字符判据（比运行时捕获宽：含纯汉字；U+30FB・在 30xx 区段内自然包含）
JP_CHAR = re.compile(r"[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uff66-\uff9f]")
CJK_CHARS = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")  # 只数汉字（不含假名/符号）
# 可打印文本段：ASCII 可见字符 + 日文区段 + 常用全角 + 换行（控制字符自然断段）
TEXT_RUN = re.compile(r"[ -~\u3000-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uff00-\uffef\n\r\t]+")


def extract_strings(raw: bytes):
    """MonoBehaviour 原始字节 → 文本段列表。

    只留"真日文信号"段：含假名（随机字节落进窄假名区概率极低），或 ≥2 个汉字
    （純漢字 UI 词如 価格/設定）。单个生僻汉字+ASCII 碎片是二进制噪声，丢弃。
    """
    text = raw.decode("utf-8", errors="ignore")
    out = []
    for m in TEXT_RUN.finditer(text):
        seg = m.group(0)
        if tp.KANA.search(seg) or len(CJK_CHARS.findall(seg)) >= 2:
            out.append(seg)
    return out


def main():
    ap = argparse.ArgumentParser(description="UnityPy 静态扫描 bundle 日文候选")
    ap.add_argument("--bundles-dir", type=Path, default=BUNDLES_DEFAULT)
    ap.add_argument("--dict", type=Path, default=tp.DICT_DEFAULT)
    ap.add_argument("--out", type=Path, default=OUT_DEFAULT)
    ap.add_argument("--max-chars", type=int, default=80)
    ap.add_argument("--sample", type=int, default=30, help="结束时随机抽样条数（目检噪声）")
    args = ap.parse_args()

    bundles = sorted(args.bundles_dir.glob("*.bundle"))
    if not bundles:
        raise SystemExit(f"找不到 bundle: {args.bundles_dir}")
    if not args.dict.exists():
        raise SystemExit(f"词典不存在: {args.dict}")
    target = tp.load_json(args.dict)
    known = set(target.get("exact", {})) | set(target.get("exact", {}).values())
    pats = [re.compile(p["re"]) for p in target.get("patterns", [])]

    # key -> [count, bundle 集合索引]
    hits = {}
    n_mb = n_bundle_hit = 0
    for bi, bp in enumerate(bundles, 1):
        bundle_hits = 0
        try:
            env = UnityPy.load(str(bp))
            for obj in env.objects:
                if obj.type.name != "MonoBehaviour":
                    continue
                n_mb += 1
                for seg in extract_strings(obj.get_raw_data()):
                    hits.setdefault(seg, [0, set()])
                    hits[seg][0] += 1
                    hits[seg][1].add(bi)
                    bundle_hits += 1
        except Exception as e:
            print(f"[{bi}/{len(bundles)}] 解析失败 {bp.name}: {e}", file=sys.stderr)
        if bundle_hits:
            n_bundle_hit += 1
        if bi % 50 == 0:
            print(f"  … {bi}/{len(bundles)} bundle, MonoBehaviour {n_mb}, 原始命中 {len(hits)}")

    # 过滤落盘
    out, skipped_dict, skipped_long = {}, 0, 0
    for seg, (cnt, bset) in hits.items():
        if seg in known:
            skipped_dict += 1
        elif len(seg) > args.max_chars:
            skipped_long += 1
        else:
            out[seg] = {"count": cnt, "bundles": len(bset)}
    # patterns 命中的单独统计（翻译时也会跳，这里只标记不计入）
    pat_covered = sum(1 for k in out if any(p.search(k) for p in pats))

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)

    print(f"\nbundle={len(bundles)} 含日文bundle={n_bundle_hit} MonoBehaviour={n_mb}")
    print(f"原始串 {len(hits)} | 入选 {len(out)}（其中 patterns 已覆盖 {pat_covered}）"
          f" | 词典已有跳过 {skipped_dict} | 超长跳过 {skipped_long}")
    print(f"→ {args.out}")
    if out and args.sample:
        print(f"\n随机抽样 {min(args.sample, len(out))} 条（目检噪声率）：")
        for k in random.sample(list(out), min(args.sample, len(out))):
            print("  ", k.replace("\n", "\\n")[:60])


if __name__ == "__main__":
    main()
