using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MuvluvUiTranslate;

/// <summary>
/// 与 ZhFull 对齐 UI 词典覆盖开关。
/// 绝不改 Unity SelectedLocale / LocaleForce（切 ja 会 CDN 404 打不开）。
/// 关 F6 = 停本插件 + ZhFull 的 TMP 词典覆盖；游戏原生 CDN 简中关不掉。
/// </summary>
internal static class PeerUiPlugins
{
    private static bool? _zhFullPresent;
    private static IDictionary _zhFullExact;
    private static Dictionary<string, string> _zhFullReverse;
    private static int _zhFullReverseExactCount = -1;
    private static bool _zhFullGatesPatched;
    private static float _nextReassertLogAt;
    private static int _reassertZhFullCount;

    public static bool SyncEnabled(bool enabled) => Hold(enabled);

    public static bool Hold(bool enabled)
    {
        bool zhOk = TrySetZhFullFlags(enabled, out bool zhChanged);
        bool gatesOk = TryPatchZhFullGates();

        if (zhChanged)
        {
            _reassertZhFullCount++;
            if (UnityEngine.Time.unscaledTime >= _nextReassertLogAt)
            {
                _nextReassertLogAt = UnityEngine.Time.unscaledTime + 8f;
                Plugin.Log.LogInfo(
                    $"[ui] hold reassert ZhFull Enable/Sweep={enabled} (count={_reassertZhFullCount})"
                );
            }
        }

        return zhOk && gatesOk;
    }

    /// <summary>读出 ZhFull 当前 Enable/Sweep，供 F6 日志核对。</summary>
    public static string DescribeZhFullState()
    {
        try
        {
            if (!TryGetZhFullCfg(out object cfg))
                return "ZhFull=absent";

            bool? enable = ReadConfigBool(cfg, "Enable");
            bool? sweep = ReadConfigBool(cfg, "Sweep");
            return $"ZhFull Enable={enable} Sweep={sweep} gates={_zhFullGatesPatched}";
        }
        catch (Exception e)
        {
            return $"ZhFull=? ({e.Message})";
        }
    }

    public static bool TryUntranslate(string chinese, out string japanese)
    {
        japanese = null;
        try
        {
            EnsureZhFullReverse();
            if (_zhFullReverse == null || string.IsNullOrEmpty(chinese))
                return false;
            return _zhFullReverse.TryGetValue(chinese, out japanese)
                && !string.IsNullOrEmpty(japanese);
        }
        catch
        {
            japanese = null;
            return false;
        }
    }

