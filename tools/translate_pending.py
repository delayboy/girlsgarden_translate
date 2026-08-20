# -*- coding: utf-8 -*-
"""批量翻译捕获文件 → 合并进 UI 词典。

用法（默认 python 环境，工作目录任意）:
    python tools/translate_pending.py                # 翻译正式目录 pending 并合并进词典
    python tools/translate_pending.py --dry-run      # 只看会翻哪些，不调 API
    python tools/translate_pending.py --max-chars 60 # 调整"只翻 UI 短文本"阈值
    python tools/translate_pending.py --api-key sk-xxx

流程: 读 zh_Hans.pending.json → 过滤(已译/超长/无假名) → 分批调 DeepSeek(挂术语表,
要求保留 TMP 标签/换行) → 校验返回 JSON → 合并写回 zh_Hans.json(保留已有词条与 patterns)。
"""
import argparse
import io
import json
import re
import sys
import time
from pathlib import Path

# sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
# sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8")

# ROOT = Path(__file__).resolve().parent.parent  # 工作目录（含 PyTools 包）
# sys.path.insert(0, str(ROOT))

PENDING_DEFAULT = Path(
    r"C:/Users/Benson/muv_luv_girlsgarden_cl/BepInEx/plugins/MuvluvUiTranslate"
    r"/translation/capture/zh_Hans.pending.json"
)
DICT_DEFAULT = Path(
    r"C:/Users/Benson/muv_luv_girlsgarden_cl/BepInEx/plugins/MuvluvUiTranslate"
    r"/translation/ui/zh_Hans.json"
)

# 与插件/清洗脚本一致的假名判据（排除 U+30FB 中点）
KANA = re.compile(r"[\u3040-\u309F\u30A1-\u30FA\u30FC-\u30FF\uFF66-\uFF9D]")

# 术语表：保证跨批次译名一致（后续里程碑会从上游翻译仓库自动扩充）
GLOSSARY = {
    "クエスト": "关卡",
    "メインクエスト": "主线关卡",
    "ミッション": "任务",
    "エピソード": "剧情",
    "メインストーリー": "主线故事",
    "ガチャ": "抽卡",
    "ショップ": "商店",
    "プレゼント": "礼物",
    "アイテム": "道具",
    "フレンド": "好友",
    "サークル": "社团",
    "指揮官": "指挥官",
    "編成": "编队",
    "親愛度": "好感度",
    "生徒": "学员",
    "スタイルカード": "风格卡",
    "採掘": "采掘",
    "メイズ": "迷宫",
    "ロール": "定位",
    "タンク": "坦克",
    "サポート": "辅助",
    "アタッカー": "输出",
    "ブーストトリガー": "Boost触发器",
    "ジェム": "宝石",
    "ギフトカード": "礼品卡",
    "ホーム": "主页",
    "ランキング": "排行榜",
    "バトルシミュレート": "战斗模拟",
    "桃園梅露": "桃园梅露",
    "シリウスシュガー": "Sirius Sugar",
    "ピコ": "Pico",
}

SYSTEM_PROMPT = """你是手游《少女庭园》（マブラヴ・ガールズガーデン，Muv-Luv 系列衍生）的 UI 本地化译者，日译简体中文。
规则（必须严格遵守）：
1. 只输出一个 JSON 对象，格式 {"原文1":"译文1","原文2":"译文2",...}，不要输出任何其他文字、解释或代码块标记。
2. key 必须与输入完全一致，一字不改（包括空格、换行、标点）。
3. TMP 富文本标签（如 <sprite name=...>、<color=#xxxxxx>、</color>、<size=40>、</size>、<sprite=5>）必须原样保留在译文的对应位置，不翻译、不删除、不改属性。
4. 换行 \n 保留；数字、百分比、英文缩写（Lv/AP/PP/EX/SSR/EN 等）保留原样。
5. UI 文案简洁有力，符合二次元手游习惯；按钮≤6字优先。
6. 术语表（遇到必须采用）：%s
7. 术语表外的角色名：日文汉字转简体保留；片假名人名按常见音译。
8. 拿不准时选择直译，不要意译扩写。"""


