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
/// - legacy 非纯 core 的旧 pending 条目 → 直接消费不送翻（送翻产出的 key 永远不会被命中）；
/// - 命中敏感词(20003) → 恒等条目入库消费（百度永久拒翻，重试只会无限循环）；
/// - 瞬态失败(52001/52002/54003/54005) → 留队 30s 重试；余额不足(54004) → 留队长退避 10 分钟；
/// - 配置/账户类致命错误(52003/54000/54001/58000-58003/90107) → 停止 worker，修复后重启游戏；
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
    private const int FailureBackoffMs = 30_000; // 瞬态失败退避
    private const int BalanceBackoffMs = 600_000; // 54004 余额不足：长退避（按天计费，当天大概率恢复不了）

    /// <summary>配置/账户类致命错误：重试无意义，停止 worker（修复配置后重启游戏）。</summary>
    private static readonly HashSet<string> FatalCodes = new()
    {
        "52003", // 未授权用户：appid 错误或服务未开通
        "54000", // 必填参数为空：请求构造 bug
        "54001", // 签名错误：密钥错误
        "58000", // 客户端 IP 非法
        "58001", // 译文语言方向不支持
        "58002", // 服务当前已关闭
        "58003", // 此 IP 已被封禁（当日多 APPID，次日解封）
        "90107", // 认证未通过或未生效
    };

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
        Plugin.Log.LogInfo("[auto] 后台自动翻译已启动（1条/秒，瞬态失败退避30s）");

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
        Plugin.Log.LogInfo("[auto] 后台自动翻译线程退出");
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

            // legacy 整串条目（0.1.2 之前捕获，含标签/换行/边缘标点）：送翻产出的 key 永远
            // 不会被 core 命中，纯浪费额度——直接消费移除。新捕获的 pending 均为纯 core。
            var cores = RichText.ExtractCores(text);
            if (cores.Count != 1 || cores[0] != text)
            {
                CaptureRecorder.Remove(text);
                Plugin.Log.LogInfo($"[auto] 消费 legacy 非纯 core 条目: {text}");
                continue;
            }

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
            catch (BaiduApiException e)
            {
                if (e.Code == "20003")
                {
                    // 命中敏感词：百度对该文本永久拒翻，重试只会无限循环——按恒等条目
                    // 入库防反复触发（key==value，人工后续可改 value 为真实译名）
                    UiDictionary.AddEntry(text, text);
                    CaptureRecorder.Remove(text);
                    Plugin.Log.LogWarning($"[auto] 命中敏感词，恒等入库: {text}");
                    sent++;
                    Sleep(QpsSpacingMs);
                    continue;
                }

                if (FatalCodes.Contains(e.Code))
                {
                    Plugin.Log.LogError(
                        $"[auto] 百度接口致命错误({e.Code})，自动翻译已停止，修复配置后重启游戏: {e.Message}"
                    );
                    _running = false;
                    return sent;
                }

                // 瞬态：52001 超时 / 52002 系统错误 / 54003 频率受限 / 54005 长 query 频繁 / 54004 余额不足
                var wait = e.Code == "54004" ? BalanceBackoffMs : FailureBackoffMs;
                Plugin.Log.LogWarning(
                    $"[auto] 翻译失败({e.Code})，本轮中止，{wait / 1000}s 后重试: {text} ({e.Message})"
                );
                Sleep(wait);
                return sent;
            }
            catch (Exception e)
            {
                // 网络层异常（超时/HTTP 失败）：留队重试
                Plugin.Log.LogWarning(
                    $"[auto] 网络异常，本轮中止，{FailureBackoffMs / 1000}s 后重试: {text} ({e.Message})"
                );
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
