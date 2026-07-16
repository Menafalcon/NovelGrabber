using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovelGrabber;

public sealed class ChapterMeta
{
    public int Num { get; set; } = -1;     // parsed chapter number (-1 if unknown)
    public int Seq { get; set; }           // order added
    public string Title { get; set; } = "";
    public string File { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class NovelMeta
{
    public string Title { get; set; } = "";
    public string Key { get; set; } = "";       // host|slug — stable identity for auto-merge
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";  // "" = General; "Completed"; or a custom name
    public string Source { get; set; } = "";
    public string Cover { get; set; } = "";   // local cover filename if downloaded
    public string CoverUrl { get; set; } = "";
    public string Created { get; set; } = "";
    public string Updated { get; set; } = "";
    public List<ChapterMeta> Chapters { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder { get; set; } = "";   // runtime only
}

public static class Library
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NovelGrabber");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Novel";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim().Trim('.');
        if (name.Length > 120) name = name.Substring(0, 120).Trim();
        return name.Length == 0 ? "Novel" : name;
    }

    /// <summary>Strip "Chapter N ..." / site separators to get a stable novel title.</summary>
    public static string CleanNovelTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = raw;
        t = Regex.Replace(t, @"\s*[-|–:]\s*(read online|read free|free online|novel|light novel|novellunar|novelbin|novelfire|ranobes|novelhall).*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*[-|–]\s*chapter\s*\d.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*chapter\s*\d.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+novel\s*$", "", RegexOptions.IgnoreCase);
        return t.Trim().Trim('-', '|', '–', ':').Trim();
    }

    /// <summary>Stable "host|slug" identity so 50-chapter batches of the same novel
    /// land in (and stitch onto) the same folder, regardless of which chapter URL was pasted.</summary>
    public static string NovelKey(Uri u)
    {
        string host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        string path = u.AbsolutePath.ToLowerInvariant();
        path = Regex.Replace(path, @"/chapter[-/_]?\d+.*$", "");   // /chapter/12, /chapter-12
        path = Regex.Replace(path, @"-chapter-\d+.*$", "");        // succ-chapter-12-title
        path = Regex.Replace(path, @"/\d+\.html?$", "");           // /19704189.html
        path = Regex.Replace(path, @"/\d+/?$", "");                // trailing /123
        path = path.Trim('/');
        var skip = new HashSet<string> { "novel", "book", "b", "series", "read", "chapter", "chapters" };
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string slug = segs.FirstOrDefault(s => !skip.Contains(s)) ?? (path.Length > 0 ? path : host);
        return host + "|" + slug;
    }

    /// <summary>Find an existing novel folder whose key matches (backfilling from Source for old entries).</summary>
    public static string? FindByKey(string key)
    {
        foreach (var m in History())
        {
            string k = !string.IsNullOrEmpty(m.Key) ? m.Key
                     : (Uri.TryCreate(m.Source, UriKind.Absolute, out var u) ? NovelKey(u) : "");
            if (k == key) return m.Folder;
        }
        return null;
    }