def load_json(path: Path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save_dict(path: Path, data: dict):
    tmp = path.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    tmp.replace(path)


def pick_candidates(pending: dict, existing: dict, max_chars: int):
    """返回待翻 keys 与统计。过滤：已译 / 已是译文 / 超长(剧情向) / 无假名。"""
    known_values = set(existing.get("exact", {}).values())
    todo, skip_done, skip_long, skip_nokana = [], 0, 0, 0
    for key in pending:
        if key.startswith("_"):
            continue
        if key in existing.get("exact", {}):
            skip_done += 1
        elif key in known_values:
            skip_done += 1
        elif len(key) > max_chars:
            skip_long += 1
        elif not KANA.search(key):
            skip_nokana += 1
        else:
            todo.append(key)
    return todo, (skip_done, skip_long, skip_nokana)


def build_batches(keys, batch_size: int):
    return [keys[i : i + batch_size] for i in range(0, len(keys), batch_size)]


def ask_deepseek(system: str, user: str, api_key: str) -> str:
    """直接调用 PyTools 的 DeepSeek 封装（默认 python 环境）。"""
    try:
        from PyTools.MyAwesomeTool.MyGptHelper import DeepSeekChat
    except ImportError as e:
        raise SystemExit(
            f"导入 PyTools 失败({e})。请在工作目录根运行，且默认 python 环境装有"
            " openai/transformers/requests 依赖。"
        )

    chat = DeepSeekChat(system_prompt=system, api_key=api_key)
    chunks = []
    for chunk in chat.chat_with_deepseek(user, temperature=0.3):
        chunks.append(chunk)
    return "".join(chunks)


def parse_model_json(raw: str, expected: list):
    """防御性解析模型输出，返回 (成功dict, 缺失keys)。"""
    text = raw.strip()
    text = re.sub(r"^```(?:json)?\s*|\s*```$", "", text, flags=re.S)
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        return None, expected
    try:
        obj = json.loads(text[start : end + 1])
    except json.JSONDecodeError:
        return None, expected
    result = {}
    missing = []
    for key in expected:
        val = obj.get(key)
        if isinstance(val, str) and val.strip() and val != key:
            result[key] = val
        else:
            missing.append(key)
    return result, missing


def main():
    ap = argparse.ArgumentParser(description="批量翻译 pending 捕获 → UI 词典")
    ap.add_argument("--pending", type=Path, default=PENDING_DEFAULT)
    ap.add_argument("--dict", type=Path, default=DICT_DEFAULT)
    ap.add_argument("--max-chars", type=int, default=80, help="只翻不超过该长度的 UI 短文本")
    ap.add_argument("--batch", type=int, default=30, help="每批条数")
    ap.add_argument("--api-key", default="sk-bc82ddd878f744c1bf8242182f74c029")
    ap.add_argument("--dry-run", action="store_true", help="不调 API，仅列出待翻清单")
    args = ap.parse_args()
    
    if not args.pending.exists():
        raise SystemExit(f"捕获文件不存在: {args.pending}")
    if not args.dict.exists():
        raise SystemExit(f"词典文件不存在: {args.dict}")

    pending = load_json(args.pending)
    target = load_json(args.dict)
    todo, (skip_done, skip_long, skip_nokana) = pick_candidates(
        pending, target, args.max_chars
    )

    print(
        f"待翻 {len(todo)} 条 | 已译跳过 {skip_done} | 超长跳过 {skip_long} "
        f"| 无假名跳过 {skip_nokana}"
    )
    if args.dry_run:
        for k in todo:
            print("  ", k.replace("\n", "\\n"))
        return
    if not todo:
        print("没有需要翻译的条目。")
        return

    glossary = "、".join(f"{k}→{v}" for k, v in GLOSSARY.items())
    system = SYSTEM_PROMPT % (glossary,)
    batches = build_batches(todo, args.batch)
    added, failed = 0, []

    for i, batch in enumerate(batches, 1):
        payload = json.dumps(batch, ensure_ascii=False)
        print(f"\n===== 批次 {i}/{len(batches)}（{len(batch)} 条）=====")
        result = None
        for attempt in (1, 2, 3):
            try:
                raw = ask_deepseek(
                    system,
                    "请翻译以下 JSON 数组中的所有字符串，按规则输出 JSON 对象：\n" + payload,
                    args.api_key,
                )
            except Exception as e:
                print(f"  API 调用失败(第{attempt}次): {e}")
                time.sleep(3 * attempt)
                continue
            result, missing = parse_model_json(raw, batch)
            if result is not None and not missing:
                break
            print(f"  输出不完整(第{attempt}次)，缺失 {len(missing or batch)} 条，重试…")
            result = result or {}
            batch = missing or batch  # 只重试缺失部分
            time.sleep(2)
        if result:
            target["exact"].update(result)
            added += len(result)
            print(f"  本批入库 {len(result)} 条")
        else:
            failed.extend(batch)

    save_dict(args.dict, target)
    print(f"\n完成：新增 {added} 条 → {args.dict}")
    if failed:
        print(f"失败 {len(failed)} 条（下轮重跑会自动重试）：")
        for k in failed:
            print("  ", k.replace("\n", "\\n"))
    print("游戏内按 F10 热重载词典即可生效。")


if __name__ == "__main__":
    main()
