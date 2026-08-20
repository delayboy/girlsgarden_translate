# 少女庭园（マブラヴ・ガールズガーデン）UI 翻译计划书

> 交接文档。由前期调研对话整理，供新 Agent 直接开工。所有分析结论均已实测验证。
> 整理日期：2026-08-20

---

## 0. 一句话结论

**新建一个独立 BepInEx 插件，用"全局 TMP_Text 渲染钩子 + 本地日中词典"翻译全部 UI 文字；词条通过"运行时捕获 + 静态提取"双管线获取，AI 初翻 + 用户校对；全程 managed 层操作，暂不需要 IDA。**

---

## 1. 环境与资产清单

| 资产 | 路径 | 说明 |
|---|---|---|
| 工作目录 | `C:\Users\Benson\Desktop\muv_luv_girlsgarden_translate` | 本文档所在目录 |
| 游戏客户端（分析用副本） | `工作目录\muv_luv_girlsgarden_cl` | Unity 6000.0.59f2，IL2CPP，已带 BepInEx+interop（曾运行过） |
| C# DUMP | `工作目录\dump.cs` | Il2CppDumper 产出，**仅声明**（无方法体、无字符串字面量），190 万行 |
| 翻译插件源码 | `工作目录\MuvluvMod-main` | 上游开源项目（作者 anosu/Jitsu），只翻剧情+MasterData |
| metadata | `muv_luv_girlsgarden_cl_Data\il2cpp_data\Metadata\global-metadata.dat` | 29MB，字符串字面量表可解析 |
| Addressables | `...\StreamingAssets\aa\` | catalog.bin + 295 个哈希名 bundle（StandaloneWindows64） |
| 正式安装目录 | **待用户提供** | 实际游玩/DMM 启动的目录，编译部署目标（桌面副本仅用于分析） |

### Python 环境（重要，别搞错）

- **`python-ba.exe`** = 逆向专用环境。Python 3.10.14，位于 `C:\Users\Benson\Desktop\BlueArchive-Translator\python_embeded`，已装 UnityPy 1.25.0 等工具库。**做资产解析必须用它。**
- `python`（默认）= 用户日常办公环境，**禁用**。
- `python3` = Windows Store 占位 stub，**坏的，禁用**。

### 其他工具

- dotnet SDK：5.0.416 / 9.0.101 / 10.0.301 已装，插件目标框架 net6.0 可编译。
- UnityPy 读这套 bundle 必须先设 `UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.59f2"`（bundle 内无版本号）。
- Il2CppDumper：用户是否自备待确认；没有就用 python-ba 自行解析 global-metadata.dat。

---

## 2. 现有 MuvluvMod 技术路线（已读全部源码确认）

**框架**：BepInEx 6 IL2CPP + Harmony。编译期引用游戏目录 `BepInEx\interop\*.dll`（运行时自动生成的 175 个 interop 程序集，含 `Unity.TextMeshPro.dll`、`TextMeshProRuby.dll`），全部游戏 C# 类型可 managed 级 hook，不碰 native。

**剧情翻译链路**（`Patches/TranslationPatch.cs`）：
1. Prefix hook `EpisodeService.DownloadSceneFrameMasters` → 启动翻译 JSON 异步下载，Postfix 把返回的 `UniTask` 替换成等待协程（等源任务+翻译任务都完成）
2. Prefix hook `ScenarioController.GenerateFrames` → 逐帧把 `SceneFrameMaster.ConfigurationJson` 解析为 JSON，查字典替换 `Phrase.SpeakerName / TeamName / Text` 后写回
3. 选项 hook `ScenarioChoiceElementComponent.Apply`、历史 hook `ScenarioHistoryCell.ApplyText`

**MasterData 翻译**（`Services/MasterDataTranslator.cs`）：
- Postfix hook `MemoryDB.LoadMasterData`
- 通用反射翻译器：`{类型名: {属性路径: {日文: 中文}}}`，路径支持 `::` 嵌套与 `[]` 集合遍历，Il2Cpp 指针→interop wrapper 动态绑定并缓存

