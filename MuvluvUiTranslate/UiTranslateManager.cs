using UnityEngine;
using UnityEngine.InputSystem;

namespace MuvluvUiTranslate;

/// <summary>
/// 帧循环组件：F10 热重载词典、F6 切换 UI 翻译、定时落盘捕获、退出兜底落盘。
/// </summary>
public sealed class UiTranslateManager : MonoBehaviour
{
    private const float FlushIntervalSeconds = 10f;
    private const float ToastSeconds = 2.2f;

    private float _flushTimer;
    private string _toast = "";
    private float _toastUntil;
    private GUIStyle _toastStyle;
    private Font _toastFont;

    private void Update()
    {
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
        const float width = 320f;
        const float height = 36f;
        var rect = new Rect((Screen.width - width) * 0.5f, 28f, width, height);
        GUI.Label(rect, _toast, _toastStyle);
    }

    private void OnApplicationQuit() => CaptureRecorder.Flush();

    private void ToggleUiTranslation()
    {
        bool next = !Config.TranslationEnabled.Value;
        Config.TranslationEnabled.Value = next;
        // ConfigEntry 赋值会触发 BepInEx 写回 cfg，重启后保持。
        string state = next ? "开" : "关";
        Plugin.Log.LogInfo($"[ui] Translation.Enabled = {next} (hotkey {Config.TranslationToggleHotkey.Value})");
        ShowToast($"UI 翻译 → {state}");
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
            fontSize = 16,
            font = _toastFont,
        };
        _toastStyle.normal.textColor = Color.white;
        _toastStyle.normal.background = MakeBg(new Color(0.08f, 0.1f, 0.16f, 0.88f));
    }

    private static Font TryChineseFont()
    {
        string[] faces = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" };
        foreach (string face in faces)
        {
            try
            {
                Font font = Font.CreateDynamicFontFromOSFont(face, 16);
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
