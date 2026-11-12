using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MuvluvUiTranslate;

/// <summary>
/// 与其它也会改界面语言的插件对齐开关。
/// <list type="bullet">
/// <item>ZhFull：TMP Hook 看 Enable，但定时 Sweep 只看 Sweep；只关 Enable 仍会扫成中文。</item>
/// <item>LocaleForce：启动后把 Unity Localization 强制为 zh-Hans；关词典后原生 UI 仍是中文。</item>
/// </list>
/// </summary>
internal static class PeerUiPlugins
{
    private static bool? _zhFullPresent;
    private static IDictionary _zhFullExact;
    private static Dictionary<string, string> _zhFullReverse;
    private static int _zhFullReverseExactCount = -1;
    private static bool _localeForcePatched;

    /// <summary>
    /// 同步 ZhFull / LocaleForce / 游戏 Locale。
    /// 返回 true 表示本轮对齐完成（可停止重试）；false 表示依赖尚未就绪，请稍后重试。
    /// </summary>
    public static bool SyncEnabled(bool enabled)
    {
        bool zhOk = TrySetZhFullFlags(enabled);
        bool patchOk = TryPatchLocaleForceGate();
        bool localeOk = TrySetGameLocale(enabled ? "zh-Hans" : "ja");
        return zhOk && patchOk && localeOk;
    }

    /// <summary>
    /// 用 ZhFull 大词典做无歧义 exact 反向还原（本插件词典覆盖面远小于 ZhFull）。
    /// </summary>
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

    private static bool TrySetZhFullFlags(bool enabled)
    {
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

            if (changedEnable || changedSweep)
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

    /// <summary>经反射设置 Unity Localization，避免与 Il2Cpp 程序集 Locale 类型冲突。</summary>
    private static bool TrySetGameLocale(string code)
    {
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

        // 回退：遍历 Locales 列表按 Code 匹配
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

    /// <summary>
    /// 给 LocaleForce.Update 加门闩：UI 翻译关闭时直接跳过，避免再次强制 zh-Hans。
    /// </summary>
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

    // Harmony prefix：返回 false 跳过原方法。
    private static bool LocaleForceUpdatePrefix() => Config.TranslationEnabled.Value;

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
