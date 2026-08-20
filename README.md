# 少女庭园 UI 汉化（MuvluvUiTranslate）

《マブラヴ・ガールズガーデン（少女庭园 / Muv-Luv Girls Garden）》的 **UI 全量中文化**插件，BepInEx 6 IL2CPP 插件，通过全局 Hook TextMeshPro 文本通道 + 本地日中词典实现运行时替换，未命中词条由后台线程自动调百度翻译实时入库。

![运行示例](运行示例.png)

## 与上游 MuvluvMod 的关系

本插件与上游 [MuvluvMod](https://github.com/anosu/muvluvgg-translation)（anosu）**共存、互补**，需先安装上游：

| 分工 | 上游 MuvluvMod | 本插件 MuvluvUiTranslate |
|---|---|---|
| 剧情/MasterData 翻译 | ✅ | ❌（明确不碰，避免重复翻译） |
| 中文字体兜底 | ✅ | 依赖上游 |
| 其余全部 UI 文本 | ❌ | ✅ 本插件翻译兜底 |

## 工作原理

### 翻译通道（Harmony Hook，`TmpTextPatches.cs`）

游戏所有 UI 文本最终都流经 TextMeshPro，Hook 三个入口即可全覆盖（已用 Il2CppDumper 的 dump.cs 验证无子类 override）：

1. **`TMP_Text.set_text`** Prefix——运行时 `text = "..."` 赋值主通道，命中词典即改写入参；
2. **`TMP_Text.SetText(string, float×0..8)`** 9 个重载 Prefix——内部直写 `m_text` 不走 setter 的旁路；
3. **`TextMeshPro` / `TextMeshProUGUI` 的 `OnEnable`** Postfix——prefab 烘焙进 `m_text` 的静态文本（反序列化不调 setter），激活时统一补翻。

### 捕获通道（`CaptureRecorder.cs`）

- 只捕获**含假名**的未命中日文（平/片假名判据，排除 `・` U+30FB——它会出现在中文译名里）；
- 纯汉字日文（如「設定」）运行时照翻、但不捕获（无法与上游已译中文区分），由里程碑 2 的静态扫描补齐；
- 跳过 `TextViewText` 打字机组件（逐字刷新的剧情通道，归上游）；
- **pending 只记纯 core**（`RichText` 三级拆分后的文本核心，无标签无空白），`A\nB` 拆两条、装饰标签不重复捕获；
- 去重计数、跨运行累计、原子落盘到 `translation/capture/zh_Hans.pending.json`；
- pending 兼任自动翻译的**持久队列 + 审计**：消费即移除，失败留队跨运行重试。

### 自动翻译通道（`AutoTranslator.cs`，v0.1.3）

捕获到的未命中 core 由**专用后台线程**全自动补翻，玩游戏的过程就是词典扩充的过程：

- 1s 轮询 pending 中未入库条目，按出现频次降序逐条调百度翻译 API（jp→zh，QPS=1 限速 1.1s/条）；
- 主线程零网络 I/O（不卡帧），词典快照原子热替换，译文下次自然刷新即上屏；
- **额度三重守卫**：发送前查词典快照（含恒等条目）→ 已在则只消费不调 API，崩溃/重启/F10 任意时序下每个 core 至多消耗一次额度；API 失败/空译文不入库留队重试（连续失败退避 30s）；译文==原文（百度不译的片假名等）照常写恒等条目防重调；
- **逐条落盘**：每成功一条读改写 `zh_Hans.json`（保留 `_` 元键/patterns/手工条目，exact 键排序）+ 原子写回；
- 启动时自动补翻 pending 历史欠账；`AutoTranslate.Enabled=false` 时线程不启动，行为与旧版完全一致。

### 三级拆分（`RichText.cs`，参考实现+单测 `tools/split_example.py`）

运行时先把整串拆成三类 token，**只有 core 进词典**（key 复用性最大化）：

| token | 内容 | 处理 |
|---|---|---|
| TAG | `<sprite>`/`<color>` 等 TMP 标签 | 原样保留 |
| SEP | 空白（含换行）+ 全角标点 `！？。…‥、：；「」『』【】（）` | 原样贴回，布局零损失 |
| CORE | 其余文本段 | **唯一翻译单元**，词典 key 即纯 core |

刻意不拆：`・`（中文译名用）、`ー`（片假名长音）、ASCII 标点/数字（`1.5倍` 不断）。

### 词典（`translation/ui/zh_Hans.json`）

```jsonc
{
  "exact": { "クリックしてスタート": "点击开始" },          // 纯 core 精确匹配（无标签无空白无边缘标点）
  "patterns": [
    { "re": "^あと(\\d+)日(\\d+)時間$", "tpl": "剩{0}天{1}小时" } // 正则模板，数字归并
  ]
}
```

- 匹配双层级：① 整串 patterns（**跨标签/数字模板逃生舱**，re 可含标签）→ ② 逐 core：exact → patterns；
- 部分 core 命中即部分替换（日中混排），未命中 core 走捕获通道；
- `<sprite> テキスト` 与裸 `テキスト` 复用同一个 core key，标签/空白/标点由运行时贴回；
- **游戏内按 F10 热重载**，改完词典不用重启。

## 安装

1. 前置：游戏本体（DMM 版）+ BepInEx 6 IL2CPP（be.785）+ 上游 MuvluvMod；
2. 从 [Releases](https://github.com/delayboy/girlsgarden_translate/releases) 下载 zip，解压到游戏根目录（合并 `BepInEx/plugins/MuvluvUiTranslate/`）；
3. 进游戏即生效。

配置文件 `BepInEx/config/benson.muvluvuitranslate.cfg`：

| 配置项 | 默认 | 说明 |
|---|---|---|
| `Translation.Enabled` | true | 启用 UI 翻译 |
| `Capture.Enabled` | true | 捕获未翻译日文到 pending.json |
| `Capture.LogLimit` | 30 | 每次运行写入 BepInEx 日志的未翻译条数上限（0 关闭） |
| `AutoTranslate.Enabled` | true | 后台自动百度翻译未命中 core，实时入库生效 |
| `AutoTranslate.BaiduAppId` | 内置 | 百度翻译开放平台 APP ID（fanyi-api.baidu.com） |
| `AutoTranslate.BaiduAppKey` | 内置 | 百度翻译开放平台密钥 |

## 翻译工作流（全自动闭环）

```
玩游戏(捕获) → 后台自动百度翻译入库(实时生效) → 人工校对 zh_Hans.json（F10 热重载）
```

默认**零操作**：进游戏即自动补翻并即时生效。下列离线脚本转为精修/补盲区用途。

`tools/` 下脚本，**注意 python 环境区分**：

| 脚本 | 环境 | 作用 |
|---|---|---|
| `clean_capture.py` | **python-ba** | 剔除 TextViewText 噪声与无假名串 |
| `split_example.py` | 默认 python | 三级拆分参考实现+单测（与 C# RichText 逐字对齐） |
| `zh_Hans_clean.py` | 默认 python | 旧词典整串条目 → 纯 core 清洗（不对齐条目百度重翻，patterns 不动） |
| `translate_pending_google.py` | 默认 python | **Google 免费翻译**全量入库（哨兵占位保标签/换行/术语） |
| `translate_pending.py` | 默认 python | DeepSeek 精翻（挂术语表，适合精修） |
| `static_scan_bundles.py` | **python-ba** | UnityPy 静态扫 295 bundle 日文候选（含纯汉字盲区） |
| `translate_static.py` | 默认 python | 静态候选 Google 粗翻入库（每 100 条落盘可续跑） |

### Google 版（批量草稿，免费无 key）

```bash
python tools/translate_pending_google.py --dry-run   # 先看待翻清单
python tools/translate_pending_google.py             # 正式翻译并入词典
```

要点：送翻前把 TMP 标签、换行、术语表全部替换成 `⟦n⟧` 哨兵，回填时校验哨兵全部存活，任何丢失该条作废进 failed 清单——**绝不写坏词典**；术语（クエスト→关卡 等 30 词）回填规范译名保证全局一致；已被 patterns 覆盖的条目自动跳过；Google 草稿记录在 `zh_Hans.google_log.json`，供日后 DeepSeek 精修。

### DeepSeek 版（精翻）

```bash
python tools/translate_pending.py --dry-run
python tools/translate_pending.py               # 需 DeepSeek API key
```

依赖 `PyTools` 包（DeepSeekChat 封装）在工作目录根。

### 静态扫描（里程碑 2：补运行时捕获盲区）

运行时捕获只能抓"已访问界面且含假名"的日文；纯汉字日文（設定/価格）与未访问界面靠静态补：

```bash
# 1. 扫 bundle（分析副本，产出 static_scan/static_candidates.json）
python-ba tools/static_scan_bundles.py

# 2. 静态候选粗翻入库（不要求假名；每 100 条落盘，Ctrl-C 可续跑）
python tools/translate_static.py --dry-run
python tools/translate_static.py
```

原理：UnityPy（FALLBACK_UNITY_VERSION=6000.0.59f2）遍历 295 bundle 的 MonoBehaviour 原始字节，按"含假名或 ≥2 汉字"的信号切日文串（二进制噪声率 ~2.5%）；剧情文本不烘焙进 bundle，扫到的以 UI prefab 文案为主。另用 Il2CppDumper 的 `stringliteral.json`（29,616 条 C# 字面量）同规则并入。

## 构建 / 打包 / 发版

```bash
# 构建（MuvluvUiTranslate/ 内自带一份 BepInEx 编译环境；也可指向真实游戏目录）
dotnet build MuvluvUiTranslate -p:GameDir=C:\你的游戏目录

# 一键打包 release zip（白名单只含 BepInEx/plugins/MuvluvUiTranslate）
python one_click_main.py

# 上传 GitHub Release（认证走 GITHUB_TOKEN 或 git 凭据管理器）
python publish_release.py
```

## 目录结构

```
MuvluvUiTranslate/        插件源码（csproj + 10 个 .cs）+ 自带 BepInEx 编译环境
  Plugin.cs               入口：配置/词典/捕获初始化，Harmony PatchAll，启动自动翻译线程
  TmpTextPatches.cs       三类 TMP Hook
  RichText.cs             三级 token 拆分（与 tools/split_example.py 对齐）
  UiDictionary.cs         词典加载/双层级匹配/运行时 AddEntry 入库（不可变快照热替换）
  CaptureRecorder.cs      捕获去重计数、原子落盘、pending 快照/消费接口
  AutoTranslator.cs       后台自动翻译 worker（1s 轮询 pending → 百度 → 逐条入库）
  BaiduTranslator.cs      百度翻译 API 客户端（移植自 tools/SingleBaiduTranslator.cs）
  UiTranslateManager.cs   F10 热重载、定时 flush
  Config.cs               BepInEx 配置项
tools/                    翻译管线脚本（见上）
build_example/            构建产物示例（dll + translation）
Il2CppDumper-win-v6.7.46/ 逆向工具（里程碑 2 静态扫描用）
dump.cs                   Il2CppDumper 导出的全量类信息（Hook 点验证依据）
少女庭园UI翻译计划书.md     项目计划书
```

## 路线图

- [x] 里程碑 1：插件 + 词典 v1 + 捕获→机翻管线，游戏内实测生效
- [x] 里程碑 2：静态扫描补齐运行时捕获盲区（bundle 扫描 1,074 条 + 字符串字面量 1,408 条 → Google 粗翻入库）
- [x] 里程碑 3：运行时自动翻译闭环（百度 API 后台入库，额度防重调三重守卫，v0.1.3）
- [ ] 术语库自动扩充（从上游翻译仓库提取译名；上游 names/zh_Hans.json 现为 404，暂缓）
- [x] Google 草稿批量 LLM 精修（`zh_Hans.google_log.json` 清单驱动）：Google应对UI翻译足够，暂缓执行

## 致谢

- [MuvluvMod](https://github.com/anosu/muvluvgg-translation)（anosu）——剧情/MasterData 翻译与中文字体兜底
- [BepInEx](https://github.com/BepInEx/BepInEx) / [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) / [Harmony](https://github.com/pardeike/Harmony)
- [Il2CppDumper](https://github.com/Perfare/Il2CppDumper)、[deep_translator](https://github.com/nidhaloff/deep-translator)
