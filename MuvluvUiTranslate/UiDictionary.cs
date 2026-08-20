using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MuvluvUiTranslate;

/// <summary>
/// 本地 UI 词典：exact 整串精确匹配 + patterns 正则模板（捕获组 {0}..{n} 回填）。
/// 词典文件不存在时写出种子模板。热重载通过快照原子替换实现。
/// </summary>
public static class UiDictionary
{
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, string>(),
            Array.Empty<(System.Text.RegularExpressions.Regex, string)>()
        );

        public Snapshot(
            Dictionary<string, string> exact,
            (System.Text.RegularExpressions.Regex Regex, string Template)[] patterns
        )
        {
            Exact = exact;
            Patterns = patterns;
            Values = new HashSet<string>(exact.Values);
        }

        public Dictionary<string, string> Exact { get; }
        public (System.Text.RegularExpressions.Regex Regex, string Template)[] Patterns { get; }
        public HashSet<string> Values { get; }
    }

    /// <summary>超过该长度的串不再尝试 patterns（UI 模板只针对短文案，避免长文本跑正则）。</summary>
    private const int MaxPatternLength = 64;

    private static volatile Snapshot _snapshot = Snapshot.Empty;
    private static string _dictionaryPath;
    private static readonly object FileGate = new(); // AddEntry 读改写文件的串行化

    public static int ExactCount => _snapshot.Exact.Count;
    public static int PatternCount => _snapshot.Patterns.Length;

    public static void Initialize()
    {
        _dictionaryPath = Path.Combine(
            Plugin.PluginDirectory,
            "translation",
            "ui",
            "zh_Hans.json"
        );

        if (!File.Exists(_dictionaryPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_dictionaryPath));
                File.WriteAllText(_dictionaryPath, SeedJson);
                Plugin.Log.LogInfo($"Seeded dictionary template: {_dictionaryPath}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Failed to seed dictionary: {e.Message}");
            }
        }

        Load();
    }

    public static void Reload()
    {
        try
        {
            Load();
            Plugin.Log.LogInfo(
                $"Dictionary reloaded: {ExactCount} exact / {PatternCount} patterns"
            );
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Dictionary reload failed: {e.Message}");
        }
    }

    /// <summary>是否值得查词典：含假名或汉字（含 CJK 标点）才查，纯 ASCII/数字直接跳过。</summary>
    public static bool NeedsLookup(string text)
    {
        foreach (var c in text)
        {
            if (c >= '\u3040' && c <= '\u30FF') // 平假名 + 片假名
                return true;
            if (c >= '\u4E00' && c <= '\u9FFF') // CJK 统一汉字
                return true;
            if (c >= '\uFF66' && c <= '\uFF9D') // 半角片假名
                return true;
            if (c >= '\u3000' && c <= '\u303F') // CJK 标点 、。「」
                return true;
        }
        return false;
    }

    /// <summary>是否应记入捕获：仅含假名的串（纯汉字串不捕获，交由静态扫描补齐，避免把已译中文/汉字误报）。</summary>
    public static bool ShouldCapture(string text)
    {
        foreach (var c in text)
        {
            if (c == '\u30FB') // 「・」中点也常见于中文译名（姜小花・拉沃雅），不能作为日文判据
                continue;
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
                return true;
            if (c >= '\uFF66' && c <= '\uFF9D')
                return true;
        }
        return false;
    }

    /// <summary>该串是否已是本词典的译文（防止自身译文被重复捕获/翻译）。</summary>
    public static bool IsKnownTranslation(string text) => _snapshot.Values.Contains(text);

    /// <summary>自动翻译发送前查重守卫：已在词典（含恒等条目/手工条目）则无需调 API。</summary>
    public static bool NeedsAutoTranslate(string text) => !_snapshot.Exact.ContainsKey(text);

    /// <summary>
    /// 运行时新增词条（AutoTranslator 后台线程调用）：读现文件 → 改 exact → 原子写回 → 刷新快照。
    /// 读改写保留文件中的 _ 元键、patterns 与手工条目；exact 键排序保证输出稳定；
    /// key 已存在时只刷新快照不重写；恒等条目（jp==cn）同样写入（额度 conservation）。
    /// </summary>
    public static void AddEntry(string jp, string cn)
    {
        lock (FileGate)
        {
            try
            {
                var (meta, exact, patternRaws) = ReadFileForUpdate();
                if (!exact.ContainsKey(jp))
                {
                    exact[jp] = cn;
                    WriteFile(meta, exact, patternRaws);
                }
                Load(); // 快照与磁盘对齐（含本轮新条目）
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"AddEntry failed for {jp}: {e.Message}");
            }
        }
    }

    private static (
        List<(string Name, string Raw)> Meta,
        Dictionary<string, string> Exact,
        List<string> PatternRaws
    ) ReadFileForUpdate()
    {
        var meta = new List<(string, string)>();
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patternRaws = new List<string>();

        if (File.Exists(_dictionaryPath))
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(_dictionaryPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }
            );
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("_"))
                    meta.Add((prop.Name, prop.Value.GetRawText()));
                else if (prop.Name == "exact")
                {
                    foreach (var item in prop.Value.EnumerateObject())
                    {
                        if (!item.Name.StartsWith("_"))
                            exact[item.Name] = item.Value.GetString();
                    }
                }
                else if (prop.Name == "patterns")
                {
                    foreach (var item in prop.Value.EnumerateArray())
                        patternRaws.Add(item.GetRawText());
                }
            }
        }
        return (meta, exact, patternRaws);
    }

    private static void WriteFile(
        List<(string Name, string Raw)> meta,
        Dictionary<string, string> exact,
        List<string> patternRaws
    )
    {
        var sorted = new SortedDictionary<string, string>(exact, StringComparer.Ordinal);
        var temp = _dictionaryPath + ".tmp";
        using (var stream = File.Create(temp))
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }
            )
        )
        {
            writer.WriteStartObject();
            foreach (var (name, raw) in meta)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(raw);
            }
            writer.WriteStartObject("exact");
            foreach (var kv in sorted)
            {
                writer.WritePropertyName(kv.Key);
                writer.WriteStringValue(kv.Value);
            }
            writer.WriteEndObject();
            writer.WriteStartArray("patterns");
            foreach (var raw in patternRaws)
                writer.WriteRawValue(raw);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.Move(temp, _dictionaryPath, overwrite: true);
    }

    /// <summary>
    /// 双层级翻译：① 整串 patterns——跨标签/数字模板逃生舱（re 原样匹配、可含标签）；
    /// ② 逐 CORE（RichText 拆分）：exact → patterns，部分命中即部分替换（混排），
    /// 未命中 core 保持原文，由 TextFlow 走捕获通道。exact key 均为纯文本 core。
    /// </summary>
    public static bool TryTranslate(string text, out string translated)
    {
        var snapshot = _snapshot;

        // ① 整串模板（短文案，避免长文本跑正则）
        if (text.Length <= MaxPatternLength && snapshot.Patterns.Length > 0)
        {
            foreach (var (regex, template) in snapshot.Patterns)
            {
                var match = regex.Match(text);
                if (match.Success)
                {
                    translated = FormatTemplate(template, match);
                    return true;
                }
            }
        }

        // ② 逐 core：标签/分隔符原样贴回，core 查词典
        var hit = false;
        var sb = new System.Text.StringBuilder(text.Length + 16);
        foreach (var token in RichText.Tokenize(text))
        {
            if (token.Kind != RichText.TokenKind.Core || !NeedsLookup(token.Text))
            {
                sb.Append(token.Text);
                continue;
            }

            if (snapshot.Exact.TryGetValue(token.Text, out var exact))
            {
                sb.Append(exact.Trim()); // Trim 防御旧数据带首尾空白
                hit = true;
                continue;
            }

            var matched = false;
            if (token.Text.Length <= MaxPatternLength)
            {
                foreach (var (regex, template) in snapshot.Patterns)
                {
                    var match = regex.Match(token.Text);
                    if (match.Success)
                    {
                        sb.Append(FormatTemplate(template, match));
                        hit = matched = true;
                        break;
                    }
                }
            }
            if (!matched)
                sb.Append(token.Text);
        }

        if (!hit)
        {
            translated = null;
            return false;
        }
        translated = sb.ToString();
        return true;
    }

    private static string FormatTemplate(string template, System.Text.RegularExpressions.Match match)
    {
        var args = new string[match.Groups.Count - 1];
        for (var i = 1; i < match.Groups.Count; i++)
            args[i - 1] = match.Groups[i].Value;
        return string.Format(template, args);
    }

    private static void Load()
    {
        if (!File.Exists(_dictionaryPath))
        {
            _snapshot = Snapshot.Empty;
            return;
        }

        using var doc = JsonDocument.Parse(
            File.ReadAllText(_dictionaryPath),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }
        );

        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("exact", out var exactNode))
        {
            foreach (var prop in exactNode.EnumerateObject())
            {
                if (prop.Name.StartsWith("_"))
                    continue;
                exact[prop.Name] = prop.Value.GetString();
            }
        }

        var patterns = Array.Empty<(System.Text.RegularExpressions.Regex, string)>();
        if (doc.RootElement.TryGetProperty("patterns", out var patternsNode))
        {
            var list = new List<(System.Text.RegularExpressions.Regex, string)>();
            foreach (var item in patternsNode.EnumerateArray())
            {
                if (!item.TryGetProperty("re", out var reNode) || !item.TryGetProperty("tpl", out var tplNode))
                    continue;
                list.Add(
                    (
                        new System.Text.RegularExpressions.Regex(
                            reNode.GetString(),
                            System.Text.RegularExpressions.RegexOptions.CultureInvariant
                        ),
                        tplNode.GetString()
                    )
                );
            }
            patterns = list.ToArray();
        }

        Interlocked.Exchange(ref _snapshot, new Snapshot(exact, patterns));
    }

    private const string SeedJson =
        @"{
  ""_说明"": ""UI 翻译词典。exact: 整串精确匹配(日文->中文)；patterns: 正则模板，re 匹配原文，tpl 用 {0}..{n} 回填捕获组。改完按 F10 热重载。"",
  ""exact"": {
    ""クリック"": ""点击"",
    ""クリックしてスタート"": ""点击开始"",
    ""ログイン"": ""登录"",
    ""ログアウト"": ""登出"",
    ""閉じる"": ""关闭"",
    ""設定"": ""设置"",
    ""ホーム"": ""主页"",
    ""戻る"": ""返回"",
    ""はい"": ""是"",
    ""いいえ"": ""否"",
    ""キャンセル"": ""取消"",
    ""OK"": ""确定"",
    ""タイトルへ"": ""回到标题"",
    ""利用規約"": ""用户协议""
  },
  ""patterns"": [
    { ""re"": ""^Lv\\.(\\d+)$"", ""tpl"": ""Lv.{0}"" }
  ]
}
";
}
