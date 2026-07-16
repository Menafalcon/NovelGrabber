using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovelGrabber;

public sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly PillTextBox _url = new() { Dock = DockStyle.Top };
    private readonly NumericUpDown _count = new() { Minimum = 0, Maximum = 1000000, Value = 50, Width = 76 };
    private readonly FlatButton _start = new() { Text = "Download", Width = 120, Style = FlatButton.Kind.Accent };
    private readonly FlatButton _stop = new() { Text = "Stop", Width = 80, Style = FlatButton.Kind.Ghost, Enabled = false };
    private readonly FlatButton _libBtn = new() { Text = "Change…", Width = 90, Style = FlatButton.Kind.Ghost };
    private readonly Label _libLbl = new() { AutoSize = true, ForeColor = Theme.SubText, Font = Theme.FontSub };

    private readonly Label _friendly = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Text, Font = new Font("Segoe UI Semibold", 13f), Text = "Ready" };
    private readonly ProgressThin _bar = new() { Dock = DockStyle.Top };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 18, ForeColor = Theme.SubText, Font = Theme.FontSub, Text = "" };
    private readonly FlatButton _detailsBtn = new() { Text = "Show details", Width = 110, Height = 26, Style = FlatButton.Kind.Ghost };
    private Card _detailsHost = null!;
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Color.Gainsboro, Font = Theme.FontMono };

    private readonly FlatButton _tabDl = new() { Text = "Download", Width = 110, Height = 34, Style = FlatButton.Kind.Accent };
    private readonly FlatButton _tabLib = new() { Text = "Library", Width = 100, Height = 34, Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _tabRead = new() { Text = "Reader", Width = 100, Height = 34, Style = FlatButton.Kind.Ghost };
    private Panel _pageDl = null!, _pageLib = null!, _pageRead = null!;
    private ReaderHost? _readerHost;
    private CoreWebView2Environment? _env;
    private readonly Label _readHint = new() { Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, ForeColor = Theme.SubText, Font = Theme.FontBase, Text = "Pick a novel in the Library and press “Read”,\nor open an EPUB file below." };

    // bookshelf grid (replaces the old file-explorer ListView)
    private readonly FlowLayoutPanel _grid = new SmoothFlowPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card, Padding = new Padding(2, 6, 2, 2) };
    private readonly Label _emptyLbl = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.SubText, Font = Theme.FontBase, BackColor = Theme.Card, Text = "Nothing here yet.\nDownload a novel from the Download tab.", Visible = false };
    private string? _selFolder;
    private string _selCategory = "General";                 // active shelf…
    private string? _selFormat;                              // …or "epub"/"pdf" when a Formats view is active
    private string _viewMode = AppSettings.Load().LibraryView;

    // left nav (20%): shelves + formats on top, actions pinned at the bottom
    private readonly Panel _nav = new() { Dock = DockStyle.Left, Width = 230, BackColor = Theme.Card, Padding = new Padding(0, 2, 12, 2) };
    private readonly Panel _navItems = new() { Dock = DockStyle.Fill, BackColor = Theme.Card, AutoScroll = true };
    private readonly Panel _navBottom = new() { Dock = DockStyle.Bottom, BackColor = Theme.Card, Height = 164 };
    private readonly PillTextBox _search = new() { Dock = DockStyle.Fill, Height = 36 };
    private readonly FlatButton _addBook = new() { Text = "＋ Add book", Width = 110, Height = 34, Style = FlatButton.Kind.Accent };
    private readonly FlatButton _addFolder = new() { Text = "Add folder", Width = 96, Height = 34, Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _viewGridBtn = new() { Text = "▦", Width = 38, Height = 34, Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _viewListBtn = new() { Text = "☰", Width = 38, Height = 34, Style = FlatButton.Kind.Ghost };
    private readonly Panel _listHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Card, AutoScroll = true, Visible = false };
    private readonly Panel _fileHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Card, AutoScroll = true, Visible = false };
    private Panel? _contentHost;                              // grid/list/file hosts' parent (shelf slide)
    private TtsSpeakPanel? _speakPanel;
    private readonly FlatButton _speakBtn = new() { Text = "Read text", Width = 132, Height = 32, Style = FlatButton.Kind.Ghost, Dock = DockStyle.Right };
    private readonly FlatButton _autoSort = new() { Text = "⚡ Auto sort", Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _newCat = new() { Text = "＋ New category", Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _settingsBtn = new() { Text = "⚙ Settings", Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _delAll = new() { Text = "Delete all", Style = FlatButton.Kind.Danger };
    private readonly Panel _settingsHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Card, AutoScroll = true, Visible = false };
    private bool _showingSettings;
    private readonly ToggleSwitch _autoSortToggle = new();
    private readonly Label _setLibLbl = new() { AutoSize = true, ForeColor = Theme.SubText, Font = Theme.FontBase };
    private readonly PillTextBox _gkeyBox = new() { Width = 320, Height = 36 };
    private readonly List<FlatButton> _themeBtns = new();

    // download page: built-in site browser
    private readonly FlatButton _sitesBtn = new() { Text = "Sites ▾", Width = 76, Height = 26, Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _nuBtn = new() { Text = "NovelUpdates", Width = 104, Height = 26, Style = FlatButton.Kind.Ghost };
    private readonly FlatButton _scrapePageBtn = new() { Text = "⚡ Scrape this page", Width = 148, Height = 26, Style = FlatButton.Kind.Accent };
    private static readonly (string Name, string Url)[] Sites =
    {
        ("NovelLunar", "https://novellunar.com"),
        ("Novel Fire", "https://novelfire.net"),
        ("Ranobes", "https://ranobes.top"),
        ("NovelHall", "https://novelhall.com"),
        ("KAT Reading Cafe", "https://katreadingcafe.com"),
        ("Novelpia (Global)", "https://global.novelpia.com"),
        ("Novel Arrow", "https://novelarrow.com"),
        ("Light Novel World", "https://www.lightnovelworld.com"),
        ("NovelFull", "https://novelfull.net"),
    };

    private static readonly HttpClient Http = CreateHttp();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public MainForm()
    {
        Text = "NovelGrabber";
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.FontBase;
        Width = 1240; Height = 860; MinimumSize = new Size(980, 660);
        StartPosition = FormStartPosition.CenterScreen;
        try { using var ico = typeof(MainForm).Assembly.GetManifestResourceStream("NovelGrabber.app.ico"); if (ico != null) Icon = new Icon(ico); } catch { }
        _libLbl.Text = Library.Root;

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _pageDl = BuildDownloadPage();
        _pageLib = BuildLibraryPage();
        _pageRead = BuildReaderPage();
        content.Controls.Add(_pageDl);
        content.Controls.Add(_pageLib);
        content.Controls.Add(_pageRead);
        Controls.Add(content);
        Controls.Add(BuildHeader());

        _tabDl.Click += (_, _) => ShowTab(0);
        _tabLib.Click += (_, _) => ShowTab(1);
        _tabRead.Click += async (_, _) => { ShowTab(2); await EnsureReaderAsync(); };
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _libBtn.Click += (_, _) => ChooseLibrary();
        _detailsBtn.Click += (_, _) => ToggleDetails();
        _addBook.Click += async (_, _) => await AddBooksAsync();
        _addFolder.Click += async (_, _) => await ImportEpubsAsync();
        _autoSort.Click += (_, _) => RunAutoSort();
        _newCat.Click += (_, _) => { var n = PromptDialog.Ask(this, "New category", "Name for the new category:"); if (n != null) { Library.AddCategory(n); _selCategory = n; _selFormat = null; RefreshHistory(); } };
        _delAll.Click += (_, _) => DeleteAll();
        _search.Box.TextChanged += (_, _) => ApplyFilter();
        _grid.Resize += (_, _) => FitCards();
        _viewGridBtn.Click += (_, _) => SetViewMode("grid");
        _viewListBtn.Click += (_, _) => SetViewMode("list");
        _sitesBtn.Click += (_, _) => ShowSitesMenu();
        _nuBtn.Click += (_, _) => OpenSiteInPreview("https://www.novelupdates.com");
        _scrapePageBtn.Click += async (_, _) => await ScrapeCurrentPageAsync();
        _settingsBtn.Click += (_, _) => ShowSettings(!_showingSettings);

        // whole-app theme (dark/darksepia/sepia/light, same as the reader)
        if (AppSettings.Load().Theme is { } th && th != "dark") SetAppTheme(th);

        Shown += (_, _) => Theme.ApplyWindowChrome(this);
        Load += async (_, _) => await InitAsync();
        ShowTab(Environment.GetCommandLineArgs().Contains("--lib") ? 1 : 0);
    }

    // ---------------- whole-app theme ----------------

    private void SetAppTheme(string name)
    {
        Theme.Switch(name, this);
        var s = AppSettings.Load(); s.Theme = name; s.Save();
        if (IsHandleCreated) Theme.ApplyWindowChrome(this);   // titlebar follows (dark ↔ light)
        try { _web.DefaultBackgroundColor = Theme.Panel; } catch { }
        _readerHost?.SetTheme(name);                          // reader page mirrors the app
        _gridSig = "\0"; RefreshHistory();                    // rebuild cards with the new palette
    }

    // ---------------- layout ----------------

    private Control BuildHeader()
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Bg, Padding = new Padding(20, 0, 20, 0) };
        var dot = new Label { Text = "●", Font = Theme.FontTitle, ForeColor = Theme.Accent, AutoSize = true, Location = new Point(20, 14) };
        var title = new Label { Text = "NovelGrabber", Font = Theme.FontTitle, ForeColor = Theme.Text, AutoSize = true, Location = new Point(40, 13) };
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(0, 11, 0, 0) };
        _tabDl.Margin = new Padding(0, 0, 8, 0);
        _tabLib.Margin = new Padding(0, 0, 8, 0);
        _tabRead.Margin = new Padding(0, 0, 0, 0);
        tabs.Controls.Add(_tabDl); tabs.Controls.Add(_tabLib); tabs.Controls.Add(_tabRead);
        p.Controls.Add(tabs); p.Controls.Add(title); p.Controls.Add(dot);
        return p;
    }

    private Panel BuildDownloadPage()
    {
        // flat page — controls sit directly on the background, no cards
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(24, 12, 24, 16) };

        // URL row
        _url.PlaceholderText = "Paste a Chapter-1 reading URL (best) or a contents page URL …";
        _url.Height = 42; _url.Dock = DockStyle.Top;

        // controls row: chapters + download/stop (left), save-to (right)
        var row = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Theme.Bg };
        StyleNumeric(_count);
        left.Controls.Add(Lbl("Chapters", 9));
        left.Controls.Add(NumericPill(_count));
        left.Controls.Add(Lbl("(0 = all)", 11, Theme.SubText, Theme.FontSub));
        _start.Margin = new Padding(10, 2, 8, 0); _stop.Margin = new Padding(0, 2, 0, 0);
        left.Controls.Add(_start); left.Controls.Add(_stop);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Bg };
        right.Controls.Add(Lbl("Save to:", 9, Theme.SubText));
        _libLbl.Padding = new Padding(0, 11, 8, 0); right.Controls.Add(_libLbl);
        _libBtn.Margin = new Padding(0, 2, 0, 0); right.Controls.Add(_libBtn);
        row.Controls.Add(left); row.Controls.Add(right);

        // status row: STATUS label + browser buttons
        var statTop = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0) };
        statTop.Controls.Add(new Label { Dock = DockStyle.Left, AutoSize = true, Text = "STATUS", Font = Theme.FontSemi, ForeColor = Theme.SubText, Padding = new Padding(0, 6, 0, 0) });
        _scrapePageBtn.Dock = DockStyle.Right; statTop.Controls.Add(_scrapePageBtn);
        statTop.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.Bg });
        _nuBtn.Dock = DockStyle.Right; statTop.Controls.Add(_nuBtn);
        statTop.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.Bg });
        _sitesBtn.Dock = DockStyle.Right; statTop.Controls.Add(_sitesBtn);
        statTop.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.Bg });
        _detailsBtn.Dock = DockStyle.Right; statTop.Controls.Add(_detailsBtn);

        _friendly.BackColor = Theme.Bg;
        _bar.BackColor = Theme.Bg;
        _status.BackColor = Theme.Bg;

        // details log (collapsible) — flat, bordered panel
        _detailsHost = new Card { Dock = DockStyle.Fill, Padding = new Padding(10), Radius = 10 };
        _detailsHost.BackColor = Theme.Panel;
        _detailsHost.Controls.Add(_log);
        var detWrap = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0), Visible = false };
        detWrap.Controls.Add(_detailsHost);

        // preview browser (fills the rest) with a thin border
        var prevWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0) };
        var prevBorder = new Card { Dock = DockStyle.Fill, Padding = new Padding(1), Radius = 10 };
        prevBorder.BackColor = Theme.Panel;
        var holder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(1) };
        holder.Controls.Add(_web);
        prevBorder.Controls.Add(holder);
        prevWrap.Controls.Add(prevBorder);
        prevWrap.Controls.Add(new Label { Dock = DockStyle.Top, Height = 22, Text = "PREVIEW  ·  solve any captcha / log in here", Font = Theme.FontSub, ForeColor = Theme.SubText });

        // add bottom-up so Dock=Top stacks in the right visual order
        page.Controls.Add(prevWrap);   // fill
        page.Controls.Add(detWrap);
        page.Controls.Add(_status);
        page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
        page.Controls.Add(_bar);
        page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
        page.Controls.Add(_friendly);
        page.Controls.Add(statTop);
        page.Controls.Add(row);
        page.Controls.Add(_url);
        _detailsWrap = detWrap;
        return page;
    }
    private Panel _detailsWrap = null!;

    private Panel BuildLibraryPage()
    {
        // flat page: nav flush to the left edge (20%), content fills the rest — no outer card
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };

        // left nav: shelves fill the top, three actions pinned at the bottom (add bottom-up)
        _nav.Padding = new Padding(14, 10, 14, 10);
        foreach (var b in new[] { _autoSort, _newCat, _settingsBtn }) { b.Dock = DockStyle.Top; b.Height = 34; }
        _navBottom.Height = 128; _navBottom.BackColor = Theme.Bg;
        _navBottom.Controls.Add(_settingsBtn);
        _navBottom.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
        _navBottom.Controls.Add(_newCat);
        _navBottom.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
        _navBottom.Controls.Add(_autoSort);
        _nav.BackColor = Theme.Bg; _navItems.BackColor = Theme.Bg;
        _nav.Controls.Add(_navItems);
        _nav.Controls.Add(_navBottom);

        // main content area
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(16, 10, 20, 12) };
        var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 4) };
        var topRight = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Bg };
        _addBook.Margin = new Padding(10, 1, 0, 0);
        _addFolder.Margin = new Padding(8, 1, 0, 0);
        _viewGridBtn.Margin = new Padding(12, 1, 0, 0);
        _viewListBtn.Margin = new Padding(4, 1, 0, 0);
        topRight.Controls.Add(_addBook); topRight.Controls.Add(_addFolder);
        topRight.Controls.Add(_viewGridBtn); topRight.Controls.Add(_viewListBtn);
        _search.PlaceholderText = "Search your library…";
        top.Controls.Add(_search);
        top.Controls.Add(topRight);

        _grid.BackColor = Theme.Bg; _listHost.BackColor = Theme.Bg;
        _fileHost.BackColor = Theme.Bg; _emptyLbl.BackColor = Theme.Bg; _settingsHost.BackColor = Theme.Bg;
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        content.Controls.Add(_grid);
        content.Controls.Add(_listHost);
        content.Controls.Add(_fileHost);
        content.Controls.Add(_settingsHost);
        content.Controls.Add(_emptyLbl);
        _contentHost = content;
        BuildSettingsPanel();
        Theme.DarkScrollbars(_grid); Theme.DarkScrollbars(_listHost);
        Theme.DarkScrollbars(_fileHost); Theme.DarkScrollbars(_navItems);
        Theme.DarkScrollbars(_settingsHost); Theme.DarkScrollbars(_log);

        main.Controls.Add(content);
        main.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
        main.Controls.Add(top);
        page.Controls.Add(main);   // fill
        page.Controls.Add(_nav);   // left
        page.Resize += (_, _) => _nav.Width = Math.Clamp((int)(page.ClientSize.Width * 0.2), 200, 320);
        return page;
    }

    // ---- left nav ----

    private void RebuildNav(List<NovelMeta> all, List<string> cats, int epubCount, int pdfCount)
    {
        _navItems.SuspendLayout();
        foreach (Control c in _navItems.Controls.Cast<Control>().ToArray()) c.Dispose();
        _navItems.Controls.Clear();
        int CountOf(string cat) => cat == "All" ? all.Count
            : cat == "General" ? all.Count(m => string.IsNullOrEmpty(m.Category) || m.Category == "General")
            : all.Count(m => string.Equals(m.Category, cat, StringComparison.OrdinalIgnoreCase));

        var rows = new List<NavButton>();
        rows.Add(new NavButton { Text = "SHELVES", Header = true, Height = 26 });
        foreach (var cat in new[] { "All" }.Concat(cats))
        {
            var it = new NavButton { Text = cat, Count = CountOf(cat), Tag = "cat:" + cat };
            string c2 = cat;
            it.Click += (_, _) => { ShowSettings(false); _selCategory = c2; _selFormat = null; ApplyFilter(); };
            rows.Add(it);
        }
        rows.Add(new NavButton { Text = "FORMATS", Header = true, Height = 30 });
        var eb = new NavButton { Text = "EPUB files", Count = epubCount, Tag = "fmt:epub" };
        eb.Click += (_, _) => { ShowSettings(false); _selFormat = "epub"; ApplyFilter(); };
        rows.Add(eb);
        var pb = new NavButton { Text = "PDF files", Count = pdfCount, Tag = "fmt:pdf" };
        pb.Click += (_, _) => { ShowSettings(false); _selFormat = "pdf"; ApplyFilter(); };
        rows.Add(pb);

        rows.Reverse();                                       // Dock=Top: last added lands on top
        _navItems.Controls.AddRange(rows.Cast<Control>().ToArray());
        _navItems.ResumeLayout();
    }

    private void SetViewMode(string mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        var s = AppSettings.Load(); s.LibraryView = mode; s.Save();
        ApplyFilter();
    }

    private void RunAutoSort()
    {
        var (groups, moved) = Library.AutoSort();
        RefreshHistory();
        Friendly(groups > 0 ? $"Auto sort: {moved} novels filed into {groups} series" : "Auto sort: no similar titles found");
        if (groups > 0) Log($"Auto sort created/filled {groups} categories with {moved} novels.");
    }

    private void MoveNovel(string folder, string cat)
    {
        Library.SetCategory(folder, cat);
        RefreshHistory();
    }

    private readonly HashSet<string> _finishDeclined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User hit "The End" in the reader — offer to file the novel under Completed.</summary>
    private void OnBookFinished(string folder)
    {
        if (_finishDeclined.Contains(folder) || !File.Exists(Library.MetaPath(folder))) return;
        var m = Library.LoadOrCreate(folder, "", "");
        if (string.Equals(m.Category, Library.Completed, StringComparison.OrdinalIgnoreCase)) return;
        if (MessageBox.Show(this, $"You reached the end of “{m.Title}”.\nMove it to Completed?",
                "Novel finished", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            MoveNovel(folder, Library.Completed);
        else
            _finishDeclined.Add(folder);   // don't nag again this session
    }

    // ---- card grid plumbing ----

    private void SelectCard(NovelCard c) => SelectFolder(c.Meta.Folder);

    private void SelectFolder(string folder)
    {
        _selFolder = folder;
        foreach (Control k in _grid.Controls) if (k is NovelCard nc) nc.Selected = nc.Meta.Folder == folder;
        foreach (Control k in _listHost.Controls) if (k is NovelListRow r) r.Selected = r.Meta.Folder == folder;
    }

    // ---- built-in site browser (Download tab) ----

    private void ShowSitesMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.FontBase };
        foreach (var (name, url) in Sites)
        {
            var it = new ToolStripMenuItem(name) { ForeColor = Theme.Text, BackColor = Theme.Panel };
            string u = url;
            it.Click += (_, _) =>
            {
                try { _web.CoreWebView2?.Navigate(u); Friendly("Browse to chapter 1, then hit ⚡ Scrape this page"); }
                catch (Exception ex) { Log("Navigate failed: " + ex.Message); }
            };
            menu.Items.Add(it);
        }
        menu.Show(_sitesBtn, new Point(0, _sitesBtn.Height + 4));
    }

    private void OpenSiteInPreview(string url)
    {
        try { _web.CoreWebView2?.Navigate(url); Friendly("Browse to chapter 1, then hit ⚡ Scrape this page"); }
        catch (Exception ex) { Log("Navigate failed: " + ex.Message); }
    }

    private async Task ScrapeCurrentPageAsync()
    {
        if (_busy) { Friendly("A download is already running"); return; }
        string src = "";
        try { src = _web.CoreWebView2?.Source ?? ""; } catch { }
        if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        { Friendly("Open a chapter page in the preview first — try Sites ▾"); return; }
        _url.Text = src;
        await StartAsync();
    }

    private void ShowCardMenu(Point screenPt)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.FontBase };
        void Add(string text, Action onClick, Color? fg = null)
        {
            var it = new ToolStripMenuItem(text) { ForeColor = fg ?? Theme.Text, BackColor = Theme.Panel };
            it.Click += (_, _) => onClick();
            menu.Items.Add(it);
        }
        // Move to → categories
        var mv = new ToolStripMenuItem("Move to") { ForeColor = Theme.Text, BackColor = Theme.Panel };
        string? folder = _selFolder;
        if (folder != null)
        {
            var cur = Library.LoadOrCreate(folder, "", "");
            foreach (var cat in Library.Categories())
            {
                bool here = cat == "General" ? string.IsNullOrEmpty(cur.Category) || cur.Category == "General"
                                             : string.Equals(cur.Category, cat, StringComparison.OrdinalIgnoreCase);
                var it = new ToolStripMenuItem((here ? "✓ " : "") + cat) { ForeColor = Theme.Text, BackColor = Theme.Panel };
                string c2 = cat;
                it.Click += (_, _) => MoveNovel(folder, c2);
                mv.DropDownItems.Add(it);
            }
            mv.DropDownItems.Add(new ToolStripSeparator());
            var nw = new ToolStripMenuItem("New category…") { ForeColor = Theme.Text, BackColor = Theme.Panel };
            nw.Click += (_, _) => { var n = PromptDialog.Ask(this, "New category", "Name for the new category:"); if (n != null) { Library.AddCategory(n); MoveNovel(folder, n); } };
            mv.DropDownItems.Add(nw);
            mv.DropDown.BackColor = Theme.Panel;
            ((ToolStripDropDownMenu)mv.DropDown).Renderer = new DarkMenuRenderer();
            ((ToolStripDropDownMenu)mv.DropDown).ShowImageMargin = false;
            menu.Items.Add(mv);
        }
        Add("Export EPUB", async () => await ExportEpubAsync());
        Add("Export PDF", async () => await ExportPdfAsync());
        Add("Share…", async () => await ShareSelectedAsync());
        Add("Open folder", OpenSelectedFolder);
        menu.Items.Add(new ToolStripSeparator());
        Add("Delete", DeleteSelected, Theme.Danger);
        menu.Show(screenPt);
    }

    private void ShowTab(int tab)
    {
        // instant page switch — no animations
        _pageDl.Visible = tab == 0;
        _pageLib.Visible = tab == 1;
        _pageRead.Visible = tab == 2;
        try { _web.Visible = tab == 0; } catch { }   // WebView2 airspace leaks a sliver if left visible off-tab
        _tabDl.Style = tab == 0 ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost;
        _tabLib.Style = tab == 1 ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost;
        _tabRead.Style = tab == 2 ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost;
        _tabDl.Invalidate(); _tabLib.Invalidate(); _tabRead.Invalidate();
        if (tab == 1) RefreshHistory();
    }

    private Panel BuildReaderPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(20, 8, 20, 16), Visible = false };
        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(8), Radius = 10 };

        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Card };
        var openEpub = new FlatButton { Text = "Open EPUB…", Width = 120, Height = 32, Style = FlatButton.Kind.Ghost, Dock = DockStyle.Right };
        openEpub.Click += async (_, _) => await OpenEpubInReaderAsync();
        _speakBtn.Click += (_, _) => ToggleSpeakPanel();
        bar.Controls.Add(new Label { Dock = DockStyle.Left, AutoSize = true, Text = "READER", Font = Theme.FontSemi, ForeColor = Theme.SubText, Padding = new Padding(4, 10, 0, 0) });
        bar.Controls.Add(_speakBtn);
        bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.Card });
        bar.Controls.Add(openEpub);

        var holder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(1) };
        holder.Controls.Add(_readHint);
        card.Controls.Add(holder);
        card.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Card });
        card.Controls.Add(bar);
        page.Controls.Add(card);
        _readerHolder = holder;
        return page;
    }
    private Panel _readerHolder = null!;

    /// <summary>Swaps the reader surface for the paste-text TTS input (and back) —
    /// the tab-bar button doubles as the way back.</summary>
    private void ToggleSpeakPanel()
    {
        if (_speakPanel == null)
        {
            _speakPanel = new TtsSpeakPanel(_env) { Dock = DockStyle.Fill, Visible = false };
            _readerHolder.Controls.Add(_speakPanel);
        }
        _speakPanel.Visible = !_speakPanel.Visible;
        if (_speakPanel.Visible) _speakPanel.BringToFront();
        _speakBtn.Text = _speakPanel.Visible ? "←  Back to reader" : "Read text";
        _speakBtn.Invalidate();
    }

    private async Task EnsureReaderAsync()
    {
        if (_readerHost != null && _readerHost.Initialized) return;
        if (_env == null) { Log("Browser engine not ready yet."); return; }
        try
        {
            if (_readerHost == null)
            {
                _readerHost = new ReaderHost(Log);
                _readerHost.BookFinished += OnBookFinished;
            }
            _readerHolder.Controls.Clear();
            _readerHolder.Controls.Add(_readerHost.View);
            await _readerHost.InitAsync(_env);
            _readerHost.SetTheme(Theme.Current);   // remembered → applied when the page signals ready
        }
        catch (Exception ex) { Log("Reader init failed: " + ex.Message); }
    }

    private async Task ReadSelectedAsync()
    {
        var m = SelectedNovel(); if (m == null) { Friendly("Pick a novel first"); return; }
        ShowTab(2);
        await EnsureReaderAsync();
        try { _readerHost?.OpenNovel(m); }
        catch (Exception ex) { Log("Read failed: " + ex.Message); }
    }

    private async Task OpenEpubInReaderAsync()
    {
        await EnsureReaderAsync();
        using var ofd = new OpenFileDialog { Filter = "EPUB files (*.epub)|*.epub", Title = "Open an EPUB to read" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try { _readerHost?.OpenEpub(ofd.FileName); }
        catch (Exception ex) { Log("EPUB open failed: " + ex.Message); }
    }

    private void ToggleDetails()
    {
        bool show = !_detailsWrap.Visible;
        _detailsWrap.Height = show ? 190 : 0;
        _detailsWrap.Visible = show;
        _detailsBtn.Text = show ? "Hide details" : "Show details";
        _pageDl.PerformLayout();
        if (show) _log.SelectionStart = _log.TextLength;
    }

    private static Label Lbl(string t, int top, Color? c = null, Font? f = null) =>
        new() { Text = t, AutoSize = true, ForeColor = c ?? Theme.SubText, Font = f ?? Theme.FontBase, Padding = new Padding(0, top, 6, 0) };

    private static void StyleNumeric(NumericUpDown n)
    {
        n.BackColor = Theme.Panel; n.ForeColor = Theme.Text; n.Font = Theme.FontBase;
        n.BorderStyle = BorderStyle.None;                      // the white FixedSingle frame is gone
        foreach (Control c in n.Controls) Theme.DarkScrollbars(c);   // dark up/down spinner
    }

    /// <summary>Hosts a NumericUpDown on a rounded dark pill (matches PillTextBox).</summary>
    private static Control NumericPill(NumericUpDown n)
    {
        var host = new Panel { Width = n.Width + 18, Height = 30, BackColor = Theme.Card, Margin = new Padding(0, 3, 0, 0), Padding = new Padding(9, 5, 3, 5) };
        host.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var p = Theme.RoundRect(new Rectangle(0, 0, host.Width - 1, host.Height - 1), 8);
            using var b = new SolidBrush(Theme.Panel); e.Graphics.FillPath(b, p);
            using var pen = new Pen(Theme.Border); e.Graphics.DrawPath(pen, p);
        };
        n.Dock = DockStyle.Fill; n.Margin = Padding.Empty;
        host.Controls.Add(n);
        return host;
    }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.Timeout = TimeSpan.FromSeconds(30);
        return h;
    }

    private async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovelGrabber", "WebView2");
            Directory.CreateDirectory(dataDir);
            // allow TTS audio to play without a user gesture (reader chains many <audio> clips)
            var envOpts = new CoreWebView2EnvironmentOptions
            { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" };
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, envOpts);
            _env = env;
            await _web.EnsureCoreWebView2Async(env);
            _web.DefaultBackgroundColor = Theme.Panel;
            // ad blocker for the built-in site browser (novel sites are popunder minefields)
            _web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _web.CoreWebView2.WebResourceRequested += (s, e) =>
            {
                bool mainFrame = e.ResourceContext == CoreWebView2WebResourceContext.Document;
                if (AdBlock.ShouldBlock(e.Request.Uri, mainFrame))
                    e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            // popunders get dropped; legit target=_blank links open in the same view
            _web.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (!AdBlock.ShouldBlock(e.Uri, true)) _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.Navigate("about:blank");
            Friendly("Ready"); Status("Paste a chapter or contents URL above, then click Download.");
            Log("Ready. Supported: novellunar · novelfire · ranobes · novelbin · novelhall · katreadingcafe · novelpia · novelarrow · lightnovelworld (+ generic).");
        }
        catch (Exception ex) { Friendly("Browser engine failed to start"); Log("WebView2 init failed: " + ex.Message); }
        RefreshHistory();
    }

    // ---------------- engine ----------------

    private async Task StartAsync()
    {
        var start = _url.Text.Trim();
        if (!Uri.TryCreate(start, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { Friendly("That doesn't look like a web address"); Status("Paste a full http(s):// link."); return; }
        if (_web.CoreWebView2 == null) { Friendly("Still starting up — try again in a moment"); return; }

        _cts = new CancellationTokenSource(); var ct = _cts.Token;
        SetBusy(true);
        int limit = (int)_count.Value; if (limit <= 0) limit = 1_000_000;
        var rule = SiteRules.ForHost(uri.Host);
        string key = Library.NovelKey(uri);
        string novelSlug = key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : "";

        try
        {
            Friendly("Opening the page…"); Status(start);
            Log($"Opening {start}");
            var first = await NavExtract(start, rule, ct);
            if (first == null) { Friendly("Couldn't read that page"); Log("No response from page."); return; }

            string novelTitle = !string.IsNullOrWhiteSpace(first.NovelTitle) ? first.NovelTitle
                : Library.CleanNovelTitle(!string.IsNullOrWhiteSpace(first.OgTitle) ? first.OgTitle : first.DocTitle);
            if (string.IsNullOrWhiteSpace(novelTitle)) novelTitle = SlugFromUrl(uri);

            string? existing = Library.FindByKey(key);
            string folder = existing ?? Path.Combine(Library.Root, Library.Sanitize(novelTitle));
            var meta = Library.LoadOrCreate(folder, novelTitle, start);
            if (string.IsNullOrEmpty(meta.Key)) meta.Key = key;
            Friendly($"Reading “{meta.Title}”");
            Log(existing != null ? $"Merging into existing entry ({meta.Chapters.Count} chapters already saved)." : "New novel.");
            if (string.IsNullOrWhiteSpace(meta.Cover) && !string.IsNullOrWhiteSpace(first.OgImage))
                await TryDownloadCover(meta, first.OgImage, ct);

            int added;
            if (rule.NextClick.Length > 0)
            {
                // SPA that redirects direct chapter URLs to a saved position (novelarrow):
                // start at chapter 1, then advance by clicking the in-page Next button.
                string startChap;
                if (start.Contains("/chapter/", StringComparison.OrdinalIgnoreCase))
                    startChap = start;
                else
                {
                    Friendly("Opening the chapter list…");
                    Log("Tabbed site → finding the first chapter on " + start);
                    var tr = await NavExtractList(start, rule, ct);
                    if (tr != null && string.IsNullOrWhiteSpace(meta.Cover)) await TryDownloadCover(meta, tr.OgImage, ct);
                    var list = FilterToNovel(tr?.Chapters ?? Array.Empty<ChapterLink>(), novelSlug);
                    if (list.Length == 0) { Friendly("Couldn't load the chapter list"); Log("No chapters found."); Library.Save(meta); RefreshHistory(); return; }
                    startChap = SortAscending(list).First().Url;
                }
                Friendly("Downloading… (clicking through chapters)");
                Log("Click-to-advance from " + startChap);
                added = await ClickFollowAsync(meta, startChap, rule, limit, ct);
            }
            else if (first.Content.Trim().Length >= 200 && rule.TocFrom.Length == 2)
            {
                // Reading page on a site whose Next is a JS button & URLs aren't incrementable:
                // don't try to follow — open the contents page and walk the list.
                string toc = DeriveToc(start, rule);
                Log("Reading page → fetching the chapter list" + (toc.Length > 0 ? " → " + toc : ""));
                var tr = toc.Length > 0 ? await NavExtract(toc, rule, ct) : null;
                if (tr != null && string.IsNullOrWhiteSpace(meta.Cover)) await TryDownloadCover(meta, tr.OgImage, ct);
                var more = FilterToNovel(tr?.Chapters ?? Array.Empty<ChapterLink>(), novelSlug);
                if (more.Length > 0) added = await WalkAsync(meta, more, rule, limit, ct);
                else { added = SaveIfNew(meta, first, start) ? 1 : 0; Log("Couldn't read the chapter list — saved just this page."); }
            }
            else if (first.Content.Trim().Length >= 200)
            {
                Log("Chapter page → following Next/▶.");
                added = await FollowAsync(meta, start, first, rule, limit, ct);
            }
            else
            {
                var chapters = FilterToNovel(first.Chapters, novelSlug);
                string entry = first.FirstUrl;
                if (string.IsNullOrWhiteSpace(entry) && chapters.Length > 0)
                    entry = SortAscending(chapters).First().Url;
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    Log("Contents page → jumping to the first chapter.");
                    var er = await NavExtract(entry, rule, ct);
                    if (er != null) await TryDownloadCover(meta, er.OgImage, ct);
                    if (er != null && er.Content.Trim().Length >= 200)
                    {
                        added = await FollowAsync(meta, entry, er, rule, limit, ct);
                        if (chapters.Length > 0 && added < limit && !ct.IsCancellationRequested)
                            added += await WalkAsync(meta, chapters, rule, limit - added, ct);
                    }
                    else if (chapters.Length > 0) { Log("Walking the chapter list."); added = await WalkAsync(meta, chapters, rule, limit, ct); }
                    else { Log("Could not open the first chapter."); added = 0; }
                }
                else if (chapters.Length > 0) { Log("Walking the chapter list."); added = await WalkAsync(meta, chapters, rule, limit, ct); }
                else { Friendly("No chapters found on this page"); Status("Tip: paste the Chapter-1 reading URL."); Log("No content/list found."); added = 0; }
            }

            Library.Save(meta);
            Friendly(added > 0 ? $"Finished — {added} new chapter(s) saved ✓" : "Nothing new to download");
            Status($"“{meta.Title}” now has {meta.Chapters.Count} chapter(s) in your library.");
            Log($"Done. +{added} new, {meta.Chapters.Count} total.");
            RefreshHistory();
        }
        catch (OperationCanceledException) { Friendly("Stopped"); Log("Cancelled by user."); }
        catch (Exception ex) { Friendly("Something went wrong — open ‘details’"); Log("Error: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task<int> FollowAsync(NovelMeta meta, string startUrl, ExtractResult firstResult, SiteRule rule, int limit, CancellationToken ct)
    {
        string cur = startUrl; ExtractResult? cr = firstResult; string prev = "";
        int added = 0, visited = 0, empty = 0, maxVisited = limit + 300;
        while (added < limit && visited < maxVisited && !ct.IsCancellationRequested)
        {
            cr ??= await NavExtract(cur, rule, ct, prev.Length > 0 ? prev : null); visited++;
            if (cr != null && cr.Content.Trim().Length > 0)
            { if (SaveIfNew(meta, cr, cur)) { added++; prev = Sig(cr.Content); Progress(added, limit >= 1_000_000 ? added + 1 : limit); } empty = 0; }
            else { empty++; if (empty >= 3) { Log("3 empty pages — stopping."); break; } }

            string next = "";
            if (cr != null && !rule.PreferIncrement && !string.IsNullOrWhiteSpace(cr.NextUrl) && SameHost(cr.NextUrl, cur)) next = cr.NextUrl;
            if (string.IsNullOrWhiteSpace(next) && rule.Increment) next = IncrementUrl(cur);
            if (string.IsNullOrWhiteSpace(next) || next == cur) { Log("No further chapter — stopping."); break; }
            cur = next; cr = null;
        }
        return added;
    }

    private async Task<int> WalkAsync(NovelMeta meta, ChapterLink[] links, SiteRule rule, int limit, CancellationToken ct)
    {
        var ordered = SortAscending(links);
        int added = 0, total = Math.Min(limit, ordered.Count);
        string prev = "";   // signature of last saved chapter → guarantees each fetch is new content
        foreach (var c in ordered)
        {
            if (added >= limit || ct.IsCancellationRequested) break;
            int num = Library.ParseChapterNumber(c.Title, c.Url);
            if (Library.HasChapter(meta, num, c.Url)) continue;
            var cr = await NavExtract(c.Url, rule, ct, prev.Length > 0 ? prev : null);
            if (cr != null && cr.Content.Trim().Length > 0 && SaveIfNew(meta, cr, c.Url))
            { added++; prev = Sig(cr.Content); Progress(added, total); }
        }
        return added;
    }

    private static System.Collections.Generic.List<ChapterLink> SortAscending(ChapterLink[] links) =>
        links.Select((c, i) => (c, n: Library.ParseChapterNumber(c.Title, c.Url), i))
             .OrderBy(t => t.n > 0 ? t.n : int.MaxValue).ThenBy(t => t.i).Select(t => t.c).ToList();

    /// <summary>Keep only chapter links that belong to THIS novel (drops "latest/recommended"
    /// links to other novels that share the same /chapter/ path shape).</summary>
    private static ChapterLink[] FilterToNovel(ChapterLink[] links, string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length < 4) return links;
        var f = links.Where(l => l.Url.IndexOf(slug, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        return f.Length > 0 ? f : links;
    }

    private static string DeriveToc(string url, SiteRule rule)
    {
        if (rule.TocFrom.Length != 2) return "";
        try
        {
            var r = Regex.Replace(url, rule.TocFrom[0], rule.TocFrom[1]);
            return (r != url && Uri.IsWellFormedUriString(r, UriKind.Absolute)) ? r : "";
        }
        catch { return ""; }
    }

    private bool SaveIfNew(NovelMeta meta, ExtractResult r, string url)
    {
        int num = Library.ParseChapterNumber(r.ChapterTitle, url);
        if (Library.HasChapter(meta, num, url)) return false;
        string title = !string.IsNullOrWhiteSpace(r.ChapterTitle) ? r.ChapterTitle : (num > 0 ? "Chapter " + num : "Chapter");
        Library.AddChapter(meta, num, title, url, r.Content);
        Library.Save(meta);
        Log($"  + {title}  ({r.Content.Length:N0} chars)");
        return true;
    }

    private static string Sig(string? s)
    {
        var t = Regex.Replace(s ?? "", @"\s+", "");
        return t.Length > 160 ? t[..160] : t;
    }

    /// <param name="avoid">Signature of the PREVIOUS chapter's content. The result is only
    /// accepted once the page shows DIFFERENT content — this defeats SPA sites that briefly
    /// keep the old chapter in the DOM (which caused the same chapter to be saved repeatedly).</param>
    private async Task<ExtractResult?> NavExtract(string url, SiteRule rule, CancellationToken ct, string? avoid = null)
    {
        var core = _web.CoreWebView2; if (core == null) return null;
        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object? s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += H;
        try { core.Navigate(url); await Task.WhenAny(nav.Task, Task.Delay(30000, ct)); }
        finally { core.NavigationCompleted -= H; }
        if (avoid != null) await Task.Delay(250, ct);   // let an SPA swap content in
        return await PollExtract(rule, ct, avoid);
    }

    /// <summary>Polls the CURRENT page (no navigation) until content is present and — when
    /// <paramref name="avoid"/> is set — differs from the previous chapter.</summary>
    private async Task<ExtractResult?> PollExtract(SiteRule rule, CancellationToken ct, string? avoid, int tries = 30)
    {
        var core = _web.CoreWebView2; if (core == null) return null;
        string script = Extractor.BuildScript(rule);
        ExtractResult? last = null;
        for (int i = 0; i < tries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = Extractor.Parse(await core.ExecuteScriptAsync(script));
                if (r != null)
                {
                    last = r;
                    bool hasContent = r.Content.Trim().Length > 40;
                    bool fresh = avoid == null || !hasContent || Sig(r.Content) != avoid;
                    if ((hasContent && fresh) || r.Chapters.Length >= 3 || !string.IsNullOrWhiteSpace(r.FirstUrl))
                        return r;
                }
            }
            catch { }
            await Task.Delay(600, ct);
        }
        if (avoid != null && last != null && last.Content.Trim().Length > 40 && Sig(last.Content) == avoid) return null;
        return last;
    }

    private static string BuildNextClickJs(string[] selectors)
    {
        string arr = System.Text.Json.JsonSerializer.Serialize(selectors);
        return @"(function(){var S=" + arr + @";for(var k=0;k<S.length;k++){var els;try{els=document.querySelectorAll(S[k]);}catch(e){continue;}
          for(var i=0;i<els.length;i++){var e=els[i];if(e.offsetParent!==null && !e.disabled){try{e.click();return 'OK';}catch(x){}}}}return 'NONE';})();";
    }

    /// <summary>Advances through a SPA by clicking its in-page "Next" control (novelarrow):
    /// direct chapter URLs get redirected to a saved position, so we navigate once then click.</summary>
    private async Task<int> ClickFollowAsync(NovelMeta meta, string startUrl, SiteRule rule, int limit, CancellationToken ct)
    {
        var core = _web.CoreWebView2; if (core == null) return 0;
        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object? s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += H;
        try { core.Navigate(startUrl); await Task.WhenAny(nav.Task, Task.Delay(30000, ct)); }
        finally { core.NavigationCompleted -= H; }
        await Task.Delay(600, ct);

        string nextJs = BuildNextClickJs(rule.NextClick);
        int added = 0, stall = 0; string prev = "";
        for (int guard = 0; added < limit && guard < limit + 600 && !ct.IsCancellationRequested; guard++)
        {
            var r = await PollExtract(rule, ct, prev.Length > 0 ? prev : null);
            if (r != null && r.Content.Trim().Length > 0)
            {
                string url = string.IsNullOrWhiteSpace(r.Url) ? startUrl : r.Url;
                if (SaveIfNew(meta, r, url)) { added++; Progress(added, limit >= 1_000_000 ? added + 1 : limit); }
                prev = Sig(r.Content); stall = 0;
            }
            else { stall++; if (stall >= 2) { Log("Content stopped changing — reached the end."); break; } }
            if (added >= limit) break;

            string clicked;
            try { clicked = await core.ExecuteScriptAsync(nextJs); } catch { clicked = "\"NONE\""; }
            if (clicked.Contains("NONE")) { Log("No Next button — reached the end."); break; }
            await Task.Delay(700, ct);
        }
        return added;
    }

    private static string? BuildClickJs(string[] tokens)
    {
        if (tokens == null || tokens.Length == 0) return null;
        string toks = System.Text.Json.JsonSerializer.Serialize(tokens);
        return @"(function(){var T=" + toks + @".map(function(x){return x.toLowerCase();});
          var els=document.querySelectorAll('button,a,[role=tab],[role=button],li,span,div');
          for(var i=0;i<els.length;i++){var el=els[i]; if(el.children.length>3) continue;
             var t=(el.textContent||'').replace(/[^a-zA-Z]/g,'').toLowerCase();
             if(T.indexOf(t)>=0){ var c=el.closest('button,a,[role=tab],[role=button]')||el; try{c.click();return true;}catch(e){} }
          } return false;})();";
    }

    /// <summary>Loads a contents page that hides its chapter list behind a tab (novelarrow):
    /// clicks the tab, scrolls to pull in lazy-loaded rows, and waits until the count stabilises.</summary>
    private async Task<ExtractResult?> NavExtractList(string url, SiteRule rule, CancellationToken ct)
    {
        var core = _web.CoreWebView2; if (core == null) return null;
        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object? s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += H;
        try { core.Navigate(url); await Task.WhenAny(nav.Task, Task.Delay(30000, ct)); }
        finally { core.NavigationCompleted -= H; }
        await Task.Delay(600, ct);   // let the SPA hydrate

        string? clickJs = BuildClickJs(rule.TabClick);
        string script = Extractor.BuildScript(rule);
        ExtractResult? best = null;
        int lastCount = -1, stable = 0;
        for (int i = 0; i < 45; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { if (clickJs != null) await core.ExecuteScriptAsync(clickJs); } catch { }
            try { await core.ExecuteScriptAsync("window.scrollTo(0,document.documentElement.scrollHeight);"); } catch { }
            try
            {
                var r = Extractor.Parse(await core.ExecuteScriptAsync(script));
                if (r != null)
                {
                    best = r;
                    int c = r.Chapters.Length;
                    if (c >= 3)
                    {
                        if (c == lastCount) { if (++stable >= 3) return r; }
                        else stable = 0;
                        lastCount = c;
                        Status($"Loading chapter list… {c} found");
                    }
                }
            }
            catch { }
            await Task.Delay(500, ct);
        }
        return best;
    }

    private async Task TryDownloadCover(NovelMeta meta, string coverUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) || !string.IsNullOrWhiteSpace(meta.Cover)) return;
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var cu)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, cu);
            // some image CDNs require a referer matching the site
            if (Uri.TryCreate(meta.Source, UriKind.Absolute, out var su))
                req.Headers.Referrer = new Uri(su.GetLeftPart(UriPartial.Authority) + "/");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 512) return;   // not a real image
            string ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            string ext = ctype.Contains("png") || coverUrl.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "png"
                       : ctype.Contains("webp") || coverUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase) ? "webp" : "jpg";
            await File.WriteAllBytesAsync(Path.Combine(meta.Folder, "cover." + ext), bytes, ct);
            meta.Cover = "cover." + ext; meta.CoverUrl = coverUrl; Library.Save(meta);
            Log("Cover saved.");
        }
        catch { }
    }

    // ---------------- PDF ----------------

    private async Task PrintHtmlToPdf(string html, string outPath)
    {
        ShowTab(0);   // the PDF renders through the download tab's browser
        var core = _web.CoreWebView2 ?? throw new InvalidOperationException("Browser engine not ready.");
        var temp = Path.Combine(Path.GetTempPath(), "ng_print_" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(temp, html, new UTF8Encoding(false));
        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object? s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += H;
        try { core.Navigate(new Uri(temp).AbsoluteUri); await Task.WhenAny(nav.Task, Task.Delay(20000)); }
        finally { core.NavigationCompleted -= H; }
        await Task.Delay(500);
        bool ok = await core.PrintToPdfAsync(outPath, null);
        try { File.Delete(temp); } catch { }
        _web.CoreWebView2.Navigate("about:blank");
        if (!ok) throw new Exception("PDF rendering failed.");
    }

    private async Task ExportPdfAsync()
    {
        var m = SelectedNovel(); if (m == null) { Friendly("Pick a novel first"); return; }
        try
        {
            Friendly($"Making a PDF of “{m.Title}”…");
            var (_, html) = PdfExport.FromNovel(m);
            string outp = Path.Combine(m.Folder, Library.Sanitize(m.Title) + ".pdf");
            await PrintHtmlToPdf(html, outp);
            Friendly("PDF saved ✓"); Log("PDF: " + outp);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outp}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Friendly("PDF export failed"); Log("PDF error: " + ex.Message); }
    }

    // ---------------- library actions ----------------

    private async Task ExportEpubAsync()
    {
        var m = SelectedNovel(); if (m == null) { Friendly("Pick a novel first"); return; }
        try
        {
            Friendly($"Building EPUB of “{m.Title}”…");
            string path = await Task.Run(() => EpubWriter.Build(m));
            Friendly("EPUB saved ✓"); Log("EPUB: " + path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Friendly("EPUB export failed"); Log("EPUB error: " + ex.Message); }
    }

    private void OpenSelectedFolder()
    {
        var m = SelectedNovel(); if (m == null) { Friendly("Pick a novel first"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{m.Folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log("Open failed: " + ex.Message); }
    }

    private async Task ShareSelectedAsync()
    {
        var m = SelectedNovel(); if (m == null) { Friendly("Pick a novel first"); return; }
        // share an EPUB (build one if none exists yet); fall back to a PDF
        string? file = Directory.GetFiles(m.Folder, "*.epub").FirstOrDefault()
                     ?? Directory.GetFiles(m.Folder, "*.pdf").FirstOrDefault();
        if (file == null)
        {
            try { Friendly("Preparing EPUB to share…"); file = await Task.Run(() => EpubWriter.Build(m)); }
            catch (Exception ex) { Friendly("Couldn't prepare a file to share"); Log(ex.Message); return; }
        }
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "share" });
            Friendly("Pick Quick Share in the panel →"); Log("Opened Windows Share for " + Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            Log("Share sheet unavailable (" + ex.Message + ") — opening the file location instead.");
            Friendly("Right-click the file → Share → Quick Share");
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); } catch { }
        }
    }

    private void DeleteSelected()
    {
        var m = SelectedNovel(); if (m == null) { Friendly("Pick a novel first"); return; }
        if (MessageBox.Show(this, $"Delete “{m.Title}” and all its files?", "Confirm delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { Directory.Delete(m.Folder, true); Log("Deleted " + m.Title); RefreshHistory(); }
        catch (Exception ex) { Log("Delete failed: " + ex.Message); }
    }

    private void DeleteAll()
    {
        var all = Library.History();
        if (all.Count == 0) { Friendly("Library is already empty"); return; }
        if (MessageBox.Show(this, $"Delete ALL {all.Count} novels and every downloaded file?\nThis cannot be undone.",
                "Delete entire library", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        int n = 0;
        foreach (var m in all) { try { Directory.Delete(m.Folder, true); n++; } catch (Exception ex) { Log("Couldn't delete " + m.Title + ": " + ex.Message); } }
        Library.ClearCategories();          // auto-sort's now-empty series shouldn't linger
        _selCategory = "General"; _selFormat = null;
        Log($"Deleted {n} novel(s)."); Friendly("Library cleared");
        ShowSettings(false); RefreshHistory();
    }

    private void ChooseLibrary()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Library.Root };
        if (dlg.ShowDialog(this) == DialogResult.OK) { Library.Root = dlg.SelectedPath; _libLbl.Text = Library.Root; RefreshHistory(); }
    }

    /// <summary>Add book(s): pick individual .epub files to import.</summary>
    private async Task AddBooksAsync()
    {
        using var ofd = new OpenFileDialog { Filter = "EPUB files (*.epub)|*.epub", Multiselect = true, Title = "Add books" };
        if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;
        await RunImportAsync(ofd.FileNames);
    }

    /// <summary>Add folder: bulk-import every .epub in a chosen folder (subfolders included).</summary>
    private async Task ImportEpubsAsync()
    {
        using var dlg = new FolderBrowserDialog
        { Description = "Pick a folder — every .epub inside it (subfolders too) will be imported", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string[] files;
        try { files = Directory.GetFiles(dlg.SelectedPath, "*.epub", SearchOption.AllDirectories); }
        catch (Exception ex) { Log("Import scan failed: " + ex.Message); return; }
        if (files.Length == 0) { MessageBox.Show(this, "No .epub files found in that folder.", "Import EPUBs"); return; }
        await RunImportAsync(files);
    }

    private async Task RunImportAsync(string[] files)
    {
        int ok = 0, skip = 0, fail = 0;
        var cts = new CancellationTokenSource();
        using var prog = new ImportProgressForm(files.Length);
        prog.Cancelled += cts.Cancel;
        Task work = Task.CompletedTask;
        prog.Shown += (_, _) => work = Task.Run(() =>
        {
            for (int i = 0; i < files.Length && !cts.IsCancellationRequested; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                prog.Report(i + 1, name);
                try
                {
                    var (r, _) = EpubImport.ImportOne(files[i]);
                    if (r == EpubImport.Result.Imported) ok++; else skip++;
                }
                catch (Exception ex) { fail++; Log($"Import failed: {name} — {ex.Message}"); }
            }
        }).ContinueWith(_ => { try { prog.BeginInvoke(prog.Close); } catch { } });
        prog.ShowDialog(this);
        await work;

        if (ok > 0 && AppSettings.Load().AutoSortOnAdd)         // auto-file new books into series
        { try { Library.AutoSort(); } catch { } }

        RefreshHistory();
        string msg = $"Imported {ok} novel(s)."
            + (skip > 0 ? $"\nSkipped {skip} already in the library." : "")
            + (fail > 0 ? $"\n{fail} failed — see the details log on the Download tab." : "");
        MessageBox.Show(this, msg, "Import EPUBs");
    }

    private string _gridSig = "\0";   // sentinel ≠ any real signature so the first call always builds
    private int _novelCount;

    /// <summary>Rebuilds controls for the WHOLE library when its content changes. Shelf/format/
    /// search/view switches never come through here — ApplyFilter() just toggles visibility,
    /// which is what makes switching categories instant.</summary>
    private void RefreshHistory()
    {
        if (_grid.InvokeRequired) { _grid.BeginInvoke(RefreshHistory); return; }
        var all = Library.History();
        var cats = Library.Categories();
        var epubs = LibFiles("*.epub");
        var pdfs = LibFiles("*.pdf");
        string sig = string.Join(",", cats) + "\n"
                   + string.Join(";", epubs.Select(f => f.path)) + "#" + string.Join(";", pdfs.Select(f => f.path)) + "\n"
                   + string.Join("\n", all.Select(m => $"{m.Folder}|{m.Updated}|{m.Chapters.Count}|{m.Category}|{Library.ProgressPct(m)}"));
        if (sig == _gridSig) { ApplyFilter(); return; }
        _gridSig = sig;
        _novelCount = all.Count;

        RebuildNav(all, cats, epubs.Count, pdfs.Count);

        _grid.SuspendLayout(); _listHost.SuspendLayout(); _fileHost.SuspendLayout();
        foreach (Control c in _grid.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (Control c in _listHost.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (Control c in _fileHost.Controls.Cast<Control>().ToArray()) c.Dispose();
        _grid.Controls.Clear(); _listHost.Controls.Clear(); _fileHost.Controls.Clear();

        // cards + list rows for EVERY novel, file rows for every export — filters only hide them
        var cards = new List<Control>(all.Count);
        var rows = new List<Control>(all.Count);
        foreach (var m in all)
        {
            var card = new NovelCard(m, ShortHost(m.Source), SafeDate(m.Updated)) { Margin = new Padding(0, 0, 10, 10) };
            card.Chosen += SelectCard;
            card.ReadRequested += async c => { SelectCard(c); await ReadSelectedAsync(); };
            card.MoreRequested += (c, pt) => { SelectCard(c); ShowCardMenu(pt); };
            card.Selected = m.Folder == _selFolder;
            cards.Add(card);

            var row = new NovelListRow(m, ShortHost(m.Source), SafeDate(m.Updated), Library.ProgressPct(m));
            row.Chosen += r => SelectFolder(r.Meta.Folder);
            row.ReadRequested += async r => { SelectFolder(r.Meta.Folder); await ReadSelectedAsync(); };
            row.MoreRequested += (r, pt) => { SelectFolder(r.Meta.Folder); ShowCardMenu(pt); };
            row.Selected = m.Folder == _selFolder;
            rows.Add(row);
        }
        _grid.Controls.AddRange(cards.ToArray());             // one layout pass instead of one per card
        rows.Reverse();                                       // Dock=Top stacking
        _listHost.Controls.AddRange(rows.ToArray());

        var fileRows = new List<Control>();
        foreach (var (path, sub) in epubs.Concat(pdfs))
        {
            var fr = new FileRow(path, sub);
            fr.OpenRequested += async r => await OpenLibFileAsync(r.Path);
            fr.MoreRequested += (r, pt) => ShowFileMenu(r.Path, pt);
            fileRows.Add(fr);
        }
        fileRows.Reverse();
        _fileHost.Controls.AddRange(fileRows.ToArray());

        _grid.ResumeLayout(); _listHost.ResumeLayout(); _fileHost.ResumeLayout();
        ApplyFilter();
    }

    // ---- settings page (lives in the library content area) ----

    private void ShowSettings(bool show)
    {
        if (_showingSettings == show) { if (show) { _settingsHost.Visible = true; _settingsHost.BringToFront(); } return; }
        _showingSettings = show;
        _settingsBtn.Style = show ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost; _settingsBtn.Invalidate();
        if (show)
        {
            _grid.Visible = _listHost.Visible = _fileHost.Visible = _emptyLbl.Visible = false;
            RefreshSettingsState();
            _settingsHost.Visible = true; _settingsHost.BringToFront();
        }
        else { _settingsHost.Visible = false; ApplyFilter(); }
    }

    private void RefreshSettingsState()
    {
        var s = AppSettings.Load();
        _setLibLbl.Text = Library.Root;
        _gkeyBox.Text = s.GoogleTtsKey;
        _autoSortToggle.On = s.AutoSortOnAdd;
        string[] order = { "dark", "darksepia", "sepia", "light" };
        for (int i = 0; i < _themeBtns.Count; i++)
            _themeBtns[i].Style = order[i] == Theme.Current ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost;
        foreach (var b in _themeBtns) b.Invalidate();
    }

    private void BuildSettingsPanel()
    {
        // stacked rows (added bottom-up so Dock=Top reads top-down)
        var host = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(4, 4, 4, 20) };

        Label Head(string t) => new() { Dock = DockStyle.Top, Height = 30, Text = t, Font = Theme.FontSemi, ForeColor = Theme.SubText, Padding = new Padding(0, 8, 0, 0) };
        Panel Gap(int h) => new() { Dock = DockStyle.Top, Height = h, BackColor = Theme.Bg };

        // --- Appearance: 4 theme choices ---
        var themeRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, BackColor = Theme.Bg };
        (string id, string label)[] themes = { ("dark", "Dark"), ("darksepia", "Dark Sepia"), ("sepia", "Sepia"), ("light", "Light") };
        foreach (var (id, label) in themes)
        {
            var b = new FlatButton { Text = label, Width = 104, Height = 34, Style = FlatButton.Kind.Ghost, Margin = new Padding(0, 0, 8, 0) };
            string tid = id;
            b.Click += (_, _) => { SetAppTheme(tid); RefreshSettingsState(); };
            _themeBtns.Add(b);
            themeRow.Controls.Add(b);
        }

        // --- Library folder ---
        var libRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, BackColor = Theme.Bg };
        var libChange = new FlatButton { Text = "Change…", Width = 90, Height = 32, Style = FlatButton.Kind.Ghost, Margin = new Padding(0, 0, 10, 0) };
        libChange.Click += (_, _) => { ChooseLibrary(); _setLibLbl.Text = Library.Root; };
        _setLibLbl.Padding = new Padding(0, 8, 0, 0);
        libRow.Controls.Add(libChange); libRow.Controls.Add(_setLibLbl);

        // --- Google TTS key ---
        var keyRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, BackColor = Theme.Bg };
        _gkeyBox.Box.UseSystemPasswordChar = true; _gkeyBox.PlaceholderText = "Paste Google Cloud TTS API key…";
        _gkeyBox.Margin = new Padding(0, 3, 10, 0);
        var keySave = new FlatButton { Text = "Save", Width = 76, Height = 34, Style = FlatButton.Kind.Accent, Margin = new Padding(0, 3, 0, 0) };
        keySave.Click += (_, _) =>
        {
            var s = AppSettings.Load(); s.GoogleTtsKey = _gkeyBox.Text.Trim(); s.Save();
            _readerHost?.RefreshVoices();
            Friendly(s.GoogleTtsKey.Length > 0 ? "Google TTS key saved ✓" : "Google TTS key cleared");
        };
        keyRow.Controls.Add(_gkeyBox); keyRow.Controls.Add(keySave);
        var keyHint = new Label { Dock = DockStyle.Top, Height = 34, ForeColor = Theme.SubText, Font = Theme.FontSub, Text = "Optional — adds Google's own neural voices to the reader. console.cloud.google.com → enable Cloud Text-to-Speech API → API key." };

        // --- Auto-sort on add ---
        var autoRow = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg };
        _autoSortToggle.Location = new Point(2, 9);
        _autoSortToggle.Size = new Size(46, 26);
        _autoSortToggle.Toggled += (_, _) => { var s = AppSettings.Load(); s.AutoSortOnAdd = _autoSortToggle.On; s.Save(); };
        autoRow.Controls.Add(_autoSortToggle);
        autoRow.Controls.Add(new Label { AutoSize = true, Location = new Point(60, 13), Text = "Auto-sort new books into series", ForeColor = Theme.Text, Font = Theme.FontBase });

        // --- Delete all ---
        var delRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, BackColor = Theme.Bg };
        var del = new FlatButton { Text = "Delete all novels", Width = 150, Height = 34, Style = FlatButton.Kind.Danger };
        del.Click += (_, _) => DeleteAll();
        delRow.Controls.Add(del);

        // stack bottom-up
        var stack = new List<Control>
        {
            Head("APPEARANCE"), themeRow, Gap(8),
            Head("LIBRARY FOLDER"), libRow, Gap(8),
            Head("GOOGLE CLOUD TTS"), keyRow, keyHint, Gap(8),
            Head("ON IMPORT"), autoRow, Gap(8),
            Head("DANGER ZONE"), delRow,
        };
        stack.Reverse();
        host.Controls.AddRange(stack.ToArray());

        _settingsHost.Padding = new Padding(20, 8, 20, 8);
        _settingsHost.Controls.Add(host);
    }

    /// <summary>Applies shelf/format/search/view state by toggling visibility — no rebuild.</summary>
    private void ApplyFilter()
    {
        if (_showingSettings) return;   // settings page owns the content area
        string q = _search.Text.Trim();

        foreach (Control c in _navItems.Controls)
            if (c is NavButton { Header: false } nb && nb.Tag is string tag)
            {
                bool sel = tag.StartsWith("cat:") ? _selFormat == null && tag[4..] == _selCategory
                                                  : _selFormat == tag[4..];
                if (nb.Selected != sel) { nb.Selected = sel; nb.Invalidate(); }
            }
        _viewGridBtn.Style = _viewMode == "grid" ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost;
        _viewListBtn.Style = _viewMode == "list" ? FlatButton.Kind.Accent : FlatButton.Kind.Ghost;
        _viewGridBtn.Invalidate(); _viewListBtn.Invalidate();

        int shown = 0;
        _grid.SuspendLayout(); _listHost.SuspendLayout(); _fileHost.SuspendLayout();
        if (_selFormat != null)
        {
            string ext = "." + _selFormat;
            foreach (Control c in _fileHost.Controls)
                if (c is FileRow fr)
                {
                    bool vis = fr.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                            && (q.Length == 0 || Path.GetFileName(fr.Path).Contains(q, StringComparison.OrdinalIgnoreCase));
                    fr.Visible = vis;
                    if (vis) shown++;
                }
            _emptyLbl.Text = "No " + _selFormat.ToUpperInvariant() + " files yet.\nExport a novel to create one.";
            _grid.Visible = false; _listHost.Visible = false;
            _fileHost.Visible = shown > 0;
        }
        else
        {
            bool InShelf(NovelMeta m) => _selCategory switch
            {
                "All" => true,
                "General" => string.IsNullOrEmpty(m.Category) || m.Category == "General",
                _ => string.Equals(m.Category, _selCategory, StringComparison.OrdinalIgnoreCase)
            };
            bool Match(NovelMeta m) => InShelf(m) && (q.Length == 0
                || m.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || m.Author.Contains(q, StringComparison.OrdinalIgnoreCase));

            bool grid = _viewMode == "grid";
            var host = grid ? (Control)_grid : _listHost;
            foreach (Control c in host.Controls)
            {
                var meta = c switch { NovelCard nc => nc.Meta, NovelListRow nr => nr.Meta, _ => null };
                if (meta == null) continue;
                bool vis = Match(meta);
                c.Visible = vis;
                if (vis) shown++;
            }
            _emptyLbl.Text = _novelCount == 0 ? "Nothing here yet.\nDownload a novel from the Download tab."
                           : q.Length > 0 ? "No matches for “" + q + "”."
                           : "Nothing in this category yet.";
            _grid.Visible = grid && shown > 0;
            _listHost.Visible = !grid && shown > 0;
            _fileHost.Visible = false;
        }
        _emptyLbl.Visible = shown == 0;
        _grid.ResumeLayout(); _listHost.ResumeLayout(); _fileHost.ResumeLayout();
        FitCards();
    }

    private bool _fitting;

    /// <summary>Stretches the cards so each row fills the grid width — no dead margin on the right.
    /// Re-entrancy guarded: setting card widths can toggle the scrollbar which fires grid.Resize,
    /// which would call back into here and thrash the layout (the 1-second lag).</summary>
    private void FitCards()
    {
        if (_fitting || !_grid.Visible) return;
        _fitting = true;
        try
        {
            int avail = _grid.ClientSize.Width - _grid.Padding.Horizontal - 2;
            if (avail < NovelCard.CardW) return;
            const int gap = 10;                                   // matches the card Margin
            int cols = Math.Max(1, (avail + gap) / (NovelCard.CardW + gap));
            int w = Math.Clamp((avail - cols * gap) / cols, NovelCard.CardW, NovelCard.MaxW);
            _grid.SuspendLayout();
            foreach (Control c in _grid.Controls)
                if (c is NovelCard && c.Visible && c.Width != w) c.Width = w;
            _grid.ResumeLayout();
        }
        finally { _fitting = false; }
    }

    /// <summary>All files of a given pattern across the library (novel folders + root).</summary>
    private static List<(string path, string sub)> LibFiles(string pattern)
    {
        var list = new List<(string, string)>();
        try
        {
            if (!Directory.Exists(Library.Root)) return list;
            foreach (var f in Directory.GetFiles(Library.Root, pattern)) list.Add((f, FileSub(f, "Library")));
            foreach (var dir in Directory.GetDirectories(Library.Root))
                foreach (var f in Directory.GetFiles(dir, pattern))
                    list.Add((f, FileSub(f, Path.GetFileName(dir))));
        }
        catch { }
        return list;
    }
    private static string FileSub(string path, string where)
    {
        try { var fi = new FileInfo(path); return $"{where} · {fi.Length / 1024.0 / 1024.0:0.#} MB · {fi.LastWriteTime:MM/dd}"; }
        catch { return where; }
    }

    private async Task OpenLibFileAsync(string path)
    {
        if (path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            ShowTab(2);
            await EnsureReaderAsync();
            try { _readerHost?.OpenEpub(path); } catch (Exception ex) { Log("EPUB open failed: " + ex.Message); }
        }
        else
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log("Open failed: " + ex.Message); }
        }
    }

    private void ShowFileMenu(string path, Point screenPt)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.FontBase };
        void Add(string text, Action onClick, Color? fg = null)
        {
            var it = new ToolStripMenuItem(text) { ForeColor = fg ?? Theme.Text, BackColor = Theme.Panel };
            it.Click += (_, _) => onClick();
            menu.Items.Add(it);
        }
        Add("Open", async () => await OpenLibFileAsync(path));
        Add("Open folder", () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } });
        menu.Items.Add(new ToolStripSeparator());
        Add("Delete file", () =>
        {
            if (MessageBox.Show(this, $"Delete “{Path.GetFileName(path)}”?", "Confirm delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { File.Delete(path); } catch (Exception ex) { Log("Delete failed: " + ex.Message); }
            RefreshHistory();
        }, Theme.Danger);
        menu.Show(screenPt);
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";
    /// <summary>"novelarrow.com" → "novelarrow" — keeps the card sub-line short enough to fit the date.</summary>
    private static string ShortHost(string url) { var h = HostOf(url); int i = h.IndexOf('.'); return i > 0 ? h[..i] : h; }
    private static string SafeDate(string s) => DateTime.TryParse(s, out var d) ? d.ToString("MM/dd") : "";

    private NovelMeta? SelectedNovel()
    {
        if (_selFolder == null || !File.Exists(Library.MetaPath(_selFolder))) return null;
        var m = Library.LoadOrCreate(_selFolder, "", ""); m.Folder = _selFolder; return m;
    }

    // ---------------- ui plumbing ----------------

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _start.Enabled = !busy; _stop.Enabled = busy; _url.Box.Enabled = !busy; _count.Enabled = !busy;
        if (!busy) _bar.Value = 0;
    }

    private void Progress(int done, int total)
    {
        void Apply() { if (total > 0) { _bar.Maximum = total; _bar.Value = done; } Friendly($"Downloading…  {done}{(total >= 1_000_000 ? "" : " of " + total)}"); }
        if (_bar.InvokeRequired) _bar.BeginInvoke(Apply); else Apply();
    }

    private void Friendly(string s) { if (_friendly.InvokeRequired) _friendly.BeginInvoke(() => _friendly.Text = s); else _friendly.Text = s; }
    private void Status(string s) { if (_status.InvokeRequired) _status.BeginInvoke(() => _status.Text = s); else _status.Text = s; }
    private void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
        if (_log.InvokeRequired) _log.BeginInvoke(() => _log.AppendText(line)); else _log.AppendText(line);
    }

    // ---------------- url helpers ----------------

    private static bool SameHost(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub) &&
        string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase);

    private static string IncrementUrl(string url)
    {
        var m = Regex.Match(url, @"\d+(?=[^\d]*$)");
        if (!m.Success || !long.TryParse(m.Value, out var n)) return "";
        return url[..m.Index] + (n + 1) + url[(m.Index + m.Length)..];
    }

    private static string SlugFromUrl(Uri uri)
    {
        var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var seg = segs.FirstOrDefault(s => s.Length > 3 && !int.TryParse(s, out _) &&
                       !s.Equals("novel", StringComparison.OrdinalIgnoreCase) && !s.Equals("book", StringComparison.OrdinalIgnoreCase) &&
                       !s.Equals("b", StringComparison.OrdinalIgnoreCase) && !s.Equals("chapter", StringComparison.OrdinalIgnoreCase))
                  ?? segs.FirstOrDefault() ?? uri.Host;
        return Library.Sanitize(seg.Replace('-', ' ').Replace('_', ' '));
    }
}
