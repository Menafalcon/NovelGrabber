using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NovelGrabber;

/// <summary>Loads and caches novel cover bitmaps, pre-scaled to card size (object-fit: cover).
/// Decodes through WPF/WIC first so webp covers work, then falls back to GDI+.</summary>
public static class CoverCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();   // Get() runs on worker threads too

    private static string KeyOf(NovelMeta m, int w, int h) => Path.Combine(m.Folder, m.Cover) + "|" + w + "x" + h;

    /// <summary>Cache-only lookup — never touches disk (safe for the UI thread).</summary>
    public static Bitmap? Peek(NovelMeta m, int w, int h)
    {
        if (string.IsNullOrWhiteSpace(m.Cover)) return null;
        lock (Gate) return Cache.TryGetValue(KeyOf(m, w, h), out var hit) ? hit : null;
    }

    public static Bitmap? Get(NovelMeta m, int w, int h)
    {
        if (string.IsNullOrWhiteSpace(m.Cover)) return null;
        string path = Path.Combine(m.Folder, m.Cover);
        string key = KeyOf(m, w, h);
        lock (Gate) if (Cache.TryGetValue(key, out var hit)) return hit;
        Bitmap? bmp = null;
        try { if (File.Exists(path)) bmp = LoadScaled(path, w, h); } catch { }
        lock (Gate) Cache[key] = bmp;
        return bmp;
    }

    // With 60+ books, firing one Task.Run per card stampedes the thread pool and disk and the
    // whole shelf stutters. One worker drains a queue instead: cards enqueue instantly and
    // covers stream in one at a time while the UI stays responsive.
    private static readonly ConcurrentQueue<(NovelMeta m, int w, int h, Action<Bitmap?> done)> Jobs = new();
    private static int _pumping;

    public static void GetAsync(NovelMeta m, int w, int h, Action<Bitmap?> done)
    {
        Jobs.Enqueue((m, w, h, done));
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) == 0)
            Task.Run(Pump);
    }

    private static void Pump()
    {
        while (true)
        {
            while (Jobs.TryDequeue(out var j))
            {
                Bitmap? bmp = null;
                try { bmp = Get(j.m, j.w, j.h); } catch { }
                try { j.done(bmp); } catch { }
            }
            Interlocked.Exchange(ref _pumping, 0);
            // a job may land between the empty dequeue and the flag reset — reclaim or exit
            if (Jobs.IsEmpty || Interlocked.CompareExchange(ref _pumping, 1, 0) != 0) return;
        }
    }

    private static Bitmap? LoadScaled(string path, int w, int h)
    {
        Bitmap? src = null;
        try { src = WicLoad(path); } catch { }
        if (src == null) { try { using var fs = File.OpenRead(path); src = new Bitmap(fs); } catch { return null; } }
        using (src)
        {
            double scale = Math.Max((double)w / src.Width, (double)h / src.Height);
            int sw = (int)Math.Ceiling(src.Width * scale), sh = (int)Math.Ceiling(src.Height * scale);
            var dst = new Bitmap(w, h);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle((w - sw) / 2, (h - sh) / 2, sw, sh));
            return dst;
        }
    }

    /// <summary>WIC decode via WPF — handles webp (and anything else with a Windows codec).</summary>
    private static Bitmap? WicLoad(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(dec.Frames[0],
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        conv.CopyPixels(pixels, stride, 0);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        return bmp;
    }
}

/// <summary>Bookshelf-style card: cover art on top, title + info below it, actions at the bottom.</summary>
public sealed class NovelCard : Control
{
    public const int CardW = 196, MaxW = 256, CoverH = 252, CardH = CoverH + 106;
    private static Color Fill => Theme.CardFill;

    public NovelMeta Meta { get; }
    private bool _sel, _hover;
    public bool Selected { get => _sel; set { if (_sel != value) { _sel = value; Invalidate(); } } }

    private Bitmap? _cover;
    private readonly string _sub;
    private readonly FlatButton _readBtn = new() { Text = "▶  Read", Style = FlatButton.Kind.Accent, Radius = 7 };
    private readonly FlatButton _moreBtn = new() { Text = "⋯", Style = FlatButton.Kind.Ghost, Radius = 7, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };

    public event Action<NovelCard>? Chosen;
    public event Action<NovelCard>? ReadRequested;
    public event Action<NovelCard, Point>? MoreRequested;   // screen point to anchor the menu

