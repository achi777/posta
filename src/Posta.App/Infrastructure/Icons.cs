using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace Posta.App.Infrastructure;

/// <summary>Glyph code points of the Segoe Fluent Icons / Segoe MDL2 Assets fonts that ship with Windows.</summary>
public static class G
{
    public const string Mail = "", MailFill = "", Read = "", Send = "", Reply = "", ReplyAll = "", Forward = "";
    public const string Calendar = "", CalendarDay = "", CalendarWeek = "", CalendarReply = "", People = "", Contact = "", ContactInfo = "";
    public const string Group = "", AddFriend = "", Tasks = "", Completed = "", CheckMark = "", Accept = "", Cancel = "", Help = "";
    public const string Delete = "", Archive = "", Folder = "", FolderOpen = "", NewFolder = "", Move = "", Copy = "";
    public const string Rename = "", Tag = "", Flag = "", Find = "", Filter = "", Settings = "", Sync = "", Refresh = "";
    public const string Print = "", Save = "", SaveAs = "", Info = "", Share = "", Lock = "", Clock = "", Ringer = "";
    public const string MapPin = "", Repeat = "", Important = "", Down = "", Up = "", Attach = "", Edit = "";
    public const string Font = "", Bold = "", Italic = "", Underline = "", Bullets = "", Numbering = "";
    public const string AlignLeft = "", AlignCenter = "", AlignRight = "", Link = "", Picture = "", Table = "";
    public const string Undo = "", Redo = "", Cut = "", Paste = "", Highlight = "", FontColor = "", Zoom = "";
    public const string Emoji = "", Back = "", Fwd = "", ChevronLeft = "", ChevronRight = "", ChevronDown = "", ChevronUp = "";
    public const string Phone = "", Globe = "", Home = "", Close = "", Add = "", Remove = "", More = "", Blocked = "";
    public const string Note = "", Message = "", Feed = "", Warning = "", Signature = "", Contact2 = "", Ignore = "";
    public const string Clean = "", QuickStep = "", Rules = "", Unread = "", Business = "", List = "", View = "";
    public const string ReadingPane = "", Layout = "", Today = "", Schedule = "", Grid = "", Month = "", Workweek = "";
    public const string Person = "", OOF = "", Account = "", Exit = "", Open = "", Download = "", Upload = "";
    public const string Mention = "", Sort = "", Pin = "", Star = "", Light = "", Spell = "", Translate = "", ReadAloud = "";
    public const string Video = "", Location = "", Accessibility = "", Shield = "", Key = "", Mobile = "", Fax = "", Web = "";
    public const string Calendar2 = "", Category = "", Junk = "", Outbox = "", Drafts = "", Sent = "", Inbox = "", Deleted = "";
    public const string Send2 = "", Paperclip = "", Tentative = "", Decline = "", Propose = "", Busy = "", Overlay = "";
}

public static class GlyphFactory
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Typeface IconTypeface = new(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Dictionary<string, ImageSource> Cache = new();

    public static ImageSource Create(string glyph, Brush brush, double size = 32, string? badge = null, Brush? badgeBrush = null)
    {
        var key = $"{glyph}|{brush}|{size}|{badge}|{badgeBrush}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var img)) return img;
        }

        var group = new DrawingGroup();
        // Fixed square bounds so all icons align identically
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconTypeface, size, brush, 1.0);
        var geo = ft.BuildGeometry(new Point(0, 0));
        group.Children.Add(new GeometryDrawing(brush, null, geo));
        if (badge != null)
        {
            var bs = size * 0.55;
            var bft = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconTypeface, bs, badgeBrush ?? brush, 1.0);
            var bgeo = bft.BuildGeometry(new Point(size - bs + size * 0.05, size - bs + size * 0.05));
            // white halo behind the badge
            var halo = new EllipseGeometry(new Point(size - bs / 2 + size * 0.05, size - bs / 2 + size * 0.05), bs / 2, bs / 2);
            group.Children.Add(new GeometryDrawing(Brushes.White, null, halo));
            group.Children.Add(new GeometryDrawing(badgeBrush ?? brush, null, bgeo));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        lock (Cache) Cache[key] = image;
        return image;
    }

    public static Brush ParseBrush(string? color, Brush fallback)
    {
        if (string.IsNullOrEmpty(color)) return fallback;
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            return b;
        }
        catch { return fallback; }
    }

    public static readonly Brush DefaultBrush = Frozen(Color.FromRgb(0x42, 0x42, 0x42));
    public static readonly Brush Blue = Frozen(Color.FromRgb(0x0F, 0x6C, 0xBD));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// XAML: Icon="{ui:Glyph E715, Color=#0F6CBD}" – produces a vector ImageSource from a Windows icon-font glyph.
/// </summary>
[MarkupExtensionReturnType(typeof(ImageSource))]
public sealed class GlyphExtension : MarkupExtension
{
    public GlyphExtension() { }
    public GlyphExtension(string code) { Code = code; }

    [ConstructorArgument("code")]
    public string Code { get; set; } = "E715";
    public string? Color { get; set; }
    public double Size { get; set; } = 32;
    public string? Badge { get; set; }
    public string? BadgeColor { get; set; }

    private static string Decode(string code) =>
        code.Length <= 2 ? code : char.ConvertFromUtf32(int.Parse(code, NumberStyles.HexNumber));

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var brush = GlyphFactory.ParseBrush(Color, GlyphFactory.DefaultBrush);
        var badgeBrush = BadgeColor != null ? GlyphFactory.ParseBrush(BadgeColor, GlyphFactory.Blue) : null;
        return GlyphFactory.Create(Decode(Code), brush, Size, Badge != null ? Decode(Badge) : null, badgeBrush);
    }
}
