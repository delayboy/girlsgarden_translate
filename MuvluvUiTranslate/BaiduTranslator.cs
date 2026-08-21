using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MuvluvUiTranslate;

/// <summary>百度翻译 API 业务错误（响应含 error_code）。Code 为官方错误码表字符串。</summary>
public sealed class BaiduApiException : Exception
{
    public BaiduApiException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>
/// 百度翻译 API 客户端（移植自 tools/SingleBaiduTranslator.cs 演示代码，已调通）。
/// 纯标准库：HttpClient + MD5 签名 + System.Text.Json。jp → zh 固定。
/// 仅供 AutoTranslator 后台线程同步调用；API 业务错误抛 BaiduApiException（带错误码），
/// 网络异常抛其他 Exception，均由调用方按错误码分类处理。
/// </summary>
public sealed class BaiduTranslator
{
    private const string ApiUrl = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _appid;
    private readonly string _appkey;

    public BaiduTranslator(string appid, string appkey)
    {
        _appid = appid;
        _appkey = appkey;
    }

    /// <summary>翻译单条文本（阻塞式，后台线程专用）。API 报错/网络异常抛 Exception。</summary>
    public string Translate(string text)
    {
        // 官方签名规则：sign = md5(appid + q + salt + 密钥)
        var salt = Random.Shared.Next(10000, 100000).ToString();
        var sign = Md5(_appid + text + salt + _appkey);

        var query =
            "?q=" + Uri.EscapeDataString(text)
            + "&from=jp&to=zh"
            + "&appid=" + Uri.EscapeDataString(_appid)
            + "&salt=" + Uri.EscapeDataString(salt)
            + "&sign=" + Uri.EscapeDataString(sign);

        using var response = Http.Send(new HttpRequestMessage(HttpMethod.Get, ApiUrl + query));
        response.EnsureSuccessStatusCode();
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        if (root.TryGetProperty("error_code", out var errorCode))
        {
            var code = errorCode.GetString() ?? "";
            var errorMsg = root.TryGetProperty("error_msg", out var msg) ? msg.GetString() ?? "" : "";
            throw new BaiduApiException(code, $"百度翻译接口报错: code={code}, msg={errorMsg}");
        }

        var sb = new StringBuilder();
        foreach (var item in root.GetProperty("trans_result").EnumerateArray())
            sb.Append(item.GetProperty("dst").GetString());
        return sb.ToString();
    }

    private static string Md5(string input)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
