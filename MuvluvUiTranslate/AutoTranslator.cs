using System;
using System.Collections.Generic;
using System.Threading;

namespace MuvluvUiTranslate;

/// <summary>
/// 自动翻译 worker：专用后台线程 1s 轮询 pending 中未入库的 core，
/// 按 count 降序逐条调百度翻译（jp→zh）实时入库——玩游戏即自动补词典，无需手动跑脚本。
///
/// 铁律（额度 conservation）：
/// - 发送前查重守卫：core 已在词典（含恒等条目）→ 只消费 pending，不调 API；
/// - API 失败/空译文 → 不入库、留在 pending 等下轮，连续失败退避 30s 防额度耗尽时空转打接口；
/// - 译文 == 原文 → 照常入库（恒等条目，防止同一 core 反复触发接口）；
/// - 逐条落盘：每成功一条即原子写 zh_Hans.json + 热替换快照，崩溃/退出零额度损失。
///
/// pending 兼任持久队列 + 审计：启动时未入库的历史欠账自动补翻；
/// 关闭 AutoTranslate 时线程不启动，行为与旧版完全一致。
/// </summary>
public static class AutoTranslator
{
    private const int PollMs = 1_000; // 空闲轮询间隔
    private const int QpsSpacingMs = 1_100; // 百度免费版 QPS=1
    private const int FailureBackoffMs = 30_000; // 失败退避

    private static Thread _thread;
    private static volatile bool _running;

    public static bool IsRunning => _running;

    public static void Start()
    {
        if (!Config.AutoTranslateEnabled.Value)
            return;

        var appid = Config.BaiduAppId.Value;
        var appkey = Config.BaiduAppKey.Value;
        if (string.IsNullOrWhiteSpace(appid) || string.IsNullOrWhiteSpace(appkey))
        {
            Plugin.Log.LogWarning("[auto] AutoTranslate 开启但 BaiduAppId/BaiduAppKey 为空，后台翻译未启动");
            return;
        }

        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true, // 游戏退出时不阻塞进程
            Name = "MuvluvUiTranslate.AutoTranslator",
        };
        _thread.Start();
    }

    public static void Stop()
    {
        _running = false;
        _thread?.Join(2_000);
        _thread = null;
    }

    private static void Run()
    {
        var translator = new BaiduTranslator(Config.BaiduAppId.Value, Config.BaiduAppKey.Value);
        Plugin.Log.LogInfo("[auto] 后台自动翻译已启动（1条/秒，失败退避30s）");

        while (_running)
        {
            try
            {
                var sent = DrainOnce(translator);
                if (sent == 0)
                    Sleep(PollMs); // 空闲（或本轮全被查重消费），按定时节奏轮询
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[auto] worker 异常: {e.Message}");
                Sleep(PollMs);
            }
        }
    }

    /// <summary>处理一批 pending 快照，返回本轮实际调 API 的条数。</summary>
    private static int DrainOnce(BaiduTranslator translator)
    {
        var pending = CaptureRecorder.SnapshotPending();
        pending.Sort((a, b) => b.Count.CompareTo(a.Count)); // 高频文案优先

        var sent = 0;
        foreach (var (text, _) in pending)
        {
            if (!_running)
                return sent;

            // 查重守卫：已在词典（含恒等条目/手工条目/F10 新增）→ 直接消费，不调 API
            if (!UiDictionary.NeedsAutoTranslate(text))
            {
                CaptureRecorder.Remove(text);
                continue;
            }

            string cn;
            try
            {
                cn = translator.Translate(text);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[auto] 翻译失败，本轮中止，{FailureBackoffMs / 1000}s 后重试: {text} ({e.Message})");
                Sleep(FailureBackoffMs);
                return sent;
            }

            cn = (cn ?? "").Trim();
            if (cn.Length == 0)
            {
                Plugin.Log.LogWarning($"[auto] 空译文，留队下轮重试: {text}");
                Sleep(FailureBackoffMs);
                return sent;
            }

            // 恒等（译文==原文）也照常入库：防止该 core 再次触发接口消耗额度
            UiDictionary.AddEntry(text, cn);
            CaptureRecorder.Remove(text);
            sent++;
            Plugin.Log.LogInfo($"[auto] ({sent}) {text} -> {cn}");

            Sleep(QpsSpacingMs);
        }
        return sent;
    }

    /// <summary>可中止的睡眠：每 200ms 检查一次 _running，Stop() 后最多 200ms 退出。</summary>
    private static void Sleep(int ms)
    {
        for (var elapsed = 0; elapsed < ms && _running; elapsed += 200)
            Thread.Sleep(200);
    }
}
