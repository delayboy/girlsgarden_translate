using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MuvluvUiTranslate;

/// <summary>
/// 与 ZhFull 对齐 UI 翻译开关，并在关闭期间持续门闩 Enable/Sweep。
/// <para>
/// 注意：绝不要改 Unity <c>SelectedLocale</c> / 门闩 LocaleForce。
/// 本客户端 CDN 本地化资源依赖 zh-Hans；切到 ja 会 404（LocalizeStrings），表现为打不开/卡标题。
/// 「关 UI 翻译」只停词典 TMP 覆盖（本插件 + ZhFull），原生 locale 仍由 LocaleForce 维持 zh-Hans。
/// </para>
/// </summary>
internal static class PeerUiPlugins
{
    private static bool? _zhFullPresent;
    private static IDictionary _zhFullExact;
    private static Dictionary<string, string> _zhFullReverse;
    private static int _zhFullReverseExactCount = -1;
    private static bool _zhFullSweepPatched;
    private static float _nextReassertLogAt;
    private static int _reassertZhFullCount;

    public static bool SyncEnabled(bool enabled) => Hold(enabled);

    /// <summary>关翻译时强制 ZhFull Enable/Sweep=false；开翻译时对齐为 true。</summary>
    public static bool Hold(bool enabled)
    {
        bool zhOk = TrySetZhFullFlags(enabled, out bool zhChanged);
        bool patchSweep = TryPatchZhFullSweepGate();

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

        return zhOk && patchSweep;
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
            Assembly asm = FindAssembly("ZhFull");
            if (asm == null)
            {
                _zhFullPresent = false;
                return true;
            }

            _zhFullPresent = true;

            Type pluginType = asm.GetType("ZhFull.Plugin");
            object cfg = pluginType
                ?.GetField("Cfg", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (cfg == null)
                return false;

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

    /// <summary>即使 Sweep cfg 被写回 true，关 UI 翻译时也不允许 ZhFull 扫屏。</summary>
    private static bool TryPatchZhFullSweepGate()
    {
        if (_zhFullSweepPatched)
            return true;

        try
        {
            Assembly asm = FindAssembly("ZhFull");
            if (asm == null)
                return true;

            Type managerType = asm.GetType("ZhFull.Manager");
            MethodInfo sweep = managerType?.GetMethod(
                "Sweep",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            if (sweep == null)
                return false;

            MethodInfo prefix = typeof(PeerUiPlugins).GetMethod(
                nameof(ZhFullSweepPrefix),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            var harmony = new Harmony(Plugin.PluginGuid + ".zhfullsweep");
            harmony.Patch(sweep, prefix: new HarmonyMethod(prefix));
            _zhFullSweepPatched = true;
            Plugin.Log.LogInfo("[ui] gated ZhFull.Sweep behind Translation.Enabled");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ui] ZhFull Sweep gate failed: {e.Message}");
            return false;
        }
    }

    private static bool ZhFullSweepPrefix() => Config.TranslationEnabled.Value;

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
