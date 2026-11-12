using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MuvluvUiTranslate;

/// <summary>
/// 与其它也会改界面语言的插件对齐开关，并在关闭期间持续门闩。
/// <list type="bullet">
/// <item>ZhFull：TMP Hook 看 Enable，Sweep 只看 Sweep；二者都要关，且要防被写回。</item>
/// <item>LocaleForce / 游戏自身：会把 Unity Localization 设回 zh-Hans，导致「F6 马上生效、过一会又中文」。</item>
/// </list>
/// </summary>
internal static class PeerUiPlugins
{
    private static bool? _zhFullPresent;
    private static IDictionary _zhFullExact;
    private static Dictionary<string, string> _zhFullReverse;
    private static int _zhFullReverseExactCount = -1;
    private static bool _localeForcePatched;
    private static bool _zhFullSweepPatched;
    private static float _nextReassertLogAt;
    private static int _reassertLocaleCount;
    private static int _reassertZhFullCount;

    /// <summary>
    /// 按当前 UI 翻译开关对齐同伴插件；返回是否已具备对齐所需程序集/组件。
    /// </summary>
    public static bool SyncEnabled(bool enabled) => Hold(enabled, forceRefreshOnLocaleFix: false);

    /// <summary>
    /// 持续门闩：关翻译时强制 ZhFull Enable/Sweep=false、locale=ja；开翻译时对齐回中文侧。
    /// </summary>
    public static bool Hold(bool enabled, bool forceRefreshOnLocaleFix)
    {
        bool zhOk = TrySetZhFullFlags(enabled, out bool zhChanged);
        bool patchLocale = TryPatchLocaleForceGate();
        bool patchSweep = TryPatchZhFullSweepGate();
        bool localeOk = TrySetGameLocale(enabled ? "zh-Hans" : "ja", out bool localeChanged);

        if (zhChanged)
            _reassertZhFullCount++;
        if (localeChanged)
            _reassertLocaleCount++;

        // 有人把 locale/ZhFull 写回去时打节流日志，便于确认「弹回」来源。
        if ((zhChanged || localeChanged) && UnityEngine.Time.unscaledTime >= _nextReassertLogAt)
        {
            _nextReassertLogAt = UnityEngine.Time.unscaledTime + 8f;
            Plugin.Log.LogInfo(
                $"[ui] hold reassert enabled={enabled} zhFullChanged={zhChanged} localeChanged={localeChanged} "
                    + $"(zhN={_reassertZhFullCount}, localeN={_reassertLocaleCount})"
            );
        }

        if (!enabled && localeChanged && forceRefreshOnLocaleFix)
            UiTextRefresh.ApplyAll(translate: false);

        return zhOk && patchLocale && patchSweep && localeOk;
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

    private static bool TrySetGameLocale(string code, out bool changed)
    {
        changed = false;
        try
        {
            Type settingsType = FindType(
                "Unity.Localization",
                "UnityEngine.Localization.Settings.LocalizationSettings"
            );
            if (settingsType == null)
                return false;

            object selected = settingsType
                .GetProperty("SelectedLocale", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (selected == null)
                return false;

            object available = settingsType
                .GetProperty("AvailableLocales", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (available == null)
                return false;

            string current = ReadLocaleCode(selected) ?? "?";
            if (current == code)
                return true;

            object target = ResolveLocale(available, code);
            if (target == null)
            {
                Plugin.Log.LogWarning($"[ui] locale '{code}' not found");
                return true;
            }

            settingsType
                .GetProperty("SelectedLocale", BindingFlags.Public | BindingFlags.Static)
                ?.SetValue(null, target);
            changed = true;
            Plugin.Log.LogInfo($"[ui] set game locale '{code}' (was '{current}')");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ui] set locale failed: {e.Message}");
            return false;
        }
    }

    private static object ResolveLocale(object available, string code)
    {
        MethodInfo getLocale = available
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == "GetLocale"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.Name.Contains("LocaleIdentifier")
            );

        if (getLocale != null)
        {
            Type idType = getLocale.GetParameters()[0].ParameterType;
            object id = CreateLocaleIdentifier(idType, code);
            if (id != null)
            {
                object found = getLocale.Invoke(available, new[] { id });
                if (found != null)
                    return found;
            }
        }

        object locales = available
            .GetType()
            .GetProperty("Locales", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(available);
        if (locales is not IEnumerable list)
            return null;

        foreach (object locale in list)
        {
            if (locale == null)
                continue;
            if (ReadLocaleCode(locale) == code)
                return locale;
        }

        return null;
    }

    private static object CreateLocaleIdentifier(Type idType, string code)
    {
        try
        {
            ConstructorInfo ctor = idType.GetConstructor(new[] { typeof(string) });
            if (ctor != null)
                return ctor.Invoke(new object[] { code });
        }
        catch
        {
        }

        try
        {
            MethodInfo op = idType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null
            );
            if (op != null)
                return op.Invoke(null, new object[] { code });
        }
        catch
        {
        }

        return null;
    }

    private static string ReadLocaleCode(object locale)
    {
        try
        {
            object identifier = locale
                .GetType()
                .GetProperty("Identifier", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(locale);
            if (identifier == null)
                return null;
            return identifier
                    .GetType()
                    .GetProperty("Code", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(identifier) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryPatchLocaleForceGate()
    {
        if (_localeForcePatched)
            return true;

        try
        {
            Assembly asm = FindAssembly("LocaleForce");
            if (asm == null)
                return true;

            Type forcerType = asm.GetType("LocaleForce.LocaleForcer");
            MethodInfo update = forcerType?.GetMethod(
                "Update",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (update == null)
                return false;

            MethodInfo prefix = typeof(PeerUiPlugins).GetMethod(
                nameof(LocaleForceUpdatePrefix),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            var harmony = new Harmony(Plugin.PluginGuid + ".localeforce");
            harmony.Patch(update, prefix: new HarmonyMethod(prefix));
            _localeForcePatched = true;
            Plugin.Log.LogInfo("[ui] gated LocaleForce.Update behind Translation.Enabled");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ui] LocaleForce gate failed: {e.Message}");
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

    private static bool LocaleForceUpdatePrefix() => Config.TranslationEnabled.Value;

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

    private static Type FindType(string assemblyName, string typeName)
    {
        Assembly asm = FindAssembly(assemblyName);
        return asm?.GetType(typeName);
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
