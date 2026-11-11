using System;
using System.Linq;
using System.Reflection;

namespace MuvluvUiTranslate;

/// <summary>
/// 与其它也会改 TMP UI 的插件对齐开关。当前汉化包里 ZhFull 同样 Hook set_text，
/// 只关本插件会导致「F6 关了仍是中文」。
/// </summary>
internal static class PeerUiPlugins
{
    public static void SyncEnabled(bool enabled)
    {
        TrySetZhFullEnable(enabled);
    }

    private static void TrySetZhFullEnable(bool enabled)
    {
        try
        {
            Assembly asm = AppDomain
                .CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ZhFull");
            if (asm == null)
                return;

            Type pluginType = asm.GetType("ZhFull.Plugin");
            if (pluginType == null)
                return;

            object cfg = pluginType
                .GetField("Cfg", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (cfg == null)
                return;

            object enableEntry = cfg.GetType()
                .GetField("Enable", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(cfg);
            if (enableEntry == null)
                return;

            PropertyInfo valueProp = enableEntry.GetType().GetProperty("Value");
            if (valueProp == null)
                return;

            object current = valueProp.GetValue(enableEntry);
            if (current is bool b && b == enabled)
                return;

            valueProp.SetValue(enableEntry, enabled);
            Plugin.Log.LogInfo($"[ui] synced ZhFull.General.Enable = {enabled}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ui] sync ZhFull failed: {e.Message}");
        }
    }
}
