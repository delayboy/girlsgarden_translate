using BepInEx.Configuration;

namespace MuvluvUiTranslate;

public static class Config
{
    private static ConfigFile _config;

    public static ConfigEntry<bool> TranslationEnabled { get; private set; }
    public static ConfigEntry<bool> CaptureEnabled { get; private set; }
    public static ConfigEntry<int> CaptureLogLimit { get; private set; }
    public static ConfigEntry<bool> AutoTranslateEnabled { get; private set; }
    public static ConfigEntry<string> BaiduAppId { get; private set; }
    public static ConfigEntry<string> BaiduAppKey { get; private set; }

    public static void Initialize(ConfigFile config)
    {
        _config = config;

        TranslationEnabled = config.Bind(
            "Translation",
            "Enabled",
            true,
            "启用 UI 文本翻译（词典命中即替换）"
        );

        CaptureEnabled = config.Bind(
            "Capture",
            "Enabled",
            true,
            "捕获未翻译的日文文本到 translation/capture/zh_Hans.pending.json"
        );

        CaptureLogLimit = config.Bind(
            "Capture",
            "LogLimit",
            30,
            "每次游戏运行写入 BepInEx 日志的未翻译条目数上限（便于首轮排查，0 关闭）"
        );

        AutoTranslateEnabled = config.Bind(
            "AutoTranslate",
            "Enabled",
            true,
            "自动翻译：后台线程逐条调用百度翻译（jp→zh，1条/秒），把捕获的未翻译 core 实时写入词典并即时生效"
        );

        BaiduAppId = config.Bind(
            "AutoTranslate",
            "BaiduAppId",
            "",
            "百度翻译开放平台 APP ID（fanyi-api.baidu.com，免费版 QPS=1）"
        );

        BaiduAppKey = config.Bind(
            "AutoTranslate",
            "BaiduAppKey",
            "",
            "百度翻译开放平台密钥"
        );
    }

    public static void Shutdown() => _config?.Save();
}
