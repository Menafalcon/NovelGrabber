using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NovelGrabber;

/// <summary>Centralised palette (Dark / Sepia / Light, matching the reader's themes) +
/// helpers and a few custom-painted controls. Custom controls read these statics at paint
/// time, so switching palettes + a Retheme walk restyles the whole live app.</summary>
public static class Theme
{
    public static Color Bg        { get; private set; }
    public static Color Panel     { get; private set; }
    public static Color Card      { get; private set; }
    public static Color CardHover { get; private set; }
    public static Color CardFill  { get; private set; }   // novel-card body
    public static Color Border    { get; private set; }
    public static Color Text      { get; private set; }
    public static Color SubText   { get; private set; }
    public static Color Accent    { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color Danger    { get; private set; }
    public static Color Success   { get; private set; }
    public static string Current  { get; private set; } = "";

    static Theme() => Apply("dark");

    private static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>Sets the active palette (matches the reader's dark/sepia/light).</summary>
    public static void Apply(string name)
    {
        Current = name;
        switch (name)
        {
            case "sepia":
                Bg = C(0xF3EAD6); Panel = C(0xEDE2C8); Card = C(0xE7DABC); CardHover = C(0xDECFA9);
                CardFill = C(0xEBDFC4); Border = C(0xD5C5A2); Text = C(0x3D2F1E); SubText = C(0x7A6A52);
                Accent = C(0x9C6B2F); AccentHover = C(0xB07F3F); Danger = C(0xC0392B); Success = C(0x2F8F4E);
                break;
            case "darksepia":
                Bg = C(0x1F1A13); Panel = C(0x262019); Card = C(0x2C251C); CardHover = C(0x352D22);
                CardFill = C(0x2A231A); Border = C(0x453A2B); Text = C(0xE8DCC8); SubText = C(0xA6957D);
                Accent = C(0xC89858); AccentHover = C(0xD8AC70); Danger = C(0xE06B5A); Success = C(0x7FB069);
                break;
            case "light":
                Bg = C(0xF2F4F8); Panel = C(0xFFFFFF); Card = C(0xFFFFFF); CardHover = C(0xEFF2F6);
                CardFill = C(0xF7F9FC); Border = C(0xE3E6EB); Text = C(0x20242C); SubText = C(0x6B7280);
                Accent = C(0x2563EB); AccentHover = C(0x3B76F0); Danger = C(0xDC2626); Success = C(0x16A34A);
                break;
            default:
                Bg = C(0x101217); Panel = C(0x16191F); Card = C(0x1C2029); CardHover = C(0x232833);
                CardFill = C(0x20252F); Border = C(0x2A303C); Text = C(0xE7EAF0); SubText = C(0x97A0B0);
                Accent = C(0x6C8CFF); AccentHover = C(0x86A0FF); Danger = C(0xFF6B6B); Success = C(0x4ADE80);
                break;
        }
    }

    /// <summary>Switches palettes and restyles every live control by remapping the old
    /// palette's colors onto the new one (custom paints pick the statics up automatically).</summary>
    public static void Switch(string name, params Control[] roots)
    {
        var old = (Bg, Panel, Card, CardHover, CardFill, Border, Text, SubText, Danger);
        Apply(name);
        foreach (var r in roots) { Walk(r, old); r.Invalidate(true); }
        ReapplyScrollbars();   // native scrollbars flip dark ↔ light with the palette
    }

    private static void Walk(Control c, (Color bg, Color panel, Color card, Color hover, Color fill, Color border, Color text, Color sub, Color danger) o)
    {
        Color MapB(Color x) => x == o.bg ? Bg : x == o.panel ? Panel : x == o.card ? Card
                             : x == o.hover ? CardHover : x == o.fill ? CardFill : x == o.border ? Border : x;
        Color MapF(Color x) => x == o.text ? Text : x == o.sub ? SubText : x == o.danger ? Danger
                             : x == Color.Gainsboro ? Text : x;
        try { c.BackColor = MapB(c.BackColor); c.ForeColor = MapF(c.ForeColor); } catch { }
        foreach (Control k in c.Controls) Walk(k, o);
    }

    public static readonly Font FontBase  = new("Segoe UI", 9.5f);
    public static readonly Font FontSemi  = new("Segoe UI Semibold", 9.5f);
    public static readonly Font FontTitle = new("Segoe UI Semibold", 15f);
    public static readonly Font FontSub   = new("Segoe UI", 8.5f);
    public static readonly Font FontMono  = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Point);

    // ----- Win11 dark titlebar / rounded corners -----
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr h, string appName, string? subIdList);
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    private const int WM_THEMECHANGED = 0x031A;

