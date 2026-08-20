using TMPro;
using UnityEngine;

namespace MuvluvUiTranslate;

/// <summary>
/// 帧循环组件：F10 热重载词典、定时落盘捕获、退出兜底落盘。
/// </summary>
public sealed class UiTranslateManager : MonoBehaviour
{
    private const float FlushIntervalSeconds = 10f;

    private float _flushTimer;

    private void Update()
    {
        try
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (
                keyboard != null
                && keyboard[UnityEngine.InputSystem.Key.F10].wasPressedThisFrame
            )
            {
                UiDictionary.Reload();
                CaptureRecorder.Flush();
            }
        }
        catch { /* 输入系统异常不应影响游戏 */ }

        _flushTimer += Time.unscaledDeltaTime;
        if (_flushTimer >= FlushIntervalSeconds)
        {
            _flushTimer = 0f;
            CaptureRecorder.Flush();
        }
    }

    private void OnApplicationQuit() => CaptureRecorder.Flush();
}
