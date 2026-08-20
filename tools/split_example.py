# -*- coding: utf-8 -*-
"""三级 token 拆分算法（已迁移到 C# 插件 RichText.cs，两版必须保持一致）。
本文件同时是参考实现与单测：zh_Hans_clean.py 直接 import 本模块的 tokenize/extract_cores。

token 三类：
  TAG  —— <...> TMP 富文本标签，原样保留，不翻译；
  SEP  —— 分隔符：空白（含 \\n、全角空格 \\u3000）+ 全角标点 ！？。…‥、：；「」『』【】（），
          原样保留（不翻译、不归化，重组时贴回）；
  CORE —— 其余文本段，唯一翻译单元。CORE 天然不含任何空白（空白都归入 SEP），
          直接作为词典 key 使用（纯文本，无标签无空白无边缘标点）。

刻意不拆（排除在 SEP 外）：
  ・  中文译名用它（姜小花・拉沃雅），拆了名字就碎；
  ー  片假名词内长音（パーティー），绝不能拆；
  ASCII 标点/数字/字母 —— 1.5倍、50%、Lv.3 不能被拆断。

重组：CORE 查词典命中则替换为译文，TAG/SEP 原样拼接——布局零损失，
`\\n解放中` 与 `解放中` 复用同一个 key。

运行单测：python tools/split_example.py （全部通过打印 ALL PASS，任一失败非零退出）
"""
import re
import sys

TAG_PATTERN = r"<[^<>\n]+>"
SEP_CHARS = "！？。…‥、：；「」『』【】（）"
TOKEN_RE = re.compile(f"({TAG_PATTERN}|[\\s{SEP_CHARS}]+)")

TAG, SEP, CORE = "tag", "sep", "core"


def tokenize(text: str):
    """整串 → [(kind, text), ...]。分块完备：拼接所有 token 恒等于原文。"""
    tokens = []
    pos = 0
    for m in TOKEN_RE.finditer(text):
        if m.start() > pos:
            tokens.append((CORE, text[pos : m.start()]))
        s = m.group()
        tokens.append((TAG, s) if s.startswith("<") else (SEP, s))
        pos = m.end()
    if pos < len(text):
        tokens.append((CORE, text[pos:]))
    return tokens


def extract_cores(text: str):
    """整串 → core 列表（捕获/清洗通道只处理这些纯文本）。"""
    return [t for kind, t in tokenize(text) if kind == CORE]


def needs_lookup(s: str) -> bool:
    """与 C# UiDictionary.NeedsLookup 对齐：含假名/汉字/CJK 标点才查词典。"""
    return any(
        "\u3040" <= c <= "\u309F"
        or "\u30A0" <= c <= "\u30FF"
        or "\u4E00" <= c <= "\u9FFF"
        or "\uFF66" <= c <= "\uFF9D"
        or "\u3000" <= c <= "\u303F"
        for c in s
    )


def _fmt(tpl: str, m: re.Match) -> str:
    """{0}..{n} 回填捕获组（与 C# string.Format 语义一致）。"""
    return re.sub(r"\{(\d+)\}", lambda g: m.group(int(g.group(1)) + 1), tpl)


def translate(text: str, dictionary: dict, patterns=()):
    """双层级翻译流水线（与 C# 对齐）：
    ① 整串 patterns —— 跨标签/数字模板的逃生舱（原样匹配，可含标签）；
    ② 逐 CORE：exact → patterns —— 部分命中即部分替换（混排），未命中 core 返回供捕获。
    返回 (译文或 None, 未命中 core 列表)。
    """
    if len(text) <= 64:  # 与 C# MaxPatternLength 对齐
        for regex, tpl in patterns:
            m = regex.search(text)
            if m:
                return _fmt(tpl, m), []

    out, missed, hit = [], [], False
    for kind, t in tokenize(text):
        if kind != CORE or not needs_lookup(t):
            out.append(t)
            continue
        if t in dictionary:
            out.append(dictionary[t])
            hit = True
            continue
        for regex, tpl in patterns:  # core 级数字模板
            m = regex.search(t)
            if m:
                out.append(_fmt(tpl, m))
                hit = True
                break
        else:
            out.append(t)
            missed.append(t)
    return ("".join(out) if hit else None), missed


# ---------------------------------------------------------------- 单测 ----