    public NovelCard(NovelMeta meta, string site, string date)
    {
        Meta = meta;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(CardW, CardH);
        BackColor = Fill;                                     // FlatButton clears with Parent.BackColor
        Cursor = Cursors.Hand;

        // covers decode off the UI thread on the shared cover worker; size matches the actual
        // DPI instead of a blanket 2x. Decoded at the card's MAX width (cards stretch to fill
        // the row) — OnPaint source-crops to the live aspect so nothing distorts.
        float dpi = Math.Clamp(DeviceDpi / 96f, 1f, 2f);
        int cw = (int)(MaxW * dpi), ch = (int)(CoverH * dpi);
        _cover = CoverCache.Peek(meta, cw, ch);
        if (_cover == null && !string.IsNullOrWhiteSpace(meta.Cover))
        {
            var ui = SynchronizationContext.Current;
            CoverCache.GetAsync(meta, cw, ch, bmp =>
            {
                if (bmp == null || ui == null) return;
                ui.Post(_ => { if (!IsDisposed) { _cover = bmp; Invalidate(); } }, null);
            });
        }

        var parts = new List<string> { $"{meta.Chapters.Count} chapters" };
        if (site.Length > 0) parts.Add(site);
        if (date.Length > 0) parts.Add(date);
        _sub = string.Join(" · ", parts);

        _readBtn.SetBounds(10, CardH - 40, CardW - 20 - 34 - 6, 30);
        _moreBtn.SetBounds(CardW - 10 - 34, CardH - 40, 34, 30);
        Controls.Add(_readBtn); Controls.Add(_moreBtn);

        MouseDown += (_, _) => Chosen?.Invoke(this);
        DoubleClick += (_, _) => ReadRequested?.Invoke(this);
        _readBtn.Click += (_, _) => { Chosen?.Invoke(this); ReadRequested?.Invoke(this); };
        _moreBtn.Click += (_, _) => { Chosen?.Invoke(this); MoreRequested?.Invoke(this, PointToScreen(new Point(CardW - 10 - 34, CardH - 8))); };
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) { _hover = false; Invalidate(); } };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // cards stretch horizontally to fill the row — keep the bottom buttons anchored
        _readBtn.SetBounds(10, CardH - 40, Width - 20 - 34 - 6, 30);
        _moreBtn.SetBounds(Width - 10 - 34, CardH - 40, 34, 30);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(r, 12))
        {
            using (var b = new SolidBrush(Fill)) g.FillPath(b, path);

            // cover (clipped to the card's rounded top)
            var coverRect = new Rectangle(1, 1, Width - 2, CoverH);
            var state = g.Save();
            using (var clip = Theme.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), 11))
                g.SetClip(clip);
            g.IntersectClip(coverRect);
            if (_cover != null)
            {
                // object-fit: cover — source-crop the pre-decoded bitmap to the live aspect
                float destA = (float)coverRect.Width / coverRect.Height;
                float srcA = (float)_cover.Width / _cover.Height;
                Rectangle src = srcA > destA
                    ? new Rectangle((int)((_cover.Width - _cover.Height * destA) / 2), 0, (int)(_cover.Height * destA), _cover.Height)
                    : new Rectangle(0, (int)((_cover.Height - _cover.Width / destA) / 2), _cover.Width, (int)(_cover.Width / destA));
                g.DrawImage(_cover, coverRect, src, GraphicsUnit.Pixel);
            }
            else PaintPlaceholder(g, coverRect);
            g.Restore(state);

            using var pen = new Pen(_sel ? Theme.Accent : (_hover ? Theme.AccentHover : Theme.Border), _sel ? 2f : 1f);
            g.DrawPath(pen, path);
        }

        // title (max 2 lines) + sub line
        var titleRect = new Rectangle(10, CoverH + 8, Width - 20, 36);
        TextRenderer.DrawText(g, Meta.Title, Theme.FontSemi, titleRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var subRect = new Rectangle(10, CoverH + 46, Width - 20, 16);
        TextRenderer.DrawText(g, _sub, Theme.FontSub, subRect, Theme.SubText,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>No cover art → the app's gradient + a big initial, so the shelf still looks intentional.</summary>
    private void PaintPlaceholder(Graphics g, Rectangle r)
    {
        using (var lg = new LinearGradientBrush(r, Color.FromArgb(0x4A, 0x6C, 0xF7), Color.FromArgb(0x8B, 0x5C, 0xF6), 55f))
            g.FillRectangle(lg, r);
        string initial = Meta.Title.Length > 0 ? Meta.Title.Substring(0, 1).ToUpperInvariant() : "?";
        using var big = new Font("Segoe UI Semibold", 64f);
        TextRenderer.DrawText(g, initial, big, r, Color.FromArgb(230, 255, 255, 255),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Small dark text-input modal (WinForms has no InputBox) — used for "New category…".</summary>
public sealed class PromptDialog : Form
{
    private readonly PillTextBox _input = new() { Dock = DockStyle.Top, Height = 38 };

    private PromptDialog(string title, string label)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg; ForeColor = Theme.Text;
        ClientSize = new Size(380, 132);
        Padding = new Padding(18, 14, 18, 12);

        var ok = new FlatButton { Text = "OK", Width = 84, Height = 32, Style = FlatButton.Kind.Accent, DialogResult = DialogResult.OK };
        var cancel = new FlatButton { Text = "Cancel", Width = 84, Height = 32, Style = FlatButton.Kind.Ghost, DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Bg };
        ok.Margin = new Padding(8, 0, 0, 0);
        btns.Controls.Add(ok); btns.Controls.Add(cancel);

        Controls.Add(btns);
        Controls.Add(_input);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Text = label, ForeColor = Theme.SubText, Font = Theme.FontSub });
        Shown += (_, _) => { Theme.ApplyWindowChrome(this); _input.Box.Focus(); };
    }

    public static string? Ask(IWin32Window owner, string title, string label)
    {
        using var dlg = new PromptDialog(title, label);
        return dlg.ShowDialog(owner) == DialogResult.OK && dlg._input.Text.Trim().Length > 0
            ? dlg._input.Text.Trim() : null;
    }
}

/// <summary>FlowLayoutPanel isn't double-buffered, so scrolling 60+ custom-painted cards
/// flickers and repaints in storms. This one buffers.</summary>
public sealed class SmoothFlowPanel : FlowLayoutPanel
{
    public SmoothFlowPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}

/// <summary>Left-nav item: label + count, accent bar when selected. Section headers use Header=true.</summary>
public sealed class NavButton : Control
{
    public bool Selected { get; set; }
    public int Count { get; set; } = -1;
    public bool Header { get; set; }
    private bool _hover;

    public NavButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 34; Dock = DockStyle.Top; Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bg = Parent?.BackColor ?? Theme.Bg;
        g.Clear(bg);
        if (Header)
        {
            using var hf = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, Text, hf, new Rectangle(10, 0, Width - 12, Height), Theme.SubText,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
            return;
        }
        if (Selected || _hover)
        {
            // accent-tinted for selected, subtle hover otherwise — derived from the palette
            using var b = new SolidBrush(Selected ? Blend(bg, Theme.Accent, 0.16) : Blend(bg, Theme.SubText, 0.10));
            using var p = Theme.RoundRect(new Rectangle(4, 2, Width - 9, Height - 5), 8);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(b, p);
        }
        if (Selected) using (var ab = new SolidBrush(Theme.Accent)) g.FillRectangle(ab, new Rectangle(4, 8, 3, Height - 17));
        TextRenderer.DrawText(g, Text, Selected ? Theme.FontSemi : Theme.FontBase,
            new Rectangle(16, 0, Width - 60, Height), Selected ? Theme.Accent : Theme.SubText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Count >= 0)
            TextRenderer.DrawText(g, Count.ToString(), Theme.FontSub, new Rectangle(Width - 52, 0, 44, Height),
                Theme.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>List-view row: cover thumb, title/author, date, progress bar + ⋯ — the compact
/// alternative to the card grid.</summary>
public sealed class NovelListRow : Control
{
    public NovelMeta Meta { get; }
    private bool _sel, _hover;
    public bool Selected { get => _sel; set { if (_sel != value) { _sel = value; Invalidate(); } } }

    private Bitmap? _thumb;
    private readonly string _sub, _date;
    private readonly int _pct;
    private readonly FlatButton _moreBtn = new() { Text = "⋯", Style = FlatButton.Kind.Ghost, Radius = 7, Width = 34, Height = 28, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Anchor = AnchorStyles.Right };

    public event Action<NovelListRow>? Chosen;
    public event Action<NovelListRow>? ReadRequested;
    public event Action<NovelListRow, Point>? MoreRequested;

    public NovelListRow(NovelMeta meta, string site, string date, int pct)
    {
        Meta = meta; _date = date; _pct = pct;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 58; Dock = DockStyle.Top; BackColor = Theme.Card; Cursor = Cursors.Hand;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(meta.Author)) parts.Add(meta.Author);
        parts.Add($"{meta.Chapters.Count} chapters");
        if (site.Length > 0) parts.Add(site);
        _sub = string.Join(" · ", parts);

        _thumb = CoverCache.Peek(meta, 68, 96);
        if (_thumb == null && !string.IsNullOrWhiteSpace(meta.Cover))
        {
            var ui = SynchronizationContext.Current;
            CoverCache.GetAsync(meta, 68, 96, bmp =>
            { if (bmp != null && ui != null) ui.Post(_ => { if (!IsDisposed) { _thumb = bmp; Invalidate(); } }, null); });
        }

        Controls.Add(_moreBtn);
        MouseDown += (_, _) => Chosen?.Invoke(this);
        DoubleClick += (_, _) => ReadRequested?.Invoke(this);
        _moreBtn.Click += (_, _) => { Chosen?.Invoke(this); MoreRequested?.Invoke(this, PointToScreen(new Point(_moreBtn.Left, _moreBtn.Bottom + 4))); };
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) { _hover = false; Invalidate(); } };
        Resize += (_, _) => _moreBtn.Location = new Point(Width - 44, (Height - 28) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_sel ? Theme.CardHover : (_hover ? Color.FromArgb(0x20, 0x25, 0x2F) : Theme.Card));
        if (_sel) using (var ab = new SolidBrush(Theme.Accent)) g.FillRectangle(ab, new Rectangle(0, 6, 3, Height - 12));

        // cover thumb
        var tr = new Rectangle(14, 7, 32, 44);
        if (_thumb != null) g.DrawImage(_thumb, tr);
        else { using var b = new SolidBrush(Color.FromArgb(0x4A, 0x6C, 0xF7)); g.FillRectangle(b, tr); }

        // progress bar + pct + date occupy the right side (before the ⋯ button)
        int barW = 110, barX = Width - 44 - 14 - barW - 46, midY = Height / 2;
        var barRect = new Rectangle(barX, midY - 4, barW, 8);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = Theme.RoundRect(barRect, 4)) using (var tb = new SolidBrush(Theme.Border)) g.FillPath(tb, track);
        int fw = (int)(barW * _pct / 100.0);
        if (fw > 0)
        {
            using var fp = Theme.RoundRect(new Rectangle(barX, midY - 4, Math.Max(fw, 8), 8), 4);
            using var fb = new SolidBrush(Theme.Accent);
            g.FillPath(fb, fp);
        }
        TextRenderer.DrawText(g, _pct + "%", Theme.FontSub, new Rectangle(barX + barW + 4, 0, 42, Height),
            Theme.SubText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _date, Theme.FontSub, new Rectangle(barX - 64, 0, 56, Height),
            Theme.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // title + sub, clipped before the date column
        int textW = Math.Max(40, barX - 64 - 8 - 56);
        TextRenderer.DrawText(g, Meta.Title, Theme.FontSemi, new Rectangle(56, 9, textW, 20), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _sub, Theme.FontSub, new Rectangle(56, 31, textW, 16), Theme.SubText,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Format-view row for a loose .epub/.pdf file in the library.</summary>
public sealed class FileRow : Control
{
    public string Path { get; }
    private bool _hover;
    private readonly string _name, _sub;
    private readonly FlatButton _moreBtn = new() { Text = "⋯", Style = FlatButton.Kind.Ghost, Radius = 7, Width = 34, Height = 28, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };

    public event Action<FileRow>? OpenRequested;
    public event Action<FileRow, Point>? MoreRequested;

    public FileRow(string path, string sub)
    {
        Path = path; _name = System.IO.Path.GetFileName(path); _sub = sub;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 48; Dock = DockStyle.Top; BackColor = Theme.Card; Cursor = Cursors.Hand;
        Controls.Add(_moreBtn);
        DoubleClick += (_, _) => OpenRequested?.Invoke(this);
        _moreBtn.Click += (_, _) => MoreRequested?.Invoke(this, PointToScreen(new Point(_moreBtn.Left, _moreBtn.Bottom + 4)));
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) { _hover = false; Invalidate(); } };
        Resize += (_, _) => _moreBtn.Location = new Point(Width - 44, (Height - 28) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_hover ? Color.FromArgb(0x20, 0x25, 0x2F) : Theme.Card);
        string icon = _name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "📄" : "📘";
        TextRenderer.DrawText(g, icon, new Font("Segoe UI Emoji", 13f), new Rectangle(12, 0, 34, Height),
            Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int textW = Width - 56 - 54;
        TextRenderer.DrawText(g, _name, Theme.FontSemi, new Rectangle(48, 6, textW, 20), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _sub, Theme.FontSub, new Rectangle(48, 26, textW, 16), Theme.SubText,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Dark-theme renderer for the card's ⋯ context menu (WinForms menus are light by default).</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color MenuItemSelected => Theme.CardHover;
        public override Color MenuItemSelectedGradientBegin => Theme.CardHover;
        public override Color MenuItemSelectedGradientEnd => Theme.CardHover;
        public override Color MenuItemBorder => Theme.CardHover;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
