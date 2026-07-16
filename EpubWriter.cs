using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace NovelGrabber;

public static class EpubWriter
{
    private static void AddText(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public static string Build(NovelMeta meta, string? outPath = null)
    {
        var ordered = Library.Ordered(meta)
            .Where(c => File.Exists(Path.Combine(meta.Folder, c.File.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        if (ordered.Count == 0) throw new InvalidOperationException("No chapter files to export.");

        outPath ??= Path.Combine(meta.Folder, Library.Sanitize(meta.Title) + ".epub");

        bool hasCover = !string.IsNullOrWhiteSpace(meta.Cover) &&
                        File.Exists(Path.Combine(meta.Folder, meta.Cover));
        string coverExt = hasCover ? Path.GetExtension(meta.Cover).TrimStart('.').ToLowerInvariant() : "jpg";
        if (coverExt is "jpeg") coverExt = "jpg";
        string coverMime = coverExt switch { "png" => "image/png", "webp" => "image/webp", _ => "image/jpeg" };

        using (var fs = new FileStream(outPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // mimetype: first entry, stored
            var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mt.Open(), new UTF8Encoding(false)))
                w.Write("application/epub+zip");

            AddText(zip, "META-INF/container.xml",
@"<?xml version=""1.0""?>
<container version=""1.0"" xmlns=""urn:oasis:names:tc:opendocument:xmlns:container"">
  <rootfiles><rootfile full-path=""OEBPS/content.opf"" media-type=""application/oebps-package+xml""/></rootfiles>
</container>");

            AddText(zip, "OEBPS/style.css",
@"body{font-family:serif;line-height:1.6;margin:5%;}
h1,h2{font-family:sans-serif;text-align:center;}
.ch-title{margin:1em 0 1.5em;font-size:1.2em;}
p{margin:0 0 1em;text-indent:1.4em;}
img{max-width:100%;height:auto;display:block;margin:0.8em auto;}
.cover{margin:0;padding:0;text-align:center;}
.cover img{max-width:100%;height:auto;}");

            if (hasCover)
            {
                var ce = zip.CreateEntry("OEBPS/cover." + coverExt, CompressionLevel.Optimal);
                using (var s = ce.Open())
                using (var src = File.OpenRead(Path.Combine(meta.Folder, meta.Cover)))
                    src.CopyTo(s);

                AddText(zip, "OEBPS/cover.xhtml",
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<!DOCTYPE html>
<html xmlns=""http://www.w3.org/1999/xhtml""><head><title>Cover</title>
<link rel=""stylesheet"" type=""text/css"" href=""style.css""/></head>
<body><div class=""cover""><img src=""cover.{coverExt}"" alt=""cover""/></div></body></html>");
            }

            // chapter files (kept under OEBPS/chapters/, referencing ../style.css)
            foreach (var c in ordered)
            {
                var srcFile = Path.Combine(meta.Folder, c.File.Replace('/', Path.DirectorySeparatorChar));
                var ce = zip.CreateEntry("OEBPS/" + c.File, CompressionLevel.Optimal);
                using var s = ce.Open();
                using var src = File.OpenRead(srcFile);
                src.CopyTo(s);
            }

            // imported LN illustrations — chapters reference ../images/<name>, which from
            // OEBPS/chapters/ resolves to OEBPS/images/<name>
            var imgDir = Path.Combine(meta.Folder, "images");
            var imgFiles = Directory.Exists(imgDir) ? Directory.GetFiles(imgDir) : Array.Empty<string>();
            foreach (var f in imgFiles)
            {
                var ie = zip.CreateEntry("OEBPS/images/" + Path.GetFileName(f), CompressionLevel.Optimal);
                using var s = ie.Open();
                using var src = File.OpenRead(f);
                src.CopyTo(s);
            }

            string uid = "urn:uuid:novelgrabber-" + Library.Sanitize(meta.Title).Replace(' ', '-') + "-" +
                         DateTimeOffset.Now.ToUnixTimeSeconds();

            var manifest = new StringBuilder();
            var spine = new StringBuilder();
            manifest.AppendLine(@"    <item id=""ncx"" href=""toc.ncx"" media-type=""application/x-dtbncx+xml""/>");
            manifest.AppendLine(@"    <item id=""css"" href=""style.css"" media-type=""text/css""/>");
            string coverMeta = "";
            if (hasCover)
            {
                coverMeta = @"<meta name=""cover"" content=""cover-img""/>";
                manifest.AppendLine($@"    <item id=""cover-img"" href=""cover.{coverExt}"" media-type=""{coverMime}""/>");
                manifest.AppendLine(@"    <item id=""cover-page"" href=""cover.xhtml"" media-type=""application/xhtml+xml""/>");
                spine.AppendLine(@"    <itemref idref=""cover-page""/>");
            }

            int im = 0;
            foreach (var f in imgFiles)
            {
                im++;
                string ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
                string mime = ext switch { "png" => "image/png", "webp" => "image/webp", "gif" => "image/gif", "svg" => "image/svg+xml", "bmp" => "image/bmp", _ => "image/jpeg" };
                manifest.AppendLine($@"    <item id=""img{im}"" href=""images/{Path.GetFileName(f)}"" media-type=""{mime}""/>");
            }

            int i = 0;
            var nav = new StringBuilder();
            foreach (var c in ordered)
            {
                i++;
                string id = "ch" + i.ToString("D5");
                manifest.AppendLine($@"    <item id=""{id}"" href=""{c.File}"" media-type=""application/xhtml+xml""/>");
                spine.AppendLine($@"    <itemref idref=""{id}""/>");
                string label = string.IsNullOrWhiteSpace(c.Title) ? (c.Num > 0 ? "Chapter " + c.Num : "Chapter " + i) : c.Title;
                nav.AppendLine(
$@"    <navPoint id=""np{i}"" playOrder=""{i}"">
      <navLabel><text>{Library.Escape(label)}</text></navLabel>
      <content src=""{c.File}""/>
    </navPoint>");
            }

            AddText(zip, "OEBPS/content.opf",
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://www.idpf.org/2007/opf"" unique-identifier=""bookid"" version=""2.0"">
  <metadata xmlns:dc=""http://purl.org/dc/elements/1.1/"" xmlns:opf=""http://www.idpf.org/2007/opf"">
    <dc:title>{Library.Escape(meta.Title)}</dc:title>
    <dc:creator opf:role=""aut"">{Library.Escape(string.IsNullOrWhiteSpace(meta.Author) ? "Unknown" : meta.Author)}</dc:creator>
    <dc:language>en</dc:language>
    <dc:identifier id=""bookid"">{uid}</dc:identifier>
    <dc:source>{Library.Escape(meta.Source)}</dc:source>
    {coverMeta}
  </metadata>
  <manifest>
{manifest}  </manifest>
  <spine toc=""ncx"">
{spine}  </spine>
  {(hasCover ? @"<guide><reference type=""cover"" title=""Cover"" href=""cover.xhtml""/></guide>" : "")}
</package>");

            AddText(zip, "OEBPS/toc.ncx",
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<!DOCTYPE ncx PUBLIC ""-//NISO//DTD ncx 2005-1//EN"" ""http://www.daisy.org/z3986/2005/ncx-2005-1.dtd"">
<ncx xmlns=""http://www.daisy.org/z3986/2005/ncx/"" version=""2005-1"">
  <head>
    <meta name=""dtb:uid"" content=""{uid}""/>
    <meta name=""dtb:depth"" content=""1""/>
    <meta name=""dtb:totalPageCount"" content=""0""/>
    <meta name=""dtb:maxPageNumber"" content=""0""/>
  </head>
  <docTitle><text>{Library.Escape(meta.Title)}</text></docTitle>
  <navMap>
{nav}  </navMap>
</ncx>");
        }

        return outPath;
    }
}
