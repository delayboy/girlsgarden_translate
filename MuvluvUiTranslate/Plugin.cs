using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace MuvluvUiTranslate;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "benson.muvluvuitranslate";
    public const string PluginName = "MuvluvUiTranslate";
    public const string PluginVersion = "0.1.1";

    public static new ManualLogSource Log;
    public static string PluginDirectory;

    private Harmony _harmony;

    public override void Load()
    {
        Log = base.Log;

        PluginDirectory = ResolvePluginRoot(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? Paths.PluginPath
        );

        MuvluvUiTranslate.Config.Initialize(base.Config);
        UiDictionary.Initialize();
        CaptureRecorder.Initialize();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Plugin).Assembly);

        AddComponent<UiTranslateManager>();

        Log.LogInfo(
            $"{PluginName} {PluginVersion} loaded. "
                + $"Dictionary: {UiDictionary.ExactCount} exact / {UiDictionary.PatternCount} patterns. "
                + $"Directory: {PluginDirectory}"
        );
    }

    public override bool Unload()
    {
        CaptureRecorder.Flush();
        _harmony?.UnpatchSelf();
        Log.LogInfo($"{PluginName} unloaded");
        return base.Unload();
    }

    /// <summary>
    /// 插件 DLL 可能位于 plugins\MuvluvUiTranslate\ 或其子目录，
    /// 向上找到 plugins 直属目录作为翻译文件根目录。
    /// </summary>
    private static string ResolvePluginRoot(string dllDirectory)
    {
        var dir = new DirectoryInfo(dllDirectory);
        while (dir != null && dir.Parent?.FullName != Paths.PluginPath)
            dir = dir.Parent;
        return dir?.FullName ?? dllDirectory;
    }
}