**词典与分发**（`Services/TranslationCache.cs` + `TranslationPaths.cs`）：
- 词典托管：GitHub 仓库 `anosu/muvluvgg-translation`（CdnURL 可配置），manifest+哈希校验+本地缓存，`PreferLocalFiles` 可强制本地优先
- 分类：`names/zh_Hans.json`（speakerNames/teamNames）、`static/zh_Hans.json`（MasterData）、`scenes/{id}/zh_Hans.json`（剧情）

**字体**（`Patches/FontPatch.cs` + `TranslationManager.LoadFallbackFontCoroutine`）：
- Hook `TMP_FontAsset.TryAddCharacterInternal` 禁掉游戏字体 `FOT-RODINNTLGPRO-B SDF` 的动态生成缺字
- 把中文字体（sarasa gothic TMP bundle）注册进 `TMP_Settings.fallbackFontAssets` → **所有 TMP 文本（含 UI）已能渲染中文，且全局生效**

**其他**：动态马赛克开关、战斗跳过、语音不中断等 Enhance 补丁；`MissingSceneReporter` 会把翻译缺失的剧本上报调试服务器（**未翻译捕获的现成范式**）。

**csproj 要点**：`net6.0`；引用 `$(GameDir)\BepInEx\interop\*.dll` + 兄弟项目 `Utility.dll`（FontHelper/Toast 所在，**不在本源码树里**，已部署插件目录有成品）。上游 GameDir 默认值是作者机器路径 `E:\Games\DMM GAMES\muv_luv_girlsgardenx_cl`，自行编译时需 `-p:GameDir=...` 覆盖。

---

## 3. UI 文字来源实测结论

