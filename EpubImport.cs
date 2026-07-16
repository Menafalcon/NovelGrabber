using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace NovelGrabber;

/// <summary>Converts external .epub files into first-class library novels
/// (folder + meta.json + chapter xhtml + cover) so Read/TTS/exports all work on them.</summary>
public static class EpubImport
{
    public enum Result { Imported, Skipped }

    public static (Result r, string title) ImportOne(string epubPath)
    {
        var book = EpubRead.Load(epubPath);                      // throws if not a valid epub
        string folder = Path.Combine(Library.Root, Library.Sanitize(book.Title));
        if (File.Exists(Library.MetaPath(folder))) return (Result.Skipped, book.Title);

        var meta = Library.LoadOrCreate(folder, book.Title, "");
        try
        {
            meta.Author = book.Author;
            int i = 0;
            foreach (var (title, body) in book.Chapters)
                Library.AddChapter(meta, ++i, title, "", HtmlToText(body));   // Num = spine order
            if (book.Cover is { Length: > 0 })
            {
                string cn = "cover." + book.CoverExt;
                File.WriteAllBytes(Path.Combine(folder, cn), book.Cover);
                meta.Cover = cn;
            }
            if (book.Images.Count > 0)                        // LN illustrations live next to the chapters
            {
                string imgDir = Path.Combine(folder, "images");
                Directory.CreateDirectory(imgDir);
                foreach (var (name, bytes) in book.Images)   // keys are made filename-safe by EpubRead
                    File.WriteAllBytes(Path.Combine(imgDir, name), bytes);
            }
            Library.Save(meta);
        }
        catch
        {
            // half-written novel would be invisible (no meta.json) but leave junk — clean it up
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
        return (Result.Imported, book.Title);
    }

    /// <summary>Epub chapter HTML → plain text with paragraph breaks (the library's chapter format).
    /// Illustrations survive as @@IMG:name@@ marker lines, which Library.Paragraphs turns back into img tags.</summary>
    public static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"\s+", " ");                              // source line breaks ≠ paragraphs
        html = Regex.Replace(html, @"<img\b[^>]*?\bsrc\s*=\s*""ngimg://([^""]+)""[^>]*/?>", "\n@@IMG:$1@@\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</(p|div|h[1-6]|li|blockquote|tr|section|article)>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t]+\n", "\n");
        return html.Trim();
    }
}

/// <summary>Tiny dark modal shown while a folder of epubs imports; worker reports per-file progress.</summary>
public sealed class ImportProgressForm : Form
{
    private readonly ProgressThin _bar = new() { Dock = DockStyle.Top, Height = 6 };
    private readonly Label _lbl = new() { Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Text, Font = Theme.FontBase, Text = "Starting…" };
    private readonly FlatButton _cancel = new() { Text = "Cancel", Width = 90, Height = 32, Style = FlatButton.Kind.Ghost };
    public event Action? Cancelled;

    public ImportProgressForm(int total)
    {
        Text = "Importing EPUBs";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg; ForeColor = Theme.Text;
        ClientSize = new Size(420, 130);
        Padding = new Padding(20, 18, 20, 14);
        _bar.Maximum = total;

        var btnRow = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.Bg };
        _cancel.Dock = DockStyle.Right;
        _cancel.Click += (_, _) => { Cancelled?.Invoke(); _cancel.Enabled = false; _cancel.Text = "Stopping…"; };
        btnRow.Controls.Add(_cancel);

        Controls.Add(btnRow);
        Controls.Add(_bar);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        Controls.Add(_lbl);
        Shown += (_, _) => Theme.ApplyWindowChrome(this);
        FormClosing += (_, _) => Cancelled?.Invoke();   // closing the window also stops the worker
    }

    public void Report(int done, string name)
    {
        try { BeginInvoke(() => { _bar.Value = done; _lbl.Text = $"{done} / {_bar.Maximum}  —  {name}"; }); }
        catch { /* handle not created yet — next report lands */ }
    }
}
