using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MuvluvUiTranslate;

/// <summary>
/// 帧循环：F10 热重载、F6 切换 UI 词典覆盖、持续门闩 ZhFull、定时落盘。
/// </summary>
public sealed class UiTranslateManager : MonoBehaviour
{
    private const float FlushIntervalSeconds = 10f;
    private const float HoldIntervalSeconds = 1f;
    private const float ToastSeconds = 3.2f;

    private float _flushTimer;
    private float _holdTimer;
    private string _toast = "";
    private float _toastUntil;
    private GUIStyle _toastStyle;
    private Font _toastFont;
    private static bool _settingHooked;
    private static bool _applyingSetting;
    private static UiTranslateManager _instance;

    private void OnEnable() => _instance = this;

    private void OnDisable()
    {
        if (_instance == this)
            _instance = null;
    }

    private void Update()
    {
        HookSettingChanged();

        _holdTimer += Time.unscaledDeltaTime;
        if (_holdTimer >= HoldIntervalSeconds)
        {
            _holdTimer = 0f;
            PeerUiPlugins.Hold(Config.TranslationEnabled.Value);
        }

        try
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard[Key.F10].wasPressedThisFrame)
                {
                    UiDictionary.Reload();
                    CaptureRecorder.Flush();
                    ShowToast("词典已热重载");
                }

                if (TryHotkeyPressed(keyboard, Config.TranslationToggleHotkey.Value))
                    ToggleUiTranslation();
            }
        }
        catch
        {
            /* 输入系统异常不应影响游戏 */
        }

        _flushTimer += Time.unscaledDeltaTime;
        if (_flushTimer >= FlushIntervalSeconds)
        {
            _flushTimer = 0f;
            CaptureRecorder.Flush();
        }
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_toast) || Time.unscaledTime >= _toastUntil)
            return;

        EnsureToastStyle();
        const float width = 420f;
        const float height = 52f;
        var rect = new Rect((Screen.width - width) * 0.5f, 28f, width, height);
        GUI.Label(rect, _toast, _toastStyle);
    }

    private void OnApplicationQuit() => CaptureRecorder.Flush();

    private static void HookSettingChanged()
    {
        if (_settingHooked || Config.TranslationEnabled == null)
            return;

        Config.TranslationEnabled.SettingChanged += OnTranslationSettingChanged;
        _settingHooked = true;
    }

    private static void OnTranslationSettingChanged(object sender, EventArgs e)
    {
        if (_applyingSetting)
            return;

        ApplyTranslationState(Config.TranslationEnabled.Value, "SettingChanged", showToast: false);
    }

    private void ToggleUiTranslation()
    {
        bool next = !Config.TranslationEnabled.Value;
        _applyingSetting = true;
        try
        {
            Config.TranslationEnabled.Value = next;
        }
        finally
        {
            _applyingSetting = false;
        }

        ApplyTranslationState(next, $"hotkey {Config.TranslationToggleHotkey.Value}", showToast: true);
    }

    private static void ApplyTranslationState(bool enabled, string reason, bool showToast)
    {
        PeerUiPlugins.Hold(enabled);
        int refreshed = UiTextRefresh.ApplyAll(translate: enabled);
        string zhState = PeerUiPlugins.DescribeZhFullState();

        Plugin.Log.LogInfo(
            $"[ui] Translation.Enabled = {enabled} ({reason}, refreshed={refreshed}, tracked={AppliedTextTracker.Count}, {zhState})"
        );

        if (!showToast || _instance == null)
            return;

        string state = enabled ? "开" : "关";
        string tip = enabled
            ? $"UI 词典 → {state}（刷新 {refreshed}）"
            : $"UI 词典 → {state}（还原 {refreshed}）\n原生 CDN 简中关不掉；已停 ZhFull/本插件覆盖";
        _instance.ShowToast(tip);
    }

    private void ShowToast(string message)
    {
        _toast = message;
        _toastUntil = Time.unscaledTime + ToastSeconds;
    }

    private void EnsureToastStyle()
    {
        if (_toastStyle != null)
            return;

        _toastFont = TryChineseFont();
        _toastStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15,
            font = _toastFont,
            wordWrap = true,
        };
        _toastStyle.normal.textColor = Color.white;
        _toastStyle.normal.background = MakeBg(new Color(0.08f, 0.1f, 0.16f, 0.9f));
    }

    private static Font TryChineseFont()
    {
        string[] faces = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" };
        foreach (string face in faces)
        {
            try
            {
                Font font = Font.CreateDynamicFontFromOSFont(face, 15);
                if (font != null)
                    return font;
            }
            catch
            {
            }
        }

        return null;
    }

    private static Texture2D MakeBg(Color fill)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.SetPixels(new[] { fill, fill, fill, fill });
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    private static bool TryHotkeyPressed(Keyboard keyboard, KeyCode hotkey)
    {
        Key? key = ToInputSystemKey(hotkey);
        return key.HasValue && keyboard[key.Value].wasPressedThisFrame;
    }

    private static Key? ToInputSystemKey(KeyCode hotkey) =>
        hotkey switch
        {
            KeyCode.F1 => Key.F1,
            KeyCode.F2 => Key.F2,
            KeyCode.F3 => Key.F3,
            KeyCode.F4 => Key.F4,
            KeyCode.F5 => Key.F5,
            KeyCode.F6 => Key.F6,
            KeyCode.F7 => Key.F7,
            KeyCode.F8 => Key.F8,
            KeyCode.F9 => Key.F9,
            KeyCode.F10 => Key.F10,
            KeyCode.F11 => Key.F11,
            KeyCode.F12 => Key.F12,
            _ => null,
        };
}