    private static bool TrySetZhFullFlags(bool enabled, out bool changed)
    {
        changed = false;
        try
        {
            if (!TryGetZhFullCfg(out object cfg))
            {
                _zhFullPresent = FindAssembly("ZhFull") != null ? true : false;
                // cfg 尚未就绪时稍后重试
                return FindAssembly("ZhFull") == null;
            }

            _zhFullPresent = true;
            bool changedEnable = SetConfigBool(cfg, "Enable", enabled);
            bool changedSweep = SetConfigBool(cfg, "Sweep", enabled);
            changed = changedEnable || changedSweep;

            if (changed)
            {
                Plugin.Log.LogInfo(
                    $"[ui] synced ZhFull Enable={enabled}, Sweep={enabled}"
                );
            }

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ui] sync ZhFull failed: {e.Message}");
            return false;
        }
    }

    private static bool TryGetZhFullCfg(out object cfg)
    {
        cfg = null;
        Assembly asm = FindAssembly("ZhFull");
        if (asm == null)
            return false;

        Type pluginType = asm.GetType("ZhFull.Plugin");
        cfg = pluginType
            ?.GetField("Cfg", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        return cfg != null;
    }

    /// <summary>
    /// 门闩 ZhFull 的 Incoming/Baked/Sweep：即使 Enable 被写回，关 UI 翻译时也不再改 TMP。
    /// </summary>
    private static bool TryPatchZhFullGates()
    {
        if (_zhFullGatesPatched)
            return true;

        try
        {
            Assembly asm = FindAssembly("ZhFull");
            if (asm == null)
                return true;

            var harmony = new Harmony(Plugin.PluginGuid + ".zhfullgates");
            int patched = 0;

            patched += PatchPrefix(
                harmony,
                asm.GetType("ZhFull.TextFlow"),
                "Incoming",
                nameof(ZhFullAllowPrefix),
                BindingFlags.Public | BindingFlags.Static
            );
            patched += PatchPrefix(
                harmony,
                asm.GetType("ZhFull.TextFlow"),
                "Baked",
                nameof(ZhFullAllowPrefix),
                BindingFlags.Public | BindingFlags.Static
            );
            patched += PatchPrefix(
                harmony,
                asm.GetType("ZhFull.Manager"),
                "Sweep",
                nameof(ZhFullAllowPrefix),
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (patched < 3)
                return false;

            _zhFullGatesPatched = true;
            Plugin.Log.LogInfo(
                $"[ui] gated ZhFull Incoming/Baked/Sweep behind Translation.Enabled (n={patched})"
            );
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ui] ZhFull gates failed: {e.Message}");
            return false;
        }
    }

    private static int PatchPrefix(
        Harmony harmony,
        Type type,
        string methodName,
        string prefixName,
        BindingFlags flags
    )
    {
        if (type == null)
            return 0;

        MethodInfo method = type.GetMethod(methodName, flags);
        if (method == null)
        {
            // Incoming 有 ref string，再试不限定参数
            method = type.GetMethods(flags).FirstOrDefault(m => m.Name == methodName);
        }

        if (method == null)
            return 0;

        MethodInfo prefix = typeof(PeerUiPlugins).GetMethod(
            prefixName,
            BindingFlags.NonPublic | BindingFlags.Static
        );
        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
        return 1;
    }

    // Harmony prefix：Translation 关闭时跳过 ZhFull 词典写入。
    private static bool ZhFullAllowPrefix() => Config.TranslationEnabled.Value;

    private static bool SetConfigBool(object cfg, string fieldName, bool value)
    {
        object entry = cfg.GetType()
            .GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(cfg);
        if (entry == null)
            return false;

        PropertyInfo valueProp = entry.GetType().GetProperty("Value");
        if (valueProp == null)
            return false;

        object current = valueProp.GetValue(entry);
        if (current is bool b && b == value)
            return false;

        valueProp.SetValue(entry, value);
        return true;
    }

    private static bool? ReadConfigBool(object cfg, string fieldName)
    {
        object entry = cfg.GetType()
            .GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(cfg);
        PropertyInfo valueProp = entry?.GetType().GetProperty("Value");
        object current = valueProp?.GetValue(entry);
        return current as bool?;
    }

    private static Assembly FindAssembly(string name)
    {
        return AppDomain
            .CurrentDomain.GetAssemblies()
            .FirstOrDefault(a =>
            {
                try
                {
                    return a.GetName().Name == name;
                }
                catch
                {
                    return false;
                }
            });
    }

    private static void EnsureZhFullReverse()
    {
        if (_zhFullPresent == false)
            return;

        object exactObj = _zhFullExact;
        if (exactObj == null)
        {
            Assembly asm = FindAssembly("ZhFull");
            if (asm == null)
            {
                _zhFullPresent = false;
                return;
            }

            Type dictType = asm.GetType("ZhFull.TextDict");
            FieldInfo exactField = dictType?.GetField(
                "Exact",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            exactObj = exactField?.GetValue(null);
            _zhFullExact = exactObj as IDictionary;
            if (_zhFullExact == null)
                return;
        }

        int count = _zhFullExact.Count;
        if (_zhFullReverse != null && _zhFullReverseExactCount == count)
            return;

        var reverse = new Dictionary<string, string>(count);
        var ambiguous = new HashSet<string>();
        foreach (DictionaryEntry kv in _zhFullExact)
        {
            if (kv.Key is not string jp || kv.Value is not string zh)
                continue;
            if (string.IsNullOrEmpty(jp) || string.IsNullOrEmpty(zh) || jp == zh)
                continue;

            if (ambiguous.Contains(zh))
                continue;

            if (reverse.TryGetValue(zh, out string existing))
            {
                if (existing != jp)
                {
                    reverse.Remove(zh);
                    ambiguous.Add(zh);
                }
                continue;
            }

            reverse[zh] = jp;
        }

        _zhFullReverse = reverse;
        _zhFullReverseExactCount = count;
    }
}
