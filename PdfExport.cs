using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NovelGrabber;

/// <summary>
/// Builds a single print-ready HTML document (title page + one section per chapter).
/// The actual PDF is produced locally by WebView2's PrintToPdfAsync in MainForm —
/// no external converter or service involved.
/// </summary>
public static class PdfExport
{
    private static readonly string Css = @"
<style>
  @page { margin: 18mm 16mm; }
  body { font-family: Georgia, 'Times New Roman', serif; font-size: 12pt; line-height: 1.55; color:#111; }
  .title-page { text-align:center; padding-top: 38vh; page-break-after: always; }
  .title-page h1 { font-size: 26pt; margin: 0 0 12px; }
  .title-page .by { font-size: 13pt; color:#444; }
  .chapter { page-break-before: always; }
  .chapter h2 { font-family: Arial, sans-serif; font-size: 16pt; margin: 0 0 16px; }
  p { margin: 0 0 0.7em; text-indent: 1.4em; text-align: justify; }
  img { max-width: 100%; height: auto; display: block; margin: 10px auto; }
</style>";

    private static string BodyInner(string xhtml)
    {
        var m = Regex.Match(xhtml, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : xhtml;
    }

    private static string Doc(string title, string author, IEnumerable<string> chapterBodies)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>")
          .Append(Library.Escape(title)).Append("</title>").Append(Css).Append("</head><body>");
        sb.Append("<div class=\"title-page\"><h1>").Append(Library.Escape(title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(author))
            sb.Append("<div class=\"by\">").Append(Library.Escape(author)).Append("</div>");
        sb.Append("</div>");
        foreach (var body in chapterBodies)
            sb.Append("<section class=\"chapter\">").Append(body).Append("</section>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Build print HTML from a novel already in the library (illustrations inlined).</summary>
    public static (string title, string html) FromNovel(NovelMeta meta)
    {
        var bodies = new List<string>();
        foreach (var c in Library.Ordered(meta))
        {
            var path = Path.Combine(meta.Folder, c.File.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) bodies.Add(EpubRead.InlineImages(BodyInner(File.ReadAllText(path)), null, meta.Folder));
        }
        if (bodies.Count == 0) throw new InvalidOperationException("No chapters to export.");
        return (meta.Title, Doc(meta.Title, meta.Author, bodies));
    }
}
