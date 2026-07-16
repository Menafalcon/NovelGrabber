using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NovelGrabber;

/// <summary>
/// CSS-selector recipe for one site. Empty fields fall back to generic detection
/// (largest text block, rel=next / "Next" text) and numeric URL-increment.
/// </summary>
public sealed class SiteRule
{
    public string[] Content { get; set; } = Array.Empty<string>();
    public string[] NovelTitle { get; set; } = Array.Empty<string>();
    public string[] ChapterTitle { get; set; } = Array.Empty<string>();
    public string[] Next { get; set; } = Array.Empty<string>();
    public string[] List { get; set; } = Array.Empty<string>();
    /// <summary>Selectors for a "first chapter" link on a TOC/novel page.</summary>
    public string[] First { get; set; } = Array.Empty<string>();
    /// <summary>Advance by adding 1 to the trailing number in the URL when no Next link.</summary>
    public bool Increment { get; set; } = true;
    /// <summary>Ignore in-page Next links and ALWAYS advance by URL number (SPA sites).</summary>
    public bool PreferIncrement { get; set; }
    /// <summary>[regexPattern, replacement] to derive the novel's contents/TOC URL from a chapter
    /// URL (for sites whose Next is a JS button and whose chapter URLs aren't incrementable).</summary>
    public string[] TocFrom { get; set; } = Array.Empty<string>();
    /// <summary>Tab/label text to click on the contents page to reveal a lazy-loaded chapter list
    /// (e.g. novelarrow's "Chapters" tab). The loader also scrolls to pull in the full list.</summary>
    public string[] TabClick { get; set; } = Array.Empty<string>();
    /// <summary>Selectors for an in-page "Next chapter" control that advances CLIENT-SIDE.
    /// Required for SPAs that redirect direct chapter URLs back to a saved reading position
    /// (novelarrow) — the only way through is to click Next like a reader does.</summary>
    public string[] NextClick { get; set; } = Array.Empty<string>();
}

public static class SiteRules
{
    private static Dictionary<string, SiteRule>? _rules;

