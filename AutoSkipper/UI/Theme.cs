namespace AutoSkipper.UI;

// Colours, brushes, fonts and button styles shared by the dark UI.
internal static class Theme
{
    public static readonly Color BackgroundColor = Color.FromArgb(30, 30, 30);
    public static readonly Color AltRowColor = Color.FromArgb(35, 35, 35);
    public static readonly Color PanelColor = Color.FromArgb(37, 37, 38);
    public static readonly Color FieldColor = Color.FromArgb(45, 45, 45);
    public static readonly Color BorderColor = Color.FromArgb(50, 50, 50);
    public static readonly Color ButtonColor = Color.FromArgb(60, 60, 60);
    public static readonly Color ForegroundColor = Color.FromArgb(204, 204, 204);
    public static readonly Color MutedColor = Color.FromArgb(150, 150, 150);
    public static readonly Color AccentColor = Color.FromArgb(0, 122, 204);
    public static readonly Color KeyColor = Color.FromArgb(86, 156, 214);
    public static readonly Color DangerColor = Color.FromArgb(244, 71, 71);
    public static readonly Color WarningColor = Color.FromArgb(206, 145, 120);

    // GDI objects for owner-draw handlers, created once instead of on every paint.
    public static readonly SolidBrush BackgroundBrush = new(BackgroundColor);
    public static readonly SolidBrush AltRowBrush = new(AltRowColor);
    public static readonly SolidBrush PanelBrush = new(PanelColor);
    public static readonly SolidBrush AccentBrush = new(AccentColor);
    public static readonly SolidBrush ForegroundBrush = new(ForegroundColor);
    public static readonly SolidBrush MutedBrush = new(MutedColor);

    public static readonly Font UiFont = new("Segoe UI", 9);
    public static readonly Font SmallFont = new("Segoe UI", 8.5f);
    public static readonly Font HeaderFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    public static readonly Font ListFont = new("Segoe UI", 9.5f);
    public static readonly Font TabFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font KeyFont = new("Segoe UI", 9, FontStyle.Bold);
    public static readonly Font MonoFont = CreateMonoFont(10);

    public static Button CreateButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = ForegroundColor, BackColor = ButtonColor, Cursor = Cursors.Hand,
            Font = UiFont, Padding = new Padding(4, 1, 4, 1)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = AccentColor;
        btn.Click += onClick;
        return btn;
    }

    public static Button CreateTabButton(string text, bool active)
    {
        var btn = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = ForegroundColor, BackColor = active ? AccentColor : PanelColor,
            Cursor = Cursors.Hand, Font = TabFont, Padding = new Padding(8, 2, 8, 2)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = AccentColor;
        return btn;
    }

    public static void SetTabActive(Button tab, bool active) => tab.BackColor = active ? AccentColor : PanelColor;

    // Cascadia Mono ships with newer Windows / Windows Terminal; fall back to
    // Consolas, then any monospace font. A missing family would otherwise
    // silently become a proportional font.
    private static Font CreateMonoFont(float size)
    {
        foreach (string name in new[] { "Cascadia Mono", "Consolas" })
        {
            var font = new Font(name, size);
            if (font.Name == name) return font;
            font.Dispose();
        }
        return new Font(FontFamily.GenericMonospace, size);
    }
}
