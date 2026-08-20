// -*- coding: utf-8 -*-
// SingleBaiduTranslator.cs
// 纯 .NET 标准库实现百度翻译 API 单测（不依赖任何第三方库，仅 System.Net.Http / System.Text.Json）。
//
// 等价于：
//     from deep_translator import BaiduTranslator
//     translator = BaiduTranslator(source='auto', target='zh', appid="...", appkey="...")
//     print(translator.translate("リキーを発行する"))

using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class SingleBaiduTranslator
{
    private const string ApiUrl = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    private static readonly HttpClient HttpClient = new HttpClient();

    private readonly string _appid;
    private readonly string _appkey;
    private readonly string _source;
    private readonly string _target;

    public SingleBaiduTranslator(string appid, string appkey, string source = "auto", string target = "zh")
    {
        _appid = appid;
        _appkey = appkey;
        _source = source;
        _target = target;
    }

    public async Task<string> TranslateAsync(string text)
    {
        // 官方签名规则：sign = md5(appid + q + salt + 密钥)
        string salt = new Random().Next(10000, 100000).ToString();
        string sign = Md5(_appid + text + salt + _appkey);

        string query =
            "?q=" + Uri.EscapeDataString(text) +
            "&from=" + Uri.EscapeDataString(_source) +
            "&to=" + Uri.EscapeDataString(_target) +
            "&appid=" + Uri.EscapeDataString(_appid) +
            "&salt=" + Uri.EscapeDataString(salt) +
            "&sign=" + Uri.EscapeDataString(sign);

        using HttpResponseMessage response = await HttpClient.GetAsync(ApiUrl + query);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("error_code", out JsonElement errorCode))
        {
            string errorMsg = root.TryGetProperty("error_msg", out JsonElement msg)
                ? msg.GetString() ?? ""
                : "";
            throw new Exception($"百度翻译接口报错: code={errorCode}, msg={errorMsg}");
        }

        var sb = new StringBuilder();
        foreach (JsonElement item in root.GetProperty("trans_result").EnumerateArray())
        {
            sb.Append(item.GetProperty("dst").GetString());
        }
        return sb.ToString();
    }

    private static string Md5(string input)
    {
        using var md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    public static async Task Main()
    {
        var translator = new SingleBaiduTranslator(
            appid: "",
            appkey: "",
            source: "jp",
            target: "zh");

        string result = await translator.TranslateAsync("リキーを発行する");
        Console.WriteLine(result);
    }
}
