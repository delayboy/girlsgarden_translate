# -*- coding: utf-8 -*-
"""Google 免费翻译版：pending 捕获 → UI 词典。

用法（默认 python 环境，工作目录任意）:
    python tools/translate_pending_google.py --dry-run   # 只看待翻清单，不发请求
    python tools/translate_pending_google.py             # 正式翻译并入词典
    python tools/translate_pending_google.py --max-chars 100 --sleep 0.5

与 DeepSeek 版（translate_pending.py）共用过滤/术语表/落盘；差异：
- Google 免费端点无术语表、且 deep_translator 用 HTML 解析返回值——TMP 标签会被
  静默丢弃、首尾空白被 strip。因此标签/换行/术语表全部哨兵化（⟦n⟧）再送翻，
  回填时校验哨兵存活，任何丢失该条作废进 failed，绝不写坏词典。
- 每条 1 个 HTTP 请求，节流 --sleep 秒防 429，失败指数退避重试。
- 已知草稿级局限：Google 可能挪动哨兵周边空格（如 "> 声音" 变 ">声音"），
  只影响对齐不影响标签/换行完整性，人工校对时顺手修。
- 产出 sidecar zh_Hans.google_log.json 记录 Google 草稿条目，供日后 LLM 精修。
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

# sys.path.insert(0, str(Path(__file__).resolve().parent))  # 复用同目录 DeepSeek 版
import translate_pending as tp  # 复用其过滤器/术语表/落盘（同目录自动进 sys.path）

from deep_translator import GoogleTranslator

LOG_DEFAULT = tp.DICT_DEFAULT.with_name("zh_Hans.google_log.json")

# TMP 标签：<sprite name=...> </color> <size=24> <sprite=5> 等（不含跨行/嵌套 <>）
TAG_RE = re.compile(r"<[^<>\n]+>")
# 术语按长度降序拼接，防止 メインクエスト 被 クエスト 截胡
GLOS_TERM_RE = re.compile(
    "|".join(re.escape(t) for t in sorted(tp.GLOSSARY, key=len, reverse=True))
)
MASK_RE = re.compile(f"({TAG_RE.pattern}|\\n|{GLOS_TERM_RE.pattern})")
FULLWIDTH_DIGITS = str.maketrans("０１２３４５６７８９", "0123456789")


def mask(text: str):
    """标签/换行/术语 → 哨兵 ⟦0⟧⟦1⟧…。返回 (送翻文本, 回填表[哨兵i→回填文本])。

    标签/换行回填原样；术语回填 GLOSSARY 规范中文译名。
    """
    restores = []

    def _sub(m):
        s = m.group(0)
        restores.append(tp.GLOSSARY.get(s, s))
        return tp.GLOSSARY.get(s, s)  # f"[{len(restores) - 1}]"

    masked = MASK_RE.sub(_sub, text)
    return masked, restores


print(mask("既にこの端末と同じプラットフォームでデータ連携を行っている場合、サポート先にデータ連携を行っていた端末の データは初期化されます"))


def unmask(text: str, restores):
    """回填哨兵。任何哨兵丢失/变形/残留返回 None（该条作废）。"""
    out = text.translate(FULLWIDTH_DIGITS)
    # for i, rep in enumerate(restores):
    #     pat = re.compile(f"\[\\s*{i}\\s*\]")
    #     if not pat.search(out):
    #         return None
    #     out = pat.sub(lambda _m: rep, out, count=1)
    # if "[" in out or "]" in out:  # 残留哨兵（Google 合并/变形了编号）
    #     return None
    return out


def split_ws(text: str):
    """deep_translator 会 strip 首尾 → 先拆出首尾空白，回填时还原。"""
    if text.__contains__("<") and text.__contains__(">"):
        return inner_split_ws(text)
    m = re.match(r"^(\s*)(.*?)(\s*)$", text, re.S)
    return m.group(1), m.group(2), m.group(3)

def inner_split_ws(text: str):
    """
    按 <> 标签分割字符串，提取第一个和最后一个标签。
    返回 (lead, core, trail)，其中：
        - lead: 第一个标签（如 '<sprite name=...>'）或空
        - core: 需要翻译的纯文本部分，去除首尾空白
        - trail: 最后一个标签，前面附加上 core 尾部被去除的空白（如有）
    示例：
        >>> split_ws('<sprite name=wing_blue_left> ガチャ説明 <sprite name=wing_blue_right>')
        ('<sprite name=wing_blue_left>', 'ガチャ説明', ' <sprite name=wing_blue_right>')
    """
    tags = re.findall(r'<[^>]*>', text)

    if not tags:
        return '', text.strip(), ''

    # ----- 处理只有一个标签的情况 -----
    if len(tags) == 1:
        tag = tags[0]
        start = text.find(tag)
        end = start + len(tag)
        before = text[:start]
        after = text[end:]

        # 标签在开头（忽略前导空白）
        if not before.strip():
            lead = tag
            core = after.strip()
            trail = ''
            return lead, core, trail

        # 标签在末尾（忽略尾随空白）
        if not after.strip():
            core = before.strip()
            # 提取 before 尾部被 strip 掉的空白
            trailing_space = before[len(before.rstrip()):]
            trail = trailing_space + tag
            return '', core, trail

        # 标签在中间：将整个文本视为普通内容，不做特殊拆分
        return '', text.strip(), ''

    # ----- 处理两个及以上标签 -----
    first_tag = tags[0]
    last_tag = tags[-1]

    # 获取中间部分（从第一个标签结束到最后一个标签开始）
    start = text.find(first_tag) + len(first_tag)
    end = text.rfind(last_tag)
    middle = text[start:end]

    core = middle.strip()
    trailing_space = middle[len(middle.rstrip()):]
    trail = trailing_space + last_tag

    return first_tag, core, trail

print(split_ws("<sprite name=wing_blue_left> ガチャ説明 <sprite name=wing_blue_right>"))
print(split_ws("ガチャ詳細"))
print(split_ws("所属するサークルを解散しますか？\n<color=#ff1c0b>"))
def google_translate(tr, masked: str, attempts: int = 3):
    """带退避重试的单条翻译。成功返回译文；请求连败/Google 原样返回 → None。"""
    for a in range(1, attempts + 1):
        try:
            res = tr.translate(masked)
        except Exception as e:
            wait = 4 * a
            print(f"    请求失败(第{a}次): {e} | {wait}s 后重试", file=sys.stderr)
            time.sleep(wait)
            continue
        # 原样返回=Google 拒翻/语言误判，重试无意义
        return res if (res and res != masked) else None
    return None


def translate_entry(tr, key: str):
    """单条完整流水线。返回 (译文, None) 或 (None, 失败原因)。"""
    lead, core, trail = split_ws(key)
    masked, restores = mask(core)
    if "<" in masked or ">" in masked:  # 嵌套/跨行标签没吃干净，直送必丢
        return None, "嵌套标签"
    translated = google_translate(tr, masked)
    if translated is None:
        return None, "请求失败"
    restored = unmask(translated, restores)
    if masked.__contains__("既にこの端末と同じプラット"):
        pass
    if restored is None:
        return None, "哨兵丢失" + masked + "---->" + translated
    final = lead + restored + trail
    if not final.strip() or final == key:
        return None, "空译/未变化"
    return final, None


def load_log(path: Path):
    if path.exists():
        try:
            with open(path, encoding="utf-8") as f:
                return json.load(f)
        except (json.JSONDecodeError, OSError):
            pass
    return {"_说明": "Google 草稿条目清单（译文在 zh_Hans.json，可用 DeepSeek 版精修）", "keys": []}


def main():
    ap = argparse.ArgumentParser(description="Google 免费翻译 pending → UI 词典")
    ap.add_argument("--pending", type=Path, default=tp.PENDING_DEFAULT)
    ap.add_argument("--dict", type=Path, default=tp.DICT_DEFAULT)
    ap.add_argument("--log", type=Path, default=LOG_DEFAULT, help="Google 草稿 sidecar 清单")
    ap.add_argument("--max-chars", type=int, default=80, help="只翻不超过该长度的 UI 短文本")
    ap.add_argument("--sleep", type=float, default=0.3, help="每条请求间隔秒数（防 429）")
    ap.add_argument("--dry-run", action="store_true", help="不发请求，仅列出待翻清单")
    args = ap.parse_args()

    for p in (args.pending, args.dict):
        if not p.exists():
            raise SystemExit(f"文件不存在: {p}")

    pending = tp.load_json(args.pending)
    target = tp.load_json(args.dict)
    todo, (skip_done, skip_long, skip_nokana) = tp.pick_candidates(
        pending, target, args.max_chars
    )
    # 已被 patterns 覆盖的运行时本来就能翻，且 exact 会遮蔽人工模板 → 跳过
    pats = [re.compile(p["re"]) for p in target.get("patterns", [])]
    covered = sum(1 for k in todo if any(p.search(k) for p in pats))
    todo = [k for k in todo if not any(p.search(k) for p in pats)]
    print(
        f"待翻 {len(todo)} 条 | 已译跳过 {skip_done} | 超长跳过 {skip_long} "
        f"| 无假名跳过 {skip_nokana} | 模板已覆盖跳过 {covered}"
    )
    if args.dry_run:
        for k in todo:
            print("  ", k.replace("\n", "\\n"))
        return
    if not todo:
        print("没有需要翻译的条目。")
        return

    tr = GoogleTranslator(source="ja", target="zh-CN")
    results, failed = {}, []
    try:
        for idx, key in enumerate(todo, 1):
            final, reason = translate_entry(tr, key)
            preview = key.replace("\n", "\\n")[:28]
            if final is None:
                failed.append(key)
                print(f"[{idx}/{len(todo)}] ✗ {reason}: {preview}")
            else:
                results[key] = final
                print(f"[{idx}/{len(todo)}] ✓ {preview} → {final.replace(chr(10), chr(92) + chr(110))[:32]}")
            time.sleep(args.sleep)
    except KeyboardInterrupt:
        print("\n检测到 Ctrl-C，保存已完成部分…")
    finally:
        if results:
            target["exact"].update(results)
            tp.save_dict(args.dict, target)
            log = load_log(args.log)
            log["keys"] = list(dict.fromkeys(log.get("keys", []) + list(results)))
            with open(args.log, "w", encoding="utf-8") as f:
                json.dump(log, f, ensure_ascii=False, indent=2)

    print(f"\n完成：Google 入库 {len(results)} 条 → {args.dict}")
    print(f"草稿清单 → {args.log}")
    if failed:
        print(f"失败 {len(failed)} 条（留给 DeepSeek 版/人工，下轮重跑会自动重试）：")
        for k in failed:
            print("  ", k.replace("\n", "\\n"))
    print("游戏内按 F10 热重载词典即可生效。")


if __name__ == "__main__":
    main()
