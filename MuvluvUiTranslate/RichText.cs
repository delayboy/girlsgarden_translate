using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MuvluvUiTranslate;

/// <summary>
/// 三级 token 拆分（与 tools/split_example.py 保持一致，修改任一侧需同步另一侧）：
///   TAG  —— TMP 富文本标签，原样保留不翻译；
///   SEP  —— 空白（含 \n、全角空格）+ 全角标点 ！？。…‥、：；「」『』【】（），
///           原样保留（不翻译、不归化，重组时贴回，布局零损失）；
///   CORE —— 其余文本段，唯一翻译单元。CORE 天然不含任何空白/标签/边缘标点，
///           直接作为词典 key（纯文本）。
/// 刻意不拆：・（中文译名用，姜小花・拉沃雅）、ー（片假名长音，パーティー）、
///           ASCII 标点/数字/字母（1.5倍、50%、Lv.3 不能断）。
/// </summary>
internal static class RichText
{
    // 分组1：<tag>（标签内无 < 无换行）；或 空白+全角标点 的连续 run。
    // 标点以 \u 转义书写避免源码编码差异：
    //   \uFF01！ \uFF1F？ \u3002。 \u2026… \u2025‥ \u3001、
    //   \uFF1A： \uFF1B； \u300C「 \u300D」 \u300E『 \u300F』 \u3010【 \u3011】 \uFF08（ \uFF09）
    private static readonly Regex TokenRe = new(
        @"(<[^<>\n]+>|[\s\uFF01\uFF1F\u3002\u2026\u2025\u3001\uFF1A\uFF1B\u300C\u300D\u300E\u300F\u3010\u3011\uFF08\uFF09]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    public enum TokenKind
    {
        Tag,
        Sep,
        Core,
    }

    public readonly struct Token
    {
        public Token(TokenKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public TokenKind Kind { get; }

        public string Text { get; }
    }

    /// <summary>整串 → token 列表。分块完备：拼接所有 token 恒等于原文。</summary>
    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var pos = 0;
        foreach (Match m in TokenRe.Matches(text))
        {
            if (m.Index > pos)
                tokens.Add(new Token(TokenKind.Core, text[pos..m.Index]));
            var s = m.Value;
            tokens.Add(
                s[0] == '<' ? new Token(TokenKind.Tag, s) : new Token(TokenKind.Sep, s)
            );
            pos = m.Index + s.Length;
        }
        if (pos < text.Length)
            tokens.Add(new Token(TokenKind.Core, text[pos..]));
        return tokens;
    }

    /// <summary>整串 → core 列表（捕获通道只记这些纯文本）。</summary>
    public static List<string> ExtractCores(string text)
    {
        var cores = new List<string>();
        foreach (var t in Tokenize(text))
        {
            if (t.Kind == TokenKind.Core)
                cores.Add(t.Text);
        }
        return cores;
    }
}
