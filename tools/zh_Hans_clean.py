# -*- coding: utf-8 -*-
"""zh_Hans.json 词典清洗：全量 exact 条目 → 纯 core 形态（与插件 RichText 三级拆分对齐）。

用法（默认 python 环境，工作目录任意）:
    python tools/zh_Hans_clean.py --dry-run        # 预览统计，不发请求、不动文件
    python tools/zh_Hans_clean.py                  # 正式清洗（.bak 备份后原子覆盖）
    python tools/zh_Hans_clean.py --clear-pending  # 顺带清空 capture/zh_Hans.pending.json（留 .bak）

规则（拆分逻辑 import 自 split_example.py，与 C# RichText 保持一致）:
- 每条 exact 的 key/value 各自 tokenize，剔除标签/分隔符得到 core 列表；
- core 数量对齐 → 按位配对入库（纯 core → 纯 core，运行时由插件贴回标签/空白/标点）；
- 不对齐（机翻合并/拆散了句子）→ 该条的 JP core 逐个用百度在线重翻入库（限速防 429）；
- core 冲突 → 首遇保留，静默跳过；
- 无 CJK 的 core（纯 ASCII/数字）与 key==value 的恒等对不入库；
- patterns 是整串正则逃生舱，原样保留，不做任何清洗；
- 旧 pending 捕获文件是整串形态，新版插件只记纯 core → --clear-pending 清空重采。
"""
import argparse
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from split_example import extract_cores, needs_lookup  # noqa: E402
from SingleBaiduTranslator import SingleBaiduTranslator  # noqa: E402

DICT_DEFAULT = Path(
    r"C:/Users/Benson/muv_luv_girlsgarden_cl/BepInEx/plugins/MuvluvUiTranslate"
    r"/translation/ui/zh_Hans.json"
)

BAIDU_APPID = "20260820002669841"
BAIDU_APPKEY = "IT7cSfeC597Uw8n9YYyL"


def baidu_translate(tr, core: str, attempts: int = 3):
    """带退避重试的单 core 翻译。成功返回译文，失败/拒翻返回 None。"""
    for a in range(1, attempts + 1):
        try:
            res = tr.translate(core)
        except Exception as e:
            wait = 2 * a
            print(f"    请求失败(第{a}次): {e} | {wait}s 后重试", file=sys.stderr)
            time.sleep(wait)
            continue
        res = (res or "").strip()
        return res if res and res != core else None
    return None


def main():
    ap = argparse.ArgumentParser(description="清洗 UI 词典为纯 core 形态")
    ap.add_argument("--dict", type=Path, default=DICT_DEFAULT)
    ap.add_argument("--clear-pending", action="store_true", help="清空同插件 capture/zh_Hans.pending.json（留 .bak）")
    ap.add_argument("--sleep", type=float, default=1.1, help="百度请求间隔秒数（免费版 QPS=1）")
    ap.add_argument("--dry-run", action="store_true", help="只统计预览，不发请求、不写文件")
    args = ap.parse_args()

    if not args.dict.exists():
        raise SystemExit(f"文件不存在: {args.dict}")

    with open(args.dict, encoding="utf-8") as f:
        raw = json.load(f)
    old_exact = raw.get("exact", {})

    new_exact = {}
    baidu_queue = []  # 不对齐条目的 JP core（有序去重）
    stats = {"aligned": 0, "mismatch": 0, "no_core": 0, "ascii_skip": 0, "identity_skip": 0, "collision": 0}

    def add(jp, cn):
        if jp in new_exact:
            stats["collision"] += 1
            return
        new_exact[jp] = cn

    for key, value in old_exact.items():
        if key.startswith("_"):
            continue
        jp_cores = extract_cores(key)
        cn_cores = extract_cores(value)

        if not jp_cores:
            stats["no_core"] += 1
            continue

        if len(jp_cores) == len(cn_cores):
            for jp, cn in zip(jp_cores, cn_cores):
                if not needs_lookup(jp):
                    stats["ascii_skip"] += 1
                elif jp == cn:
                    stats["identity_skip"] += 1
                else:
                    add(jp, cn)
                    stats["aligned"] += 1
        else:
            stats["mismatch"] += 1
            for jp in jp_cores:
                if needs_lookup(jp) and jp not in new_exact and jp not in baidu_queue:
                    baidu_queue.append(jp)

    print(
        f"条目 {len(old_exact)} → 纯 core {len(new_exact) + len(baidu_queue)}（现有 {len(new_exact)}）\n"
        f"  对齐配对 {stats['aligned']} | 不对齐条目 {stats['mismatch']}（待百度重翻 core {len(baidu_queue)}）\n"
        f"  无可翻内容 {stats['no_core']} | 无CJK跳过 {stats['ascii_skip']} | 恒等跳过 {stats['identity_skip']}\n"
        f"  core 冲突首遇保留 {stats['collision']} | patterns 原样保留 {len(raw.get('patterns', []))} 条"
    )

    if args.dry_run:
        for jp in baidu_queue[:20]:
            print("  待重翻:", jp)
        if len(baidu_queue) > 20:
            print(f"  … 其余 {len(baidu_queue) - 20} 条略")
        print("[dry-run] 未发请求、未写文件。")
        return

    # ---- 百度重翻不对齐条目的 core ----
    if baidu_queue:
        tr = SingleBaiduTranslator(appid=BAIDU_APPID, appkey=BAIDU_APPKEY, source="jp", target="zh")
        failed = []
        for idx, jp in enumerate(baidu_queue, 1):
            cn = baidu_translate(tr, jp)
            if cn is None:
                failed.append(jp)
                print(f"[{idx}/{len(baidu_queue)}] x 失败: {jp}")
            else:
                new_exact[jp] = cn
                print(f"[{idx}/{len(baidu_queue)}] + {jp} -> {cn}")
            time.sleep(args.sleep)
        if failed:
            # 清洗后母条目已消失，重跑不会再排队——失败 core 必须落 sidecar 防丢失
            failed_path = args.dict.with_name("zh_Hans.clean_failed.json")
            with open(failed_path, "w", encoding="utf-8") as f:
                json.dump({"_说明": "清洗时百度重翻失败的 core，人工补译后并入 exact", "cores": failed}, f, ensure_ascii=False, indent=2)
            print(f"百度重翻失败 {len(failed)} 条 → {failed_path}（人工补译后并入词典）")
            for jp in failed:
                print("  ", jp)

    # ---- 原子写回（保留 _ 开头说明键与 patterns）----
    out = {k: v for k, v in raw.items() if k.startswith("_")}
    out["exact"] = new_exact
    out["patterns"] = raw.get("patterns", [])
    bak = args.dict.with_suffix(".json.bak")
    if not bak.exists():
        shutil.copy2(args.dict, bak)
        print(f"备份 → {bak}")
    tmp = args.dict.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    tmp.replace(args.dict)
    print(f"清洗完成：exact {len(old_exact)} → {len(new_exact)} 条 → {args.dict}")

    # ---- 清空旧 pending（整串形态作废，新版插件只记纯 core）----
    if args.clear_pending:
        pending = args.dict.parent.parent / "capture" / "zh_Hans.pending.json"
        if pending.exists():
            pbak = pending.with_suffix(".json.bak")
            if not pbak.exists():
                shutil.copy2(pending, pbak)
            pending.unlink()
            print(f"旧 pending 已清空（备份 {pbak}），新版插件将纯 core 重采")


if __name__ == "__main__":
    main()