def run_tests():
    DICT = {
        "ガチャ説明": "抽卡说明",
        "未開拓エリア": "未开拓区域",
        "解放中": "解锁中",
        "サウンド": "声音",
        "所属するサークルを解散しますか": "要解散所属的社团吗",
        "指揮官ランク": "指挥官等级",
        "設定": "设置",
        "スキル使用": "技能使用",
    }

    PATTERNS = [
        (re.compile(r"^あと(\d+)日(\d+)時間$"), "剩{0}天{1}小时"),
        (re.compile(r"^第<color=#0096ff>(\d+)</color>エリア$"), "第<color=#0096ff>{0}</color>区域"),
    ]

    failed = 0

    def check(name, actual, expected):
        nonlocal failed
        ok = actual == expected
        if not ok:
            failed += 1
        print(f"  [{'PASS' if ok else 'FAIL'}] {name}")
        if not ok:
            print(f"        期望: {expected!r}")
            print(f"        实际: {actual!r}")

    print("== tokenize 分块完备（往返无损） ==")
    samples = [
        "<sprite name=wing_blue_left> ガチャ説明 <sprite name=wing_blue_right>",
        "<sprite name=wing_blue_left> ガチャ説明   <sprite name=wing_blue_right>\n其他<div>",
        "所属するサークルを解散しますか？\n<color=#ff1c0b>",
        "ガチャ詳細",
        "<size=24>未開拓エリア</size>\n解放中",
        "【解放条件】メインクエスト1-8をクリア",
        "パーティー開催中・姜小花・ラボワ",
        "1.5倍のダメージ！",
        "  設定  ",
        "",
    ]
    for s in samples:
        check(f"join == 原文: {s[:24]!r}", "".join(t for _, t in tokenize(s)), s)

    print("== 三级拆分形态 ==")
    check(
        "<size> 跨标签（旧 split_ws 会丢尾巴的用例）",
        [k for k, _ in tokenize("<size=24>未開拓エリア</size>\n解放中")],
        [TAG, CORE, TAG, SEP, CORE],
    )
    check("【】 归 SEP", [k for k, _ in tokenize("【解放条件】")], [SEP, CORE, SEP])
    check("？\\n 归 SEP（一个 run）", [k for k, _ in tokenize("解散しますか？\n")], [CORE, SEP])
    check("core 不含标点", extract_cores("【解放条件】"), ["解放条件"])
    check("・不拆，一个 core", extract_cores("姜小花・ラボワ"), ["姜小花・ラボワ"])
    check("ー不拆", extract_cores("パーティー開催中"), ["パーティー開催中"])
    check("ASCII 标点不拆", extract_cores("1.5倍のダメージ"), ["1.5倍のダメージ"])
    check("多 core 拆分", extract_cores("<size=24>未開拓エリア</size>\n解放中"), ["未開拓エリア", "解放中"])

    print("== 翻译重组 ==")
    check(
        "两翼精灵，空格贴回",
        translate("<sprite name=wing_blue_left> ガチャ説明 <sprite name=wing_blue_right>", DICT, PATTERNS),
        ("<sprite name=wing_blue_left> 抽卡说明 <sprite name=wing_blue_right>", []),
    )
    check(
        "最后标签后还有文字（3 空格 + \\n 保留，未命中 core 混排）",
        translate("<sprite name=wing_blue_left> ガチャ説明   <sprite name=wing_blue_right>\n其他<div>", DICT, PATTERNS),
        ("<sprite name=wing_blue_left> 抽卡说明   <sprite name=wing_blue_right>\n其他<div>", ["其他"]),
    )
    check(
        "句尾？贴回，末尾未闭合 color 原样",
        translate("所属するサークルを解散しますか？\n<color=#ff1c0b>", DICT, PATTERNS),
        ("要解散所属的社团吗？\n<color=#ff1c0b>", []),
    )
    check(
        "<size> 双 core 全命中",
        translate("<size=24>未開拓エリア</size>\n解放中", DICT, PATTERNS),
        ("<size=24>未开拓区域</size>\n解锁中", []),
    )
    check("纯文本首尾空白贴回", translate("  設定  ", DICT, PATTERNS), ("  设置  ", []))
    check(
        "整串未命中 → None + 捕获清单",
        translate("ガチャ詳細", DICT, PATTERNS),
        (None, ["ガチャ詳細"]),
    )

    print("== 双层级 patterns ==")
    check("core 级数字模板", translate("あと3日4時間", DICT, PATTERNS), ("剩3天4小时", []))
    check(
        "整串跨标签模板逃生舱（优先于逐 core）",
        translate("第<color=#0096ff>5</color>エリア", DICT, PATTERNS),
        ("第<color=#0096ff>5</color>区域", []),
    )

    print()
    if failed:
        print(f"FAILED: {failed} 项未通过")
        sys.exit(1)
    print("ALL PASS")


if __name__ == "__main__":
    run_tests()
