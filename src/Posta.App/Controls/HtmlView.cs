using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Posta.App.Services;
using Posta.Core.Services;

namespace Posta.App.Controls;

/// <summary>
/// Read-only HTML message renderer (reading pane / opened items). Scripts disabled, links open in the browser,
/// external pictures blocked unless allowed.
/// </summary>
public sealed partial class HtmlView : Border
{
    private readonly WebView2 _web = new();
    private Task? _init;
    private bool _allowExternal;
    private string? _pendingHtml;
    private double _zoom = 1.0;

    public event EventHandler<bool>? ExternalContentDetected;

    public HtmlView()
    {
        Child = _web;
        _web.DefaultBackgroundColor = System.Drawing.Color.White;
        Loaded += (_, _) => _init ??= InitAsync();
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            if (_web.CoreWebView2 != null) _web.ZoomFactor = value;
        }
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await AppServices.Current.GetWebViewEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.IsScriptEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.IsZoomControlEnabled = true;
            s.IsBuiltInErrorPageEnabled = false;
            _web.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            _web.ZoomFactor = _zoom;
            _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Infrastructure.Ui.OpenUrl(e.Uri);
            };
            _web.CoreWebView2.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            _web.CoreWebView2.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            _web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                if (!_allowExternal)
                    e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            if (_pendingHtml != null)
            {
                var h = _pendingHtml;
                _pendingHtml = null;
                NavigateHtml(h);
            }
        }
        catch (Exception ex)
        {
            Child = new TextBlock { Text = "WebView2: " + ex.Message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && uri.Contains("/posta-view-", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            MailtoRequested?.Invoke(this, uri);
            return;
        }
        if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Infrastructure.Ui.OpenUrl(uri);
    }

    public event EventHandler<string>? MailtoRequested;

    [GeneratedRegex(@"<img[^>]+src\s*=\s*[""']?https?://", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalImgRx();

    [GeneratedRegex(@"url\(\s*['""]?https?://", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalCssRx();

    public void ShowMessage(string bodyHtml, bool allowExternal)
    {
        _allowExternal = allowExternal;
        var safe = HtmlUtil.Sanitize(bodyHtml);
        var hasExternal = ExternalImgRx().IsMatch(safe) || ExternalCssRx().IsMatch(safe);
        ExternalContentDetected?.Invoke(this, hasExternal && !allowExternal);
        NavigateHtml(Wrap(safe));
    }

    public void Clear() => NavigateHtml("<html><body style='background:#fff'></body></html>");

    public static string Wrap(string body)
    {
        const string css = "html,body{margin:0;padding:0;background:#fff;color-scheme:light;}body{padding:14px 18px;font-family:'Aptos','Calibri','Segoe UI','Sylfaen',sans-serif;font-size:11pt;color:#201f1e;word-wrap:break-word;overflow-wrap:anywhere;}" +
                           "img{max-width:100%;height:auto;}table{max-width:100%;}pre{white-space:pre-wrap;}a{color:#0563c1;}blockquote{margin:0 0 0 .8ex;border-left:2px solid #c8c6c4;padding-left:1ex;}";
        if (body.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            // Inject our base CSS before the message's own styles so the message styles win.
            var idx = body.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var end = body.IndexOf('>', idx);
                if (end > 0) return body.Insert(end + 1, $"<meta charset=\"utf-8\"><style>{css}</style>");
            }
            return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{css}</style></head><body>{HtmlUtil.BodyContent(body)}</body></html>";
        }
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{css}</style></head><body>{body}</body></html>";
    }

    private void NavigateHtml(string html)
    {
        if (_web.CoreWebView2 == null)
        {
            _pendingHtml = html;
            return;
        }
        // NavigateToString is limited to ~2 MB – larger messages are rendered from a temp file.
        if (html.Length > 1_500_000)
        {
            var dir = Path.Combine(SettingsService.LocalFolder, "temp");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"posta-view-{Guid.NewGuid():N}.html");
            File.WriteAllText(file, html);
            _web.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
        }
        else
        {
            _web.CoreWebView2.NavigateToString(html);
        }
    }

    public void Print()
    {
        try { _web.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (_web.CoreWebView2 == null) return "";
        try
        {
            var r = await _web.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
            return System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "";
        }
        catch { return ""; }
    }
}
