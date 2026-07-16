using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NovelGrabber;

public sealed class EpubBook
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public byte[]? Cover { get; set; }
    public string CoverExt { get; set; } = "jpg";
    public List<(string Title, string Body)> Chapters { get; } = new();
    /// <summary>Illustrations pulled from the epub, keyed by flattened zip path.
    /// Chapter bodies reference them as ngimg://&lt;key&gt; until a serving layer inlines them.</summary>
    public Dictionary<string, byte[]> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Parses an external .epub into reader-ready chapters (spine order, NCX titles).</summary>
public static class EpubRead
{
    public static EpubBook Load(string epubPath)
    {
        using var zip = ZipFile.OpenRead(epubPath);

        string ReadEntry(string name)
        {
            var e = zip.GetEntry(name) ?? zip.GetEntry(name.Replace('\\', '/'));
            if (e == null) return "";
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            return r.ReadToEnd();
        }

        string container = ReadEntry("META-INF/container.xml");
        var mRoot = Regex.Match(container, @"full-path=""([^""]+)""", RegexOptions.IgnoreCase);
        string opfPath = mRoot.Success ? mRoot.Groups[1].Value
                       : zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.FullName ?? "";
        if (opfPath == "") throw new InvalidOperationException("Not a valid EPUB (no OPF found).");
        string opf = ReadEntry(opfPath);
        string baseDir = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";
        string Rel(string href) => (baseDir == "" ? "" : baseDir + "/") + Uri.UnescapeDataString(href);

        var book = new EpubBook
        {
            Title = Meta(opf, "dc:title") is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(epubPath),
            Author = Meta(opf, "dc:creator")
        };

        // manifest id -> (href, mediaType)
        var manifest = new Dictionary<string, (string href, string type)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match it in Regex.Matches(opf, @"<item\b[^>]*?/?>", RegexOptions.IgnoreCase))
        {
            string id = Attr(it.Value, "id"), href = Attr(it.Value, "href"), mt = Attr(it.Value, "media-type");
            if (id != "" && href != "") manifest[id] = (href, mt);
        }

        // cover: <meta name="cover" content="id"> or an item with id/properties containing cover
        string coverHref = "";
        var mc = Regex.Match(opf, @"<meta[^>]*name=""cover""[^>]*content=""([^""]+)""", RegexOptions.IgnoreCase);
        if (mc.Success && manifest.TryGetValue(mc.Groups[1].Value, out var cv)) coverHref = cv.href;
        if (coverHref == "")
            coverHref = manifest.Values.FirstOrDefault(v => v.type.StartsWith("image") &&
                v.href.Contains("cover", StringComparison.OrdinalIgnoreCase)).href ?? "";
        if (coverHref != "")
        {
            var ce = zip.GetEntry(Rel(coverHref));
            if (ce != null)
            {
                using var ms = new MemoryStream();
                using (var s = ce.Open()) s.CopyTo(ms);
                book.Cover = ms.ToArray();
                book.CoverExt = Path.GetExtension(coverHref).TrimStart('.').ToLowerInvariant() switch
                { "png" => "png", "webp" => "webp", "gif" => "gif", _ => "jpg" };
            }
        }

