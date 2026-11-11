using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MuvluvUiTranslate;

/// <summary>
/// 开关切换后刷新当前已上屏 TMP：开→套词典；关→按 exact 反向表尽量还原日文。
/// </summary>
internal static class UiTextRefresh
{
    public static int ApplyAll(bool translate)
    {
        int changed = 0;
        foreach (TMP_Text tmp in Collect())
        {
            try
            {
                string current = tmp.text;
                if (string.IsNullOrEmpty(current))
                    continue;

                if (translate)
                {
                    if (
                        UiDictionary.TryTranslate(current, out string zh)
                        && !string.IsNullOrEmpty(zh)
                        && zh != current
                    )
                    {
                        tmp.text = zh;
                        changed++;
                    }
                }
                else if (
                    UiDictionary.TryUntranslate(current, out string jp)
                    && !string.IsNullOrEmpty(jp)
                    && jp != current
                )
                {
                    tmp.text = jp;
                    changed++;
                }
            }
            catch
            {
                /* 单个控件失败不阻断整屏刷新 */
            }
        }

        return changed;
    }

    private static List<TMP_Text> Collect()
    {
        var list = new List<TMP_Text>(512);
        try
        {
            Il2CppArrayBase<TMP_Text> all = Resources.FindObjectsOfTypeAll<TMP_Text>();
            if (all != null)
            {
                foreach (TMP_Text item in all)
                {
                    if ((Object)item != null)
                        list.Add(item);
                }
                if (list.Count > 0)
                    return list;
            }
        }
        catch
        {
        }

        try
        {
            Il2CppReferenceArray<Object> found = Object.FindObjectsOfType(Il2CppType.Of<TMP_Text>());
            if (found == null)
                return list;

            foreach (Object obj in (Il2CppArrayBase<Object>)found)
            {
                if (obj == null)
                    continue;
                try
                {
                    TMP_Text tmp = ((Il2CppObjectBase)obj).TryCast<TMP_Text>();
                    if ((Object)tmp != null)
                        list.Add(tmp);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return list;
    }
}
