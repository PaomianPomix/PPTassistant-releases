using System.Text.RegularExpressions;

namespace PPTAssistant.InkML;

public static class InkMlNormalizer
{
    private static readonly Regex TraceElementPattern = new(
        @"(?<open><(?<prefix>[\w\-]*:)?trace\b[^>]*>)(?<body>.*?)(?<close></(?:\k<prefix>)?trace>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TimeStringPattern = new(
        @"(?<attr>timeString\s*=\s*"")(?<value>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?<rest>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex TrailingZerosPattern = new(
        @"(?<number>-?\d+)\.0+(?=\s|,|<|$)",
        RegexOptions.Compiled);

    /// <summary>
    /// 修复 WPS 引入的两处方言破坏。返回修复后的 XML；若无事可修则原样返回同一字符串实例。
    /// </summary>
    /// <remarks>幂等：对已修复的内容再跑一次不会改变结果。</remarks>
    public static string Normalize(string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return xml;
        }

        var result = xml;

        // ① 补回毫秒（严格三位）。
        result = TimeStringPattern.Replace(result, match =>
        {
            var rest = match.Groups["rest"].Value;
            // 已经有 .mmm 就别动（rest 以 . 开头且后面至少一位数字）。
            var hasMilliseconds = rest.StartsWith('.') &&
                                  rest.Length > 1 &&
                                  char.IsAsciiDigit(rest[1]);
            var suffix = hasMilliseconds ? string.Empty : ".000";
            return $"{match.Groups["attr"].Value}{match.Groups["value"].Value}{suffix}{rest}\"";
        });

        // ② 去掉 trace 文本里整型值尾随的 .000（只处理 trace 元素内部）。
        result = TraceElementPattern.Replace(result, match =>
        {
            var body = TrailingZerosPattern.Replace(match.Groups["body"].Value, "${number}");
            return match.Groups["open"].Value + body + match.Groups["close"].Value;
        });

        return result;
    }

    /// <summary>这份 XML 是否带有 WPS 引入的破坏（用于日志/诊断，避免无谓重写）。</summary>
    public static bool NeedsNormalization(string xml)
        => !string.IsNullOrEmpty(xml) && !string.Equals(Normalize(xml), xml, StringComparison.Ordinal);
}