    public static Dictionary<string, SiteRule> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["novellunar.com"] = new SiteRule
        {
            Content = new[] { "div[style*=\"white-space:pre-wrap\"]", "div[style*=\"white-space: pre-wrap\"]" },
            ChapterTitle = new[] { "h1.text-lg", "main h1" },
            Increment = true,
            PreferIncrement = true
        },
        ["novelfire.net"] = new SiteRule
        {
            Content = new[] { "#content", ".chapter-content", "#chapter-container" },
            List = new[] { "ul.chapter-list li a", ".chapter-list a" },
            Next = new[] { "a#next-chapter", "a.next-chapter", "a.next", "a[rel=next]" },
            ChapterTitle = new[] { ".chapter-title", ".titles h2", "h1", "h2" },
            Increment = true            // /book/<slug>/chapter-N  → increment works
        },
        ["ranobes.top"] = new SiteRule
        {
            Content = new[] { "#arrticle", "#cont-text", ".story .text", "#dle-content .text", ".story_line" },
            List = new[] { ".cat_block.cat_line a", ".cat_block a" },
            Next = new[] { "a#next", "a.next", ".nextlink a", "a[rel=next]" },
            ChapterTitle = new[] { "h1.h2", ".story_c h1", "h1" },
            Increment = false           // chapter URLs use non-sequential internal IDs
        },
        ["novelbin.com"] = NovelBin(),
        ["novelbin.me"] = NovelBin(),
        ["novelhall.com"] = new SiteRule
        {
            Content = new[] { "#htmlContent", "#txt", ".entry-content", "div.content" },
            List = new[] { ".book-catalog a", "#morelist a", "ul.chapterlist a" },
            Next = new[] { "#next_url", "a#next", "a.next", "a[rel=next]" },
            ChapterTitle = new[] { ".chapter h1", "h1" },
            Increment = true            // /<slug>-<id>/<consecutive-id>.html → increment works
        },
        ["katreadingcafe.com"] = new SiteRule
        {
            Content = new[] { ".epcontent", ".entry-content", "#readerarea", ".text-left", ".reading-content" },
            List = new[] { ".eplister a", "#chapterlist a", "li.wp-manga-chapter a", ".version-chap a" },
            First = new[] { ".lastend .inepcx a", ".epcurfirst" },
            Next = new[] { "a[rel=next]", ".naveps a.next", "a.ch-next", ".nextprev a.next" },
            ChapterTitle = new[] { ".entry-title", "h1" },
            Increment = false           // chapter URLs carry a title suffix → rely on rel=next
        },
        ["novelpia.com"] = NovelPia(),
        ["global.novelpia.com"] = NovelPia(),
        ["novelarrow.com"] = new SiteRule
        {
            // Next is a JS <button> and chapter URLs carry a title suffix (…/chapter-1-some-title)
            // so neither a next-link nor URL increment works — drive it from the main-page list.
            Content = new[] { "article[data-chapter-id]", "article", ".chapter-content", "#chapter-content" },
            List = new[] { "a[href*=\"/chapter/\"]" },
            ChapterTitle = new[] { "h1", "h2" },
            Increment = false,
            TocFrom = new[] { "/chapter/([^/]+)/.*$", "/novel/$1" },
            TabClick = new[] { "Chapters" },          // main page opens on Synopsis; click Chapters to find ch.1
            NextClick = new[] { "[aria-label=\"Next chapter\"]", "a.next-btn", "button.next-btn" } // advance by clicking, not URL
        },
        ["lightnovelworld.org"] = LightNovelWorld(),
        ["lightnovelworld.com"] = LightNovelWorld(),
        ["lightnovelpub.com"] = LightNovelWorld(),
        ["novelfull.net"] = NovelFull(),
        ["novelfull.com"] = NovelFull(),
    };

    private static SiteRule NovelFull() => new()
    {
        Content = new[] { "#chapter-content", ".chapter-c", ".chapter-content" },
        ChapterTitle = new[] { ".chapter-title", "a.chapter-title", "span.chapter-text" },
        Next = new[] { "#next_chap", "a#next_chap", "a[rel=next]" },
        List = new[] { ".list-chapter a", "#list-chapter a", "ul.list-chapter a" },
        Increment = false            // /<slug>/chapter-N-<title>.html → rely on #next_chap
    };

    private static SiteRule LightNovelWorld() => new()
    {
        Content = new[] { "#chapterText", ".chapter-text", ".chapter-reader" },
        Next = new[] { "a.next-btn", ".nav-btn.next-btn", "a[rel=next]" },
        List = new[] { ".chapter-list a", "#chapter-list a", ".chapters a" },
        ChapterTitle = new[] { ".chapter-title", "h1" },
        Increment = true            // /novel/<slug>/chapter/N/ → increment works
    };

    private static SiteRule NovelBin() => new()
    {
        Content = new[] { "#chr-content", ".chr-c", ".chapter-content" },
        List = new[] { "#list-chapter a", ".list-chapter a", "ul.list-chapter a" },
        Next = new[] { "a#next_chap", "a.next_chap", "a[rel=next]" },
        ChapterTitle = new[] { ".chr-title", "h2", "h1" },
        Increment = true
    };

    private static SiteRule NovelPia() => new()
    {
        Content = new[] { "#novel_drawing", ".viewer", "#viewer", ".novel-text", "article" },
        ChapterTitle = new[] { ".chapter-title", "h1", "h2" },
        Increment = false
    };

    public static Dictionary<string, SiteRule> All
    {
        get
        {
            if (_rules != null) return _rules;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "sites.json");
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, SiteRule>>(
                        File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded is { Count: > 0 })
                        return _rules = new Dictionary<string, SiteRule>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return _rules = Defaults();
        }
    }

    public static SiteRule ForHost(string host)
    {
        host = (host ?? "").ToLowerInvariant();
        foreach (var kv in All)
            if (host == kv.Key || host.EndsWith("." + kv.Key) || host.EndsWith(kv.Key))
                return kv.Value;
        return new SiteRule();
    }
}