    // scrollbar-themed controls are remembered so theme switches can restyle them live
    private static readonly List<WeakReference<Control>> SbReg = new();
    private static bool DarkUi => Current is "dark" or "darksepia";

    private static void ThemeScrollbar(Control c)
    {
        try
        {
            SetWindowTheme(c.Handle, DarkUi ? "DarkMode_Explorer" : "Explorer", null);
            SendMessage(c.Handle, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
            // existing native scrollbars keep their old skin until recreated — toggle AutoScroll to force it
            if (c is ScrollableControl { AutoScroll: true } sc)
            { sc.AutoScroll = false; sc.AutoScroll = true; }
        }
        catch { }
    }

    /// <summary>Registers a control's native scrollbars to follow the active theme.</summary>
    public static void DarkScrollbars(Control c)
    {
        SbReg.Add(new WeakReference<Control>(c));
        if (c.IsHandleCreated) ThemeScrollbar(c);
        c.HandleCreated += (_, _) => ThemeScrollbar(c);
    }

    /// <summary>Re-themes every registered scrollbar to the current palette. Public so callers
    /// can re-run it after they rebuild content that recreated native scrollbars.</summary>
    public static void RefreshScrollbars()
    {
        for (int i = SbReg.Count - 1; i >= 0; i--)
        {
            if (!SbReg[i].TryGetTarget(out var c) || c.IsDisposed) { SbReg.RemoveAt(i); continue; }
            if (c.IsHandleCreated) ThemeScrollbar(c);
        }
    }
    private static void ReapplyScrollbars() => RefreshScrollbars();

    public static void ApplyWindowChrome(Form f)
    {
        try
        {
            int dark = Current == "dark" ? 1 : 0;
            DwmSetWindowAttribute(f.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;                                              // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            DwmSetWindowAttribute(f.Handle, 33, ref round, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Flat, rounded, hover-aware button with Accent / Ghost / Subtle / Danger styles.</summary>
public sealed class FlatButton : Button
{
    public enum Kind { Accent, Ghost, Subtle, Danger }
    public Kind Style { get; set; } = Kind.Subtle;
    public int Radius { get; set; } = 8;
    private bool _hover, _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.FontSemi;
        Cursor = Cursors.Hand;
        Height = 36;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);

        Color fill, fg, border = Color.Empty;
        switch (Style)
        {
            case Kind.Accent: fill = _down ? Theme.Accent : (_hover ? Theme.AccentHover : Theme.Accent); fg = Color.White; break;
            case Kind.Danger: fill = _hover ? Color.FromArgb(0xFF, 0x82, 0x82) : Theme.Danger; fg = Color.White; break;
            case Kind.Ghost:  fill = _hover ? Theme.CardHover : Color.Transparent; fg = Theme.Text; border = Theme.Border; break;
            default:          fill = _hover ? Theme.CardHover : Theme.Card; fg = Theme.Text; break;
        }
        if (!Enabled) { fill = Theme.Card; fg = Theme.SubText; }

        if (fill != Color.Transparent) { using var b = new SolidBrush(fill); g.FillPath(b, path); }
        if (border != Color.Empty) { using var pen = new Pen(border); g.DrawPath(pen, path); }

        TextRenderer.DrawText(g, Text, Font, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Rounded card panel with a subtle border.</summary>
public sealed class Card : Panel
{
    public int Radius { get; set; } = 12;
    public Color Stroke { get; set; } = Theme.Border;
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);
        using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
        using var pen = new Pen(Stroke);
        g.DrawPath(pen, path);
    }
}

/// <summary>Slim, accent-coloured progress bar with rounded ends.</summary>
public sealed class ProgressThin : Control
{
    private int _value;
    public int Maximum { get; set; } = 100;
    public int Value { get => _value; set { _value = Math.Max(0, Math.Min(Maximum, value)); Invalidate(); } }
    public bool Indeterminate { get; set; }

    public ProgressThin()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 6;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var track = Theme.RoundRect(r, Height / 2)) using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, track);
        if (Maximum <= 0) return;
        int w = (int)((Width - 1) * (_value / (double)Maximum));
        if (w < Height) w = _value > 0 ? Height : 0;
        if (w <= 0) return;
        using var fill = Theme.RoundRect(new Rectangle(0, 0, w, Height - 1), Height / 2);
        using var fb = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(w, 1), Height), Theme.Accent, Theme.AccentHover, 0f);
        g.FillPath(fb, fill);
    }
}

