using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LegalPilot.Api.Application;

public static partial class HtmlSanitizer
{
    public const int MaxAnalysisCharacters = 1_000_000;
    public const int MaxStoredBodyCharacters = 1_000_000;

    public static string ToLegalInnerText(string? value)
    {
        var text = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = CommentsRegex().Replace(text, " ");
        text = BlockRegex("script").Replace(text, " ");
        text = BlockRegex("style").Replace(text, " ");
        text = BlockRegex("head").Replace(text, " ");
        text = HiddenElementRegex().Replace(text, " ");
        text = LineBreakTagRegex().Replace(text, "\n");
        text = ParagraphTagRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = RemoveStandardFooters(text);
        text = NormalizeText(text);
        return text;
    }

    public static string ClipForStorage(string text)
    {
        if (text.Length <= MaxStoredBodyCharacters)
        {
            return text;
        }

        return text[..MaxStoredBodyCharacters];
    }

    public static string ClipForAnalysis(string text)
    {
        if (text.Length <= MaxAnalysisCharacters)
        {
            return text;
        }

        return text[..MaxAnalysisCharacters];
    }

    private static string RemoveStandardFooters(string value)
    {
        var markers = new[]
        {
            "utilidad solo para informaci",
            "la informacion contenida en este mensaje es confidencial",
            "este mensaje y sus archivos adjuntos son confidenciales"
        };
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        var cutoff = value.Length;
        foreach (var marker in markers)
        {
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                cutoff = Math.Min(cutoff, index);
            }
        }

        return cutoff < value.Length ? value[..cutoff] : value;
    }

    private static string NormalizeText(string value)
    {
        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => InlineWhitespaceRegex().Replace(line.Trim(), " "))
            .Where(line => line.Length > 0)
            .ToArray();

        return string.Join('\n', lines).Trim();
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static Regex BlockRegex(string tag) =>
        new($@"<\s*{tag}\b[^>]*>.*?<\s*/\s*{tag}\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex CommentsRegex();

    [GeneratedRegex(@"<(?<tag>[a-z][a-z0-9]*)\b[^>]*(?:display\s*:\s*none|visibility\s*:\s*hidden|mso-hide\s*:\s*all|\shidden\b|aria-hidden\s*=\s*[""']?true)[^>]*>.*?</\k<tag>>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex HiddenElementRegex();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<\s*/?\s*(p|div|section|article|tr|td|li|h[1-6]|table|tbody|thead|ul|ol)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ParagraphTagRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.Compiled)]
    private static partial Regex InlineWhitespaceRegex();
}
