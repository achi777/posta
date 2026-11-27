using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Posta.Core.Services;

public static partial class HtmlUtil
{
    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockTagsRx();

    [GeneratedRegex(@"<br\s*/?>|</p\s*>|</div\s*>|</tr\s*>|</li\s*>|</h[1-6]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRx();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpacesRx();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex MultiNewlineRx();

    [GeneratedRegex(@"<body[^>]*>(.*)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRx();

    [GeneratedRegex(@"<script[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRx();

    [GeneratedRegex(@"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttrRx();

    [GeneratedRegex(@"(href|src)\s*=\s*([""'])\s*javascript:[^""']*\2", RegexOptions.IgnoreCase)]
    private static partial Regex JsUrlRx();

    [GeneratedRegex(@"<(iframe|object|embed|form|meta[^>]*http-equiv)[^>]*>(.*?</(iframe|object|embed|form)>)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousRx();

    [GeneratedRegex(@"(https?://[^\s<>""']+|www\.[^\s<>""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = BlockTagsRx().Replace(html, "");
        s = LineBreakRx().Replace(s, "\n");
        s = TagRx().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = SpacesRx().Replace(s, " ");
        s = MultiNewlineRx().Replace(s, "\n\n");
        return s.Trim();
    }

    public static string Preview(string? html, int max = 255)
    {
        var t = ToPlainText(html).Replace('\r', ' ').Replace('\n', ' ');
        t = SpacesRx().Replace(t, " ").Trim();
        return t.Length > max ? t[..max] : t;
    }

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    public static string PlainTextToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var enc = WebUtility.HtmlEncode(text);
        enc = UrlRx().Replace(enc, m =>
        {
            var url = m.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + m.Value : m.Value;
            return $"<a href=\"{url}\">{m.Value}</a>";
        });
        return "<div style=\"font-family:Consolas,'Courier New',monospace;white-space:pre-wrap\">" + enc.Replace("\r\n", "\n").Replace("\n", "<br>") + "</div>";
    }

    /// <summary>Returns the inner content of the body element (or the whole string if it is a fragment).</summary>
    public static string BodyContent(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var m = BodyRx().Match(html);
        return m.Success ? m.Groups[1].Value : html;
    }

    /// <summary>Removes active content (scripts, event handlers, frames, forms) from received HTML.</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = ScriptRx().Replace(html, "");
        s = DangerousRx().Replace(s, "");
        s = EventAttrRx().Replace(s, "");
        s = JsUrlRx().Replace(s, "$1=\"#\"");
        return s;
    }

    public static string RecipientsLine(IEnumerable<Models.Recipient> recipients) =>
        string.Join("; ", recipients.Select(r => string.IsNullOrWhiteSpace(r.Name) ? r.Address : r.Name));

    public static string RecipientsLineFull(IEnumerable<Models.Recipient> recipients) =>
        string.Join("; ", recipients.Select(r => r.FullDisplay));

    public static string AppendSignatureSeparator(string bodyHtml) => bodyHtml;

    public static string StripSignatureDiv(string html)
    {
        var start = html.IndexOf("<div id=\"Signature\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return html;
        var end = html.IndexOf("<!--/Signature-->", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return html;
        return html.Remove(start, end + "<!--/Signature-->".Length - start);
    }

    public static string WrapSignature(string? signatureHtml) =>
        string.IsNullOrWhiteSpace(signatureHtml)
            ? "<div id=\"Signature\"></div><!--/Signature-->"
            : "<div id=\"Signature\">" + BodyContent(signatureHtml) + "</div><!--/Signature-->";

    public static string SubjectPrefix(string subject, string prefix)
    {
        var s = subject ?? "";
        var known = new[] { "RE:", "FW:", "FWD:", "AW:", "WG:", "SV:", "VS:", "TR:", "RV:" };
        if (known.Any(k => s.StartsWith(k, StringComparison.OrdinalIgnoreCase)) && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return s;
        return prefix + " " + s;
    }

    public static string FileSafe(string name)
    {
        var sb = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        var r = sb.ToString().Trim();
        if (r.Length > 120) r = r[..120];
        return string.IsNullOrWhiteSpace(r) ? "message" : r;
    }
}