| 来源 | 实测结果 |
|---|---|
| bundle 内 TextAsset | 295 个 bundle 仅 14 个 TextAsset，**全部无日文** → 排除 |
| prefab 序列化 TMP 字段（m_text） | **主力**。bundle 共 147,151 个 MonoBehaviour / 133,839 个 GameObject，UI prefab 为主，文案烘焙在 TMP 序列化字段里 |
| C# 代码硬编码字符串 | 存在于 global-metadata.dat 字符串字面量表（尚未解析，可自动提取） |
| 服务器 MasterData | 存在，上游 MuvluvMod 的 static/*.json 通道已覆盖（继续用，与本计划无关） |

注意：**prefab 烘焙文本在运行时反序列化直接写 m_text 字段，不走 text setter**——所以纯 setter hook 抓不到静态标签，必须配合组件激活时机（见 §5 钩子设计）。

---

## 4. 已确认的决策（用户逐项拍板，勿再讨论）

| # | 决策项 | 结论 |
|---|---|---|
| 1 | 翻译范围 | **全部 UI 文字**（主干工程量与范围无关，范围只影响翻译工作量） |
| 2 | 技术主干 | **全局 TMP_Text 渲染钩子**（MasterData 表通道已存在，继续由上游负责） |
| 3 | IDA | **暂不需要**。仅当出现"运行时抓不到且静态提取不到"的硬编码逻辑时，用户再按需开 IDA，届时给用户具体类名/偏移任务清单 |
| 4 | 词条抓取 | **运行时捕获（钩子落盘未翻译日文）+ 静态补全（字面量表 + bundle m_text 扫描）** |
| 5 | 分发 | **纯本地 JSON 文件**。不建 CDN、不 PR 上游翻译仓库 |
| 6 | 代码关系 | **独立插件与 MuvluvMod 共存**。不 fork 上游、不依赖其源码；字体回退由上游全局提供（MuvluvMod 须保持安装且翻译开启） |
| 7 | 翻译生产 | **AI 初翻 + 用户游戏内校对**，挂 Muv-Luv 术语表保证专有名词一致。不内置在线机翻 |

---

## 5. 新插件设计要点

### 5.1 项目骨架

- 独立仓库/目录（建议放工作目录下，如 `MuvluvUiTranslate/`），csproj 仿上游：`net6.0`，`-p:GameDir=<正式安装目录>`，引用 `GameDir\BepInEx\interop\*.dll`（含 Unity.TextMeshPro.dll 即够 TMP hook，无需 Utility.dll——字体与 Toast 都不用自己实现）
- 插件职责仅三件事：**TMP 钩子替换、本地词典加载、未翻译日文捕获落盘**

### 5.2 钩子设计（核心技术点）

Harmony patch 目标（`Unity.TextMeshPro.dll` interop）：
1. `TMP_Text.set_text`（string setter）+ `TMP_Text.SetText(string)` 等重载 → 前置查词典替换
2. `TMP_Text.OnEnable`（或 Awake，Postfix）→ **覆盖 prefab 烘焙文本**：组件激活时对当前 m_text 查词典，命中则写回
3. 天然幂等：译后中文不会命中日文键，无双重翻译问题；剧情文本已被上游在 JSON 层翻译成中文，同样不命中，互不干扰

### 5.3 词典格式（本地 `ui/zh_Hans.json`）

```json
{
  "exact": { "ホーム": "主页", "設定": "设置" },
  "patterns": [ { "re": "^Lv\\.\\d+$", "tpl": "Lv.{0}" } ]
}
```
- `exact`：整串精确匹配（主体）
- `patterns`：正则归一化，处理 `Lv.10`、`残り3分` 类拼接文案（静态扫描时把含数字/变量的词归并成模板）
- 加载后预编译；词典文件放 `BepInEx\plugins\<插件名>\translation\ui\zh_Hans.json`，热重载可选（快捷键）

### 5.4 捕获管线

- **运行时**：钩子遇到未命中词典的日文串 → 去重后追加写 `capture\zh_Hans.pending.json`（含出现次数与 TMP 组件名，便于定位界面）
- **静态补全**：
  - 字符串字面量：Il2CppDumper 跑 GameAssembly.dll + global-metadata.dat 出 `stringliteral.json`；或 python-ba 解析 metadata 字面量表
  - prefab 文案：UnityPy（记得设 FALLBACK_UNITY_VERSION）+ DummyDll 生成类型树，扫全部 bundle MonoBehaviour 的 TMP `m_text`
  - 两路结果与运行时捕获合并去重 → 交给 AI 初翻

---

## 6. 实施步骤

1. **环境准备**：向用户要正式安装目录路径；确认 Il2CppDumper 有无（无则 python-ba 写 metadata 解析器）
2. **跑 Il2CppDumper**：出 DummyDll（UnityPy 类型树用）+ stringliteral.json
3. **写插件**：项目骨架 + TMP 钩子 + 本地词典 + 捕获落盘（§5）
4. **静态扫描**：python-ba + UnityPy 全 bundle 扫 m_text，与字面量表合并出候选词表
5. **AI 初翻**：批量翻译候选词（挂 Muv-Luv 术语表），产出 ui/zh_Hans.json 初版
6. **部署实测**：编译部署到正式安装目录，用户游戏内校对修正
7. **迭代**：按捕获文件补词条，处理溢出等布局问题

## 7. 风险与待验证项（开发首批确认）

| 风险 | 说明 | 预案 |
|---|---|---|
| TextMeshProRuby 兼容 | 游戏带注音组件（TextMeshProRuby.dll），全局钩子可能与其文本处理叠加 | 首个里程碑先在剧情+主界面实测；异常则对 ruby 组件名下对象跳过 |
| prefab 文本不触发 setter | 反序列化直写字段 | 已在钩子设计中用 OnEnable Postfix 覆盖，实测验证 |
| 布局溢出 | 中文比日文长，固定宽度标签可能截断 | 个案处理：必要时对特定 TMP 实例启用 auto-size/缩放兜底 |
| 拼接文案 | 代码里 `string.Format` 的碎片化短串，整串词典匹配不到 | patterns 正则归一化；仍漏的靠运行时捕获补 |
| 上游依赖 | 中文字形依赖 MuvluvMod 的全局 fallback 字体 | 文档声明依赖；如需独立，后续自行注册 fallback 字体即可 |

## 8. 用户待办

- [ ] 提供正式游戏安装目录路径（部署目标）：暂时不提供，你写好插件后，告诉我如何手动安装，我自己会去找目录，防止操作时不小心覆盖重要的游戏文件。
- [x] 确认是否有现成 Il2CppDumper（版本随意，能出 DummyDll + stringliteral.json 即可）：我是用户，我已办理此项，工具解压到【Il2CppDumper-win-v6.7.46】目录下了