    public static int ParseChapterNumber(string? title, string? url)
    {
        // Volume-aware: "Volume 2 Chapter 1" must NOT collide with "Volume 1 Chapter 1".
        string both = (title ?? "") + " " + (url ?? "");
        int vol = 0;
        var mv = Regex.Match(both, @"\bvol(?:ume)?[\s\-_.]*?(\d{1,4})", RegexOptions.IgnoreCase);
        if (mv.Success) int.TryParse(mv.Groups[1].Value, out vol);

        int ch = -1;
        foreach (var s in new[] { title ?? "", url ?? "" })
        {
            var m = Regex.Match(s, @"chapter[\s\-_/]*?(\d{1,6})", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out ch)) break;
            ch = -1;
        }
        if (ch < 0)
        {
            var m2 = Regex.Match(url ?? "", @"(\d{1,6})(?!.*\d)");   // trailing number, e.g. /123.html
            if (m2.Success) int.TryParse(m2.Groups[1].Value, out ch); else ch = -1;
        }
        if (ch < 0) return -1;
        return vol > 0 ? vol * 100000 + ch : ch;   // composite key keeps volumes ordered & distinct
    }

    public static string MetaPath(string folder) => Path.Combine(folder, "meta.json");

    public static NovelMeta LoadOrCreate(string folder, string title, string source)
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "chapters"));
        var path = MetaPath(folder);
        NovelMeta meta;
        if (File.Exists(path))
        {
            try { meta = JsonSerializer.Deserialize<NovelMeta>(File.ReadAllText(path), JsonOpts) ?? new NovelMeta(); }
            catch { meta = new NovelMeta(); }
        }
        else
        {
            meta = new NovelMeta { Title = title, Source = source, Created = DateTime.Now.ToString("u") };
        }
        if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = title;
        if (string.IsNullOrWhiteSpace(meta.Source)) meta.Source = source;
        meta.Folder = folder;
        return meta;
    }

    /// <summary>bumpUpdated: false for metadata-only edits (category moves) so the novel
    /// doesn't jump to the top of the recency sort.</summary>
    public static void Save(NovelMeta meta, bool bumpUpdated = true)
    {
        if (bumpUpdated) meta.Updated = DateTime.Now.ToString("u");
        File.WriteAllText(MetaPath(meta.Folder), JsonSerializer.Serialize(meta, JsonOpts));
    }

    // ---------------- reading progress (drives the list view's progress column) ----------------

    private static readonly object ProgGate = new();
    private static Dictionary<string, double[]>? _prog;   // folder NAME -> [chapterIdx, scrollFrac]
    private static string _progRoot = "";
    private static string ProgPath => Path.Combine(Root, "progress.json");

    private static Dictionary<string, double[]> Prog()
    {
        if (_prog == null || _progRoot != Root)
        {
            _progRoot = Root;
            try { _prog = File.Exists(ProgPath) ? JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(ProgPath)) : null; }
            catch { _prog = null; }
            _prog ??= new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        }
        return _prog;
    }

    public static void SaveProgress(string folderName, int idx, double frac)
    {
        lock (ProgGate)
        {
            Prog()[folderName] = new[] { idx, Math.Clamp(frac, 0, 1) };
            try { File.WriteAllText(ProgPath, JsonSerializer.Serialize(_prog)); } catch { }
        }
    }

    public static int ProgressPct(NovelMeta m)
    {
        lock (ProgGate)
        {
            if (m.Chapters.Count == 0 || !Prog().TryGetValue(Path.GetFileName(m.Folder), out var v)) return 0;
            return (int)Math.Clamp(Math.Round((v[0] + v[1]) / m.Chapters.Count * 100.0), 0, 100);
        }
    }

    // ---------------- categories ----------------

    public const string Completed = "Completed";
    private static string CatPath => Path.Combine(Root, "categories.json");

    /// <summary>General + Completed + user categories (from categories.json and any assignments).</summary>
    public static List<string> Categories()
    {
        var list = new List<string> { "General", Completed };
        try
        {
            if (File.Exists(CatPath))
                foreach (var c in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(CatPath)) ?? new())
                    if (!string.IsNullOrWhiteSpace(c) && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
        }
        catch { }
        foreach (var m in History())
            if (!string.IsNullOrEmpty(m.Category) && !list.Contains(m.Category, StringComparer.OrdinalIgnoreCase))
                list.Add(m.Category);
        return list;
    }

    public static void AddCategory(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Equals("General", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(Completed, StringComparison.OrdinalIgnoreCase)) return;
        List<string> custom = new();
        try { if (File.Exists(CatPath)) custom = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(CatPath)) ?? new(); } catch { }
        if (custom.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        custom.Add(name);
        try { File.WriteAllText(CatPath, JsonSerializer.Serialize(custom, JsonOpts)); } catch { }
    }

    public static void SetCategory(string folder, string cat)
    {
        if (!File.Exists(MetaPath(folder))) return;
        var m = LoadOrCreate(folder, "", "");
        m.Category = cat.Equals("General", StringComparison.OrdinalIgnoreCase) ? "" : cat;
        Save(m, bumpUpdated: false);
    }

    /// <summary>Removes all custom categories (leaves the implicit General/Completed defaults).
    /// Used by Delete-all so auto-sort's now-empty series don't linger.</summary>
    public static void ClearCategories()
    {
        try { if (File.Exists(CatPath)) File.Delete(CatPath); } catch { }
    }

    // ---------------- auto sort (multi-volume LN series → one category) ----------------

    /// <summary>Groups General novels whose titles are ≥50% similar and files each group under
    /// a category named after the common part of the titles. Returns (groups, novels moved).</summary>
    public static (int groups, int moved) AutoSort()
    {
        var pool = History().Where(m => string.IsNullOrEmpty(m.Category)).ToList();
        var used = new HashSet<int>();
        int groups = 0, moved = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            if (used.Contains(i)) continue;
            var group = new List<NovelMeta> { pool[i] };
            for (int j = i + 1; j < pool.Count; j++)
                if (!used.Contains(j) && TitleSim(pool[i].Title, pool[j].Title) >= 0.5)
                { group.Add(pool[j]); used.Add(j); }
            if (group.Count < 2) continue;
            used.Add(i);
            string name = CommonName(group.Select(g => g.Title).ToList());
            AddCategory(name);
            foreach (var m in group) { m.Category = name; Save(m, bumpUpdated: false); moved++; }
            groups++;
        }
        return (groups, moved);
    }

    public static double TitleSim(string a, string b)
    {
        a = NormTitle(a); b = NormTitle(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        return 1.0 - (double)Lev(a, b) / Math.Max(a.Length, b.Length);
    }

    private static string NormTitle(string t) =>
        Regex.Replace(Regex.Replace(t.ToLowerInvariant(), @"[^\w\s]", " "), @"\s+", " ").Trim();

    private static int Lev(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>The common start of the group's titles, with dangling volume/number words trimmed.</summary>
    private static string CommonName(List<string> titles)
    {
        string p = titles[0];
        foreach (var t in titles.Skip(1))
        {
            int n = 0;
            while (n < p.Length && n < t.Length && char.ToLowerInvariant(p[n]) == char.ToLowerInvariant(t[n])) n++;
            p = p[..n];
        }
        p = Regex.Replace(p, @"\s*\(?\s*(light\s+novel|vol(ume)?|part|book|v)\.?\s*\d*\s*\)?\s*$", "", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, @"\s*\d+$", "");                 // partial volume number, e.g. "… [W] 0"
        p = Regex.Replace(p, @"[\s\-–—:,(\[]+$", "").Trim();
        return p.Length >= 3 ? p : "Series";
    }

    private static string NormUrl(string? u) => (u ?? "").TrimEnd('/').ToLowerInvariant();

    public static bool HasChapter(NovelMeta meta, int num, string url)
    {
        // URL is the reliable identity; number is a fallback.
        string nu = NormUrl(url);
        if (nu.Length > 0 && meta.Chapters.Any(c => NormUrl(c.Url) == nu)) return true;
        if (num > 0 && meta.Chapters.Any(c => c.Num == num)) return true;
        return false;
    }

    /// <summary>Writes one chapter as XHTML into the novel folder and registers it in meta.</summary>
    public static ChapterMeta AddChapter(NovelMeta meta, int num, string title, string url, string text)
    {
        int seq = meta.Chapters.Count == 0 ? 1 : meta.Chapters.Max(c => c.Seq) + 1;
        string key = num > 0 ? num.ToString("D5") : "s" + seq.ToString("D5");
        string file = "chapters/c" + key + ".xhtml";
        string full = Path.Combine(meta.Folder, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, ChapterXhtml(title, text), new UTF8Encoding(false));

        var cm = new ChapterMeta { Num = num, Seq = seq, Title = title, File = file, Url = url };
        meta.Chapters.Add(cm);
        return cm;
    }

    public static IEnumerable<ChapterMeta> Ordered(NovelMeta meta) =>
        meta.Chapters.OrderBy(c => c.Num > 0 ? c.Num : int.MaxValue).ThenBy(c => c.Seq);

    // meta.json of a long novel is big (every chapter listed); with 60+ books re-parsing them all
    // on every Library visit is the main lag. Cache by write-stamp — only changed files re-parse.
    private static readonly Dictionary<string, (DateTime stamp, NovelMeta meta)> MetaCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<NovelMeta> History()
    {
        var list = new List<NovelMeta>();
        if (!Directory.Exists(Root)) return list;
        lock (MetaCache)
        {
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var p = MetaPath(dir);
                if (!File.Exists(p)) continue;
                var stamp = File.GetLastWriteTimeUtc(p);
                if (!MetaCache.TryGetValue(dir, out var e) || e.stamp != stamp)
                {
                    try
                    {
                        var m = JsonSerializer.Deserialize<NovelMeta>(File.ReadAllText(p), JsonOpts);
                        if (m == null) continue;
                        m.Folder = dir;
                        e = (stamp, m);
                        MetaCache[dir] = e;
                    }
                    catch { continue; }
                }
                list.Add(e.meta);
            }
        }
        return list.OrderByDescending(m => m.Updated).ToList();
    }

    public static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Paragraphs(string text)
    {
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("@@IMG:") && t.EndsWith("@@") && t.Length > 8)   // imported illustration marker
            {
                var name = t[6..^2].Replace("\"", "").Replace("<", "").Replace(">", "");
                sb.Append("<img src=\"../images/").Append(name).Append("\"/>\n");
                continue;
            }
            sb.Append("<p>").Append(Escape(t)).Append("</p>\n");
        }
        return sb.ToString();
    }

    public static string ChapterXhtml(string title, string text) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<!DOCTYPE html>
<html xmlns=""http://www.w3.org/1999/xhtml"">
<head><title>{Escape(title)}</title><link rel=""stylesheet"" type=""text/css"" href=""../style.css""/></head>
<body><h2 class=""ch-title"">{Escape(title)}</h2>
{Paragraphs(text)}</body></html>";
}
