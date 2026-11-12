using System.Collections.Generic;
using TMPro;
using Object = UnityEngine.Object;

namespace MuvluvUiTranslate;

/// <summary>
/// 记录本插件（及刷新路径）改写过的 TMP 原文，关 F6 时优先还原，
/// 不依赖改 SelectedLocale。
/// </summary>
internal static class AppliedTextTracker
{
    private static readonly Dictionary<int, string> Originals = new();

    public static void Remember(TMP_Text tmp, string original)
    {
        if ((Object)tmp == null || string.IsNullOrEmpty(original))
            return;

        try
        {
            int id = tmp.GetInstanceID();
            // 只记第一次：后续可能已是中文。
            if (!Originals.ContainsKey(id))
                Originals[id] = original;
        }
        catch
        {
        }
    }

    public static bool TryRestore(TMP_Text tmp, out string original)
    {
        original = null;
        if ((Object)tmp == null)
            return false;

        try
        {
            return Originals.TryGetValue(tmp.GetInstanceID(), out original)
                && !string.IsNullOrEmpty(original);
        }
        catch
        {
            return false;
        }
    }

    public static int Count => Originals.Count;

    public static void Clear() => Originals.Clear();
}
