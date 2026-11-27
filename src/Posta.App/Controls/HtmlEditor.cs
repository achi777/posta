using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Posta.App.Services;

namespace Posta.App.Controls;

public sealed class EditorState
{
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public bool Bullets { get; set; }
    public bool Numbering { get; set; }
    public string Align { get; set; } = "left";
    public string FontName { get; set; } = "";
    public string FontSize { get; set; } = "";
}

/// <summary>
/// Rich HTML editor (WebView2 + contenteditable) driven by the ribbon "Format Text" commands,
/// used for composing mail, signatures, automatic replies and item bodies.
/// </summary>
public sealed class HtmlEditor : Border
{
    private readonly WebView2 _web = new();
    private readonly TaskCompletionSource _ready = new();
    private Task? _init;
    private string _pendingHtml = "";

    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler? ContentChanged;
    public event EventHandler<string>? LinkClicked;

    public string DefaultFont { get; set; } = "Aptos, Calibri, 'Segoe UI', Sylfaen, sans-serif";
    public int DefaultSizePt { get; set; } = 11;
    public string DefaultColor { get; set; } = "#000000";
    public bool IsDirty { get; set; }
    public bool PlainText { get; set; }
    public double MinBodyHeight { get; set; }

    public HtmlEditor()
    {
        Child = _web;
        _web.DefaultBackgroundColor = System.Drawing.Color.White;
        Loaded += (_, _) => _init ??= InitAsync();
    }

    public Task Ready => _ready.Task;