/// <summary>Fully self-painted dropdown (pill + chevron + themed popup list). Sidesteps the
/// native ComboBox whose drop-button stays light in dark mode. Reads the palette at paint time.</summary>
public sealed class PillCombo : Control
{
    private readonly List<string> _items = new();
    private int _index = -1;
    private bool _hover;
    public event EventHandler? SelectedIndexChanged;

    public PillCombo()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 34; Cursor = Cursors.Hand; Font = Theme.FontBase;
    }

    public void SetItems(IEnumerable<string> items, string? select = null)
    {
        _items.Clear(); _items.AddRange(items);
        _index = select != null ? _items.FindIndex(x => x == select) : (_items.Count > 0 ? 0 : -1);
        if (_index < 0 && _items.Count > 0) _index = 0;
        Invalidate();
    }
    public string? SelectedItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    public int SelectedIndex
    {
        get => _index;
        set { if (value != _index && value >= 0 && value < _items.Count) { _index = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); } }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    private Form? _popup;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Capture = false;
        if (_items.Count == 0 || _popup != null) return;
        // borderless popup Form + virtualized ListBox: instant with 300+ items, and no
        // ToolStripDropDown double-dispose crash. Closes when it loses focus.
        var list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
            BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Font
        };
        list.Items.AddRange(_items.ToArray());
        list.SelectedIndex = _index >= 0 ? _index : 0;
        Theme.DarkScrollbars(list);

        int w = Math.Max(Width, 200);
        int h = Math.Min(_items.Count, 12) * list.ItemHeight + 6;
        var pop = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            BackColor = Theme.Border, Padding = new Padding(1),
            Size = new Size(w, h), Location = PointToScreen(new Point(0, Height + 2))
        };
        pop.Controls.Add(list);
        _popup = pop;

        void Commit(int idx) { if (idx >= 0) SelectedIndex = idx; pop.Close(); }
        list.MouseClick += (_, me) => Commit(list.IndexFromPoint(me.Location));
        list.KeyDown += (_, ev) => { if (ev.KeyCode is Keys.Enter) Commit(list.SelectedIndex); else if (ev.KeyCode == Keys.Escape) pop.Close(); };
        pop.Deactivate += (_, _) => pop.Close();
        pop.FormClosed += (_, _) => { _popup = null; pop.Dispose(); };

        pop.Show(FindForm());
        pop.Activate();
        list.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, 9);
        using (var b = new SolidBrush(_hover ? Theme.CardHover : Theme.Panel)) g.FillPath(b, path);
        using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
        var textRect = new Rectangle(11, 0, Width - 34, Height);
        TextRenderer.DrawText(g, SelectedItem ?? "", Font, textRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        // chevron
        int cx = Width - 18, cy = Height / 2 - 1;
        using var cp = new Pen(Theme.SubText, 1.6f);
        g.DrawLines(cp, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
    }
}

/// <summary>iOS-style on/off toggle that reads the palette at paint time.</summary>
public sealed class ToggleSwitch : Control
{
    private bool _on;
    public event EventHandler? Toggled;
    public bool On { get => _on; set { if (_on != value) { _on = value; Invalidate(); Toggled?.Invoke(this, EventArgs.Empty); } } }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 26); Cursor = Cursors.Hand;
    }
    protected override void OnClick(EventArgs e) { On = !On; base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var r = new Rectangle(0, 2, Width - 1, Height - 5);
        // off-track: midway between border and subtext so it reads on any background
        Color off = Color.FromArgb((Theme.Border.R + Theme.SubText.R) / 2,
                                   (Theme.Border.G + Theme.SubText.G) / 2,
                                   (Theme.Border.B + Theme.SubText.B) / 2);
        using (var track = Theme.RoundRect(r, (Height - 5) / 2))
        using (var b = new SolidBrush(_on ? Theme.Accent : off)) g.FillPath(b, track);
        int d = Height - 11;
        int x = _on ? Width - d - 4 : 4;
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, x, 5, d, d);
    }
}

/// <summary>Borderless rounded text box hosted on a painted pill.</summary>
public sealed class PillTextBox : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    public string PlaceholderText { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

    public PillTextBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Box.BackColor = Theme.Panel;
        Box.ForeColor = Theme.Text;
        Box.Font = Theme.FontBase;
        Box.BorderStyle = BorderStyle.None;
        Controls.Add(Box);
        Height = 38;
        Padding = new Padding(14, 0, 14, 0);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Box.Location = new Point(14, (Height - Box.Height) / 2);
        Box.Width = Width - 28;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, 10);
        using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
        using var pen = new Pen(Box.Focused ? Theme.Accent : Theme.Border);
        g.DrawPath(pen, path);
    }
}