        // NCX titles: src (sans fragment) -> label
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ncxHref = manifest.Values.FirstOrDefault(v => v.type.Contains("dtbncx") || v.href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase)).href;
        if (!string.IsNullOrEmpty(ncxHref))
        {
            string ncx = ReadEntry(Rel(ncxHref));
            foreach (Match np in Regex.Matches(ncx, @"<navPoint\b.*?</navPoint>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var lb = Regex.Match(np.Value, @"<text>\s*([^<]*?)\s*</text>", RegexOptions.IgnoreCase);
                var sr = Regex.Match(np.Value, @"<content[^>]*src=""([^""#]+)", RegexOptions.IgnoreCase);
                if (lb.Success && sr.Success)
                {
                    var key = Uri.UnescapeDataString(sr.Groups[1].Value);
                    if (!titles.ContainsKey(key)) titles[key] = System.Net.WebUtility.HtmlDecode(lb.Groups[1].Value);
                }
            }
        }

        // every image file in the zip, by normalized full path (for resolving chapter <img> refs)
        var zipImages = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
            if (Regex.IsMatch(e.FullName, @"\.(jpe?g|png|gif|webp|bmp|svg)$", RegexOptions.IgnoreCase))
                zipImages[e.FullName.Replace('\\', '/')] = e;

        int n = 0;
        foreach (Match sp in Regex.Matches(opf, @"<itemref\b[^>]*?/?>", RegexOptions.IgnoreCase))
        {
            string idref = Attr(sp.Value, "idref");
            if (idref == "" || !manifest.TryGetValue(idref, out var item)) continue;
            if (!(item.type.Contains("html") || item.href.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                  item.href.EndsWith(".html", StringComparison.OrdinalIgnoreCase))) continue;
            string chapPath = Rel(item.href);
            string xhtml = ReadEntry(chapPath);
            if (xhtml == "") continue;
            n++;
            string chapDir = chapPath.Contains('/') ? chapPath[..chapPath.LastIndexOf('/')] : "";
            string raw = BodyInner(xhtml);
            raw = SvgToImg(raw);
            raw = CollectImages(raw, chapDir, zipImages, book);
            string body = Sanitize(raw);
            bool hasImg = body.Contains("<img", StringComparison.OrdinalIgnoreCase);
            if (!hasImg && Regex.Replace(body, "<[^>]+>", "").Trim().Length < 2) continue;   // skip empty pages (but keep illustration-only ones)
            string title = titles.TryGetValue(item.href, out var tt) && tt.Length > 0 ? tt
                         : Regex.Match(xhtml, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase) is { Success: true } tm
                           ? System.Net.WebUtility.HtmlDecode(tm.Groups[1].Value).Trim()
                           : "Chapter " + n;
            book.Chapters.Add((title, body));
        }
        if (book.Chapters.Count == 0) throw new InvalidOperationException("EPUB had no readable chapters.");
        return book;
    }

    public static string BodyInner(string xhtml)
    {
        var m = Regex.Match(xhtml, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : xhtml;
    }

    /// <summary>Strips scripts/styles/event handlers so arbitrary epub HTML is safe & clean to render.
    /// Images are kept — CollectImages has already rewritten their srcs to ngimg:// keys.</summary>
    public static string Sanitize(string html)
    {
        html = Regex.Replace(html, @"<script\b.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style\b.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(iframe|object|embed|video|audio|svg)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|\S+)", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\s(style|class|id)\s*=\s*(""[^""]*""|'[^']*')", "", RegexOptions.IgnoreCase);
        return html;
    }

    /// <summary>Full-page LN illustrations are often wrapped as &lt;svg&gt;&lt;image xlink:href…&gt;&lt;/svg&gt;,
    /// which Sanitize would delete — unwrap them into plain img tags first.</summary>
    private static string SvgToImg(string html) =>
        Regex.Replace(html, @"<svg\b[^>]*>.*?<image\b[^>]*?(?:xlink:href|href)\s*=\s*""([^""]+)""[^>]*/?>.*?</svg>",
            m => $"<img src=\"{m.Groups[1].Value}\"/>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Resolves each img src against the chapter's directory, loads the bytes out of the zip
    /// into book.Images, and rewrites the src to a stable ngimg:// key.</summary>
    private static string CollectImages(string html, string chapDir, Dictionary<string, ZipArchiveEntry> zipImages, EpubBook book)
    {
        return Regex.Replace(html, @"<img\b[^>]*?\bsrc\s*=\s*""([^""]+)""[^>]*/?>", m =>
        {
            string src = m.Groups[1].Value.Trim();
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return m.Value;

            string norm = NormPath(src.StartsWith('/') ? "" : chapDir, src.TrimStart('/'));
            if (!zipImages.TryGetValue(norm, out var entry))
            {
                // some epubs use odd relative bases — fall back to a filename match
                string name = norm.Contains('/') ? norm[(norm.LastIndexOf('/') + 1)..] : norm;
                var k = zipImages.Keys.FirstOrDefault(x =>
                    x.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) ||
                    x.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (k == null) return "";                     // image missing from the zip
                entry = zipImages[k]; norm = k;
            }
            string key = norm.Replace('/', '_');
            foreach (var bad in Path.GetInvalidFileNameChars()) key = key.Replace(bad, '_');   // key doubles as a filename on import
            if (!book.Images.ContainsKey(key))
            {
                using var ms = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(ms);
                book.Images[key] = ms.ToArray();
            }
            return $"<img src=\"ngimg://{key}\"/>";
        }, RegexOptions.IgnoreCase);
    }

    private static string NormPath(string dir, string src)
    {
        src = Uri.UnescapeDataString(src.Split('#')[0].Split('?')[0]).Replace('\\', '/');
        var parts = new List<string>(dir.Length > 0 ? dir.Split('/') : Array.Empty<string>());
        foreach (var p in src.Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join("/", parts);
    }

    /// <summary>Replaces ngimg:// refs (from an open EpubBook) and ../images/ refs (from an imported
    /// library novel folder) with data: URIs so any WebView can render them with no file access.</summary>
    public static string InlineImages(string html, EpubBook? book, string? novelFolder)
    {
        return Regex.Replace(html, @"<img\b[^>]*?\bsrc\s*=\s*""([^""]+)""[^>]*/?>", m =>
        {
            string src = m.Groups[1].Value;
            byte[]? bytes = null;
            if (src.StartsWith("ngimg://", StringComparison.OrdinalIgnoreCase))
                book?.Images.TryGetValue(src[8..], out bytes);
            else if (src.StartsWith("../images/", StringComparison.OrdinalIgnoreCase) ||
                     src.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(novelFolder))
                {
                    var p = Path.Combine(novelFolder, "images", Path.GetFileName(Uri.UnescapeDataString(src)));
                    if (File.Exists(p)) bytes = File.ReadAllBytes(p);
                }
            }
            else return m.Value;                              // data:/http srcs pass through
            if (bytes == null) return "";
            return $"<img src=\"{DataUrl(bytes, Path.GetExtension(src).TrimStart('.'))}\"/>";
        }, RegexOptions.IgnoreCase);
    }

    public static string DataUrl(byte[] bytes, string ext)
    {
        string mime = ext.ToLowerInvariant() switch
        {
            "png" => "image/png", "webp" => "image/webp", "gif" => "image/gif",
            "svg" => "image/svg+xml", "bmp" => "image/bmp", _ => "image/jpeg"
        };
        return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
    }

    private static string Meta(string opf, string tag)
    {
        var m = Regex.Match(opf, "<" + tag + @"[^>]*>([^<]*)</" + tag + ">", RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    private static string Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }
}
