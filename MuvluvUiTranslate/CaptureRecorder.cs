using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MuvluvUiTranslate;

/// <summary>
/// 未翻译文本捕获：内存去重计数（含出现过的 TMP 组件名），定时/退出时原子写盘。
/// 启动时合并已有文件，计数跨运行累计。
/// </summary>
public static class CaptureRecorder
{
    private sealed class Entry
    {
        public int Count;
        public readonly HashSet<string> Components = new(StringComparer.Ordinal);
        public DateTime LastSeen;
    }

    private const int MaxTextLength = 2000;

    private static readonly object Gate = new();
    private static Dictionary<string, Entry> _entries = new();
    private static bool _dirty;
    private static string _path;
    private static int _loggedThisSession;

    public static int UniqueCount => _entries.Count;

    public static void Initialize()
    {
        _path = Path.Combine(
            Plugin.PluginDirectory,
            "translation",
            "capture",
            "zh_Hans.pending.json"
        );

        try
        {
            if (File.Exists(_path))
                LoadExisting();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Failed to load existing capture file: {e.Message}");
        }
    }

    public static void Record(string text, string component)
    {
        if (!Config.CaptureEnabled.Value || string.IsNullOrEmpty(text))
            return;

        // TextViewText = 打字机逐字刷新组件（每字一条前缀噪声），且上游已译剧情也走此通道；
        // 其静态文案由里程碑 2 静态扫描覆盖，运行时直接跳过捕获。
        if (component == "TextViewText")
            return;

        var clipped = text.Length > MaxTextLength ? text[..MaxTextLength] : text;

        lock (Gate)
        {
            if (!_entries.TryGetValue(clipped, out var entry))
            {
                entry = new Entry();
                _entries[clipped] = entry;
            }
            entry.Count++;
            entry.LastSeen = DateTime.Now;
            if (!string.IsNullOrEmpty(component))
                entry.Components.Add(component);
            _dirty = true;
        }

        var limit = Config.CaptureLogLimit.Value;
        if (limit > 0)
        {
            var logged = Interlocked.Increment(ref _loggedThisSession);
            if (logged <= limit)
                Plugin.Log.LogInfo($"[capture] {clipped}  ({component})");
        }
    }

    /// <summary>锁内快照全部 pending 条目（core, 出现次数），供 AutoTranslator 后台线程消费。</summary>
    public static List<(string Text, int Count)> SnapshotPending()
    {
        lock (Gate)
        {
            var list = new List<(string, int)>(_entries.Count);
            foreach (var kv in _entries)
                list.Add((kv.Key, kv.Value.Count));
            return list;
        }
    }

    /// <summary>消费一条 pending（翻译已入库/已在词典），随下一次 Flush 落盘。</summary>
    public static void Remove(string text)
    {
        lock (Gate)
        {
            if (_entries.Remove(text))
                _dirty = true;
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            if (!_dirty || _path == null)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var temp = _path + ".tmp";
                using (var stream = File.Create(temp))
                using (
                    var writer = new Utf8JsonWriter(
                        stream,
                        new JsonWriterOptions
                        {
                            Indented = true,
                            Encoder = System
                                .Text
                                .Encodings
                                .Web
                                .JavaScriptEncoder
                                .UnsafeRelaxedJsonEscaping,
                        }
                    )
                )
                {
                    writer.WriteStartObject();
                    foreach (var kv in _entries)
                    {
                        writer.WritePropertyName(kv.Key);
                        writer.WriteStartObject();
                        writer.WriteNumber("count", kv.Value.Count);
                        writer.WriteStartArray("components");
                        foreach (var component in kv.Value.Components)
                            writer.WriteStringValue(component);
                        writer.WriteEndArray();
                        writer.WriteString(
                            "lastSeen",
                            kv.Value.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")
                        );
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }

                File.Move(temp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Capture flush failed: {e.Message}");
            }
        }
    }

    private static void LoadExisting()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith("_"))
                continue;

            var entry = new Entry();
            if (prop.Value.TryGetProperty("count", out var countNode))
                entry.Count = countNode.GetInt32();
            if (prop.Value.TryGetProperty("components", out var componentsNode))
            {
                foreach (var node in componentsNode.EnumerateArray())
                    entry.Components.Add(node.GetString());
            }
            entry.LastSeen = default;
            _entries[prop.Name] = entry;
        }

        Plugin.Log.LogInfo($"Loaded existing capture file: {_entries.Count} entries");
    }
}