    private async Task InitAsync()
    {
        try
        {
            var env = await AppServices.Current.GetWebViewEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.IsZoomControlEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = false;
            _web.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            _web.CoreWebView2.WebMessageReceived += OnMessage;
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; Infrastructure.Ui.OpenUrl(e.Uri); };
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("data:") || e.Uri.StartsWith("about:")) return;
                e.Cancel = true;
            };
            var tcs = new TaskCompletionSource();
            void Done(object? o, CoreWebView2NavigationCompletedEventArgs a) { _web.CoreWebView2.NavigationCompleted -= Done; tcs.TrySetResult(); }
            _web.CoreWebView2.NavigationCompleted += Done;
            _web.CoreWebView2.NavigateToString(Shell());
            await tcs.Task;
            await Exec($"ed.setHtml({JsonSerializer.Serialize(_pendingHtml)})");
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            Child = new TextBlock { Text = "WebView2: " + ex.Message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
            _ready.TrySetException(ex);
        }
    }

    private string Shell()
    {
        var font = DefaultFont.Replace("\"", "'");
        var shell = """
<!DOCTYPE html><html><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;height:100%;background:#fff;color-scheme:light;}
body{font-family:%FONT%;font-size:%SIZE%pt;color:%COLOR%;}
#ed{outline:none;padding:10px 14px 30px 14px;min-height:calc(100% - 40px);word-wrap:break-word;overflow-wrap:anywhere;%PLAIN%}
#ed img{max-width:100%;}
#ed table{border-collapse:collapse;}
#ed td,#ed th{border:1px solid #a6a6a6;padding:3px 6px;min-width:40px;}
#ed blockquote{margin:0 0 0 .8ex;border-left:2px solid #c8c6c4;padding-left:1ex;}
#ed p{margin:0;}
</style></head><body><div id="ed" contenteditable="true" spellcheck="true"></div>
<script>
const box=document.getElementById('ed');
function post(o){try{window.chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}
function state(){
  let fs='';try{const s=window.getSelection();if(s.rangeCount){let n=s.getRangeAt(0).startContainer;if(n.nodeType==3)n=n.parentNode;const cs=getComputedStyle(n);fs=Math.round(parseFloat(cs.fontSize)*0.75*2)/2+'';var fn=cs.fontFamily.split(',')[0].replace(/["']/g,'');}}catch(e){}
  post({type:'state',bold:document.queryCommandState('bold'),italic:document.queryCommandState('italic'),underline:document.queryCommandState('underline'),
    strike:document.queryCommandState('strikeThrough'),bullets:document.queryCommandState('insertUnorderedList'),numbering:document.queryCommandState('insertOrderedList'),
    align:document.queryCommandState('justifyCenter')?'center':document.queryCommandState('justifyRight')?'right':document.queryCommandState('justifyFull')?'justify':'left',
    fontName:fn||'',fontSize:fs});
}
document.addEventListener('selectionchange',state);
box.addEventListener('input',()=>post({type:'dirty'}));
box.addEventListener('keydown',e=>{
  if(e.ctrlKey&&e.key==='Enter'){e.preventDefault();post({type:'key',key:'send'});}
  else if(e.ctrlKey&&(e.key==='s'||e.key==='S')){e.preventDefault();post({type:'key',key:'save'});}
  else if(e.ctrlKey&&(e.key==='k'||e.key==='K')){e.preventDefault();post({type:'key',key:'link'});}
  else if(e.key==='Tab'){e.preventDefault();document.execCommand(e.shiftKey?'outdent':'indent');}
});
box.addEventListener('paste',e=>{
  const items=(e.clipboardData||{}).items||[];
  for(const it of items){ if(it.type&&it.type.indexOf('image')===0){ e.preventDefault(); const f=it.getAsFile(); const r=new FileReader();
    r.onload=()=>{document.execCommand('insertHTML',false,'<img src="'+r.result+'">');post({type:'dirty'});}; r.readAsDataURL(f); return; } }
});
box.addEventListener('drop',e=>{
  const files=(e.dataTransfer||{}).files||[];
  if(files.length){ e.preventDefault(); for(const f of files){ if(f.type.indexOf('image')===0){const r=new FileReader();r.onload=()=>{document.execCommand('insertHTML',false,'<img src="'+r.result+'">');post({type:'dirty'});};r.readAsDataURL(f);} } }
});
box.addEventListener('click',e=>{ if(e.ctrlKey){const a=e.target.closest('a'); if(a){e.preventDefault();post({type:'link',href:a.href});}}});
window.ed={
  setHtml(h){box.innerHTML=h||'<div><br></div>';},
  getHtml(){return box.innerHTML;},
  getText(){return box.innerText;},
  exec(c,v){box.focus();document.execCommand(c,false,v===undefined?null:v);state();post({type:'dirty'});},
  insertHtml(h){box.focus();document.execCommand('insertHTML',false,h);post({type:'dirty'});},
  fontSize(pt){box.focus();document.execCommand('fontSize',false,'7');for(const f of box.querySelectorAll('font[size="7"]')){f.removeAttribute('size');f.style.fontSize=pt+'pt';}state();post({type:'dirty'});},
  focusStart(){box.focus();const r=document.createRange();r.setStart(box,0);r.collapse(true);const s=window.getSelection();s.removeAllRanges();s.addRange(r);},
  setSignature(h){let s=document.getElementById('Signature');if(!s){s=document.createElement('div');s.id='Signature';const br=document.createElement('div');br.innerHTML='<br>';box.insertBefore(br,box.firstChild);box.insertBefore(s,br.nextSibling);}s.innerHTML=h;post({type:'dirty'});},
  formatBlock(t){box.focus();document.execCommand('formatBlock',false,t);post({type:'dirty'});},
  zoom(z){document.body.style.zoom=z;},
  hasText(re){return new RegExp(re,'i').test(box.innerText.split(/\n\s*(From|გამგზავნი):/)[0]);}
};
</script></body></html>
""";
        return shell.Replace("%FONT%", font).Replace("%SIZE%", DefaultSizePt.ToString()).Replace("%COLOR%", DefaultColor)
            .Replace("%PLAIN%", PlainText ? "white-space:pre-wrap;font-family:Consolas,'Courier New',monospace;" : "");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "dirty":
                    IsDirty = true;
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "state":
                    StateChanged?.Invoke(this, new EditorState
                    {
                        Bold = root.GetProperty("bold").GetBoolean(),
                        Italic = root.GetProperty("italic").GetBoolean(),
                        Underline = root.GetProperty("underline").GetBoolean(),
                        Strike = root.GetProperty("strike").GetBoolean(),
                        Bullets = root.GetProperty("bullets").GetBoolean(),
                        Numbering = root.GetProperty("numbering").GetBoolean(),
                        Align = root.GetProperty("align").GetString() ?? "left",
                        FontName = root.GetProperty("fontName").GetString() ?? "",
                        FontSize = root.GetProperty("fontSize").GetString() ?? ""
                    });
                    break;
                case "key":
                    KeyCommand?.Invoke(this, root.GetProperty("key").GetString() ?? "");
                    break;
                case "link":
                    LinkClicked?.Invoke(this, root.GetProperty("href").GetString() ?? "");
                    break;
            }
        }
        catch { }
    }

    public event EventHandler<string>? KeyCommand;

    private async Task<string> Exec(string script)
    {
        if (_web.CoreWebView2 == null) return "null";
        return await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    public async Task SetHtmlAsync(string html)
    {
        if (!_ready.Task.IsCompleted)
        {
            _pendingHtml = html;
            if (_web.CoreWebView2 == null) return;
        }
        await Exec($"ed.setHtml({JsonSerializer.Serialize(html)})");
        IsDirty = false;
    }

    public async Task<string> GetHtmlAsync()
    {
        if (!_ready.Task.IsCompleted) return _pendingHtml;
        var r = await Exec("ed.getHtml()");
        return JsonSerializer.Deserialize<string>(r) ?? "";
    }

    public async Task<string> GetTextAsync()
    {
        if (!_ready.Task.IsCompleted) return Core.Services.HtmlUtil.ToPlainText(_pendingHtml);
        var r = await Exec("ed.getText()");
        return JsonSerializer.Deserialize<string>(r) ?? "";
    }

    public async Task<bool> MatchesAsync(string regex)
    {
        if (!_ready.Task.IsCompleted) return false;
        var r = await Exec($"ed.hasText({JsonSerializer.Serialize(regex)})");
        return r == "true";
    }

    public Task Command(string command, string? value = null) =>
        Exec(value == null ? $"ed.exec({JsonSerializer.Serialize(command)})" : $"ed.exec({JsonSerializer.Serialize(command)},{JsonSerializer.Serialize(value)})");

    public Task InsertHtml(string html) => Exec($"ed.insertHtml({JsonSerializer.Serialize(html)})");
    public Task SetFontSize(double pt) => Exec($"ed.fontSize({pt.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    public Task FormatBlock(string tag) => Exec($"ed.formatBlock({JsonSerializer.Serialize(tag)})");
    public Task SetSignature(string html) => Exec($"ed.setSignature({JsonSerializer.Serialize(html)})");
    public Task FocusStart() => Exec("ed.focusStart()");
    public Task SetZoom(double z) => Exec($"ed.zoom({z.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

    public new void Focus()
    {
        _web.Focus();
        _ = Exec("document.getElementById('ed').focus()");
    }

    public void Print()
    {
        try { _web.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }
}
