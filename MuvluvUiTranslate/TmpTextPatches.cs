using System;
using HarmonyLib;
using TMPro;

namespace MuvluvUiTranslate;

/// <summary>共用文本处理流程（钩子入口全部异常自防护，绝不影响游戏渲染）。</summary>
internal static class TextFlow
{
    /// <summary>代码路径设文本（set_text / SetText）：前置替换。</summary>
    public static void ProcessIncoming(ref string text, TMP_Text instance)
    {
        try
        {
            if (!Config.TranslationEnabled.Value || string.IsNullOrEmpty(text))
                return;
            if (!UiDictionary.NeedsLookup(text))
                return;

            if (UiDictionary.TryTranslate(text, out var translated))
            {
                text = translated;
                return;
            }

            CaptureMissedCores(text, SafeName(instance));
        }
        catch { }
    }

    /// <summary>prefab 烘焙文本（OnEnable 时 m_text 已反序列化完成）：查词典命中则写回。</summary>
    public static void ProcessBaked(TMP_Text instance)
    {
        try
        {
            if (!Config.TranslationEnabled.Value || instance == null)
                return;

            var current = instance.text;
            if (string.IsNullOrEmpty(current) || !UiDictionary.NeedsLookup(current))
                return;

            if (UiDictionary.TryTranslate(current, out var translated))
            {
                instance.text = translated; // 经过 set_text 钩子，中文不会再次命中
            }
            else
            {
                CaptureMissedCores(current, SafeName(instance));
            }
        }
        catch { }
    }

    /// <summary>
    /// pending 只记纯 core（RichText 拆分后的无标签无空白文本段），
    /// 逐 core 判定捕获条件（含假名、非自身译文）。
    /// </summary>
    private static void CaptureMissedCores(string text, string name)
    {
        foreach (var core in RichText.ExtractCores(text))
        {
            if (UiDictionary.ShouldCapture(core) && !UiDictionary.IsKnownTranslation(core))
                CaptureRecorder.Record(core, name);
        }
    }

    private static string SafeName(TMP_Text instance)
    {
        try
        {
            return instance == null ? "?" : instance.name;
        }
        catch
        {
            return "?";
        }
    }
}

/// <summary>
/// 文本属性 setter：TMP_Text.set_text 为唯一非 override 实现
/// （dump.cs 已验证 TextMeshPro / TextMeshProUGUI 均未重写），单点覆盖全部代码设值路径。
/// </summary>
[HarmonyPatch(typeof(TMP_Text), "set_text")]
internal static class TmpText_SetTextPropertyPatch
{
    [HarmonyPrefix]
    private static void Prefix(TMP_Text __instance, ref string value) =>
        TextFlow.ProcessIncoming(ref value, __instance);
}

/// <summary>
/// SetText(string…) 全部重载：内部直接写 m_text，不经 set_text，需单独拦截。
/// （SetText(string, bool) 与 SetText(string) 共享同一 RVA，无需重复挂。）
/// </summary>
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float), typeof(float) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float), typeof(float), typeof(float) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float), typeof(float), typeof(float), typeof(float) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) })]
[HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) })]
internal static class TmpText_SetTextPatch
{
    [HarmonyPrefix]
    private static void Prefix(TMP_Text __instance, ref string sourceText) =>
        TextFlow.ProcessIncoming(ref sourceText, __instance);
}

/// <summary>prefab 烘焙文本：反序列化直写 m_text 不触发 setter，组件激活时统一覆盖。</summary>
[HarmonyPatch(typeof(TextMeshPro), "OnEnable")]
internal static class TextMeshPro_OnEnablePatch
{
    [HarmonyPostfix]
    private static void Postfix(TextMeshPro __instance) => TextFlow.ProcessBaked(__instance);
}

[HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
internal static class TextMeshProUGUI_OnEnablePatch
{
    [HarmonyPostfix]
    private static void Postfix(TextMeshProUGUI __instance) => TextFlow.ProcessBaked(__instance);
}
