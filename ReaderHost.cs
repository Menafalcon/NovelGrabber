using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovelGrabber;

/// <summary>
/// Hosts the HTML reader (reader.html) in its own WebView2 and bridges it to the app:
/// serves chapters on demand, saves progress, and proxies Google Cloud TTS requests.
/// </summary>
public sealed class ReaderHost
{
    public WebView2 View { get; } = new() { Dock = DockStyle.Fill };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private readonly Action<string> _log;
    private bool _ready;
    private string? _pendingBookJson;

    // current book: either files on disk (library novel) or in-memory bodies (external epub)
    private sealed record Chap(string Title, string? File, string? Body);
    private List<Chap> _chapters = new();
    private string _folder = "";
    private EpubBook? _book;   // holds extracted illustrations while an external epub is open

    private static string? _voicesCache;   // Google voices JSON (per app run)

    /// <summary>Fires with the novel folder when the user hits "The End" of a library novel.</summary>
    public event Action<string>? BookFinished;

    private string _theme = "dark";

    /// <summary>Pushes the app-wide theme into the reader page. Remembered so it re-applies once
    /// the page signals ready (a push before that is dropped).</summary>
    public void SetTheme(string name)
    {
        _theme = name;
        if (_ready) Post(JsonSerializer.Serialize(new { type = "theme", theme = name }));
    }

    /// <summary>Re-fetches the voice list (call after the Google TTS key changes in Settings).</summary>
    public void RefreshVoices() { _voicesCache = null; _ = SendVoicesAsync(); }

    public ReaderHost(Action<string> log) { _log = log; View.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x12, 0x17); }

    public bool Initialized { get; private set; }

    public async Task InitAsync(CoreWebView2Environment env)
    {
        if (Initialized) return;
        await View.EnsureCoreWebView2Async(env);
        var core = View.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping("grabber.app", AppContext.BaseDirectory,
            CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        core.Navigate("https://grabber.app/reader.html");
        Initialized = true;
    }

    // ---------------- opening books ----------------

    public void OpenNovel(NovelMeta meta)
    {
        _chapters = Library.Ordered(meta)
            .Select(c => new Chap(string.IsNullOrWhiteSpace(c.Title) ? (c.Num > 0 ? "Chapter " + (c.Num > 100000 ? c.Num % 100000 : c.Num) : "Chapter") : c.Title,
                                  c.File, null))
            .ToList();
        _folder = meta.Folder;
        _book = null;
        string cover = "";
        if (!string.IsNullOrWhiteSpace(meta.Cover))
        {
            var cp = Path.Combine(meta.Folder, meta.Cover);
            if (File.Exists(cp)) cover = DataUrl(File.ReadAllBytes(cp), Path.GetExtension(cp).TrimStart('.'));
        }
        SendBook(meta.Title, meta.Author, cover, "novel:" + Path.GetFileName(meta.Folder));
    }

    public void OpenEpub(string path)
    {
        var book = EpubRead.Load(path);
        _chapters = book.Chapters.Select(c => new Chap(c.Title, null, c.Body)).ToList();
        _folder = "";
        _book = book;
        string cover = book.Cover != null ? DataUrl(book.Cover, book.CoverExt) : "";
        SendBook(book.Title, book.Author, cover, "epub:" + Path.GetFileName(path));
    }

    private static string DataUrl(byte[] bytes, string ext) => EpubRead.DataUrl(bytes, ext);

    private void SendBook(string title, string author, string cover, string key)
    {
        string json = JsonSerializer.Serialize(new
        {
            type = "book",
            title,
            author,
            cover,
            key,
            chapters = _chapters.Select(c => c.Title).ToArray(),
            hasGoogleKey = AppSettings.Load().GoogleTtsKey.Length > 0
        });
        if (_ready) Post(json); else _pendingBookJson = json;
        _ = SendVoicesAsync();
    }

    private void Post(string json)
    {
        try { View.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception ex) { _log("Reader: " + ex.Message); }
    }

    // ---------------- messages from the page ----------------

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement; } catch { return; }
        string cmd = m.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        try
        {
            switch (cmd)
            {
                case "ready":
                    _ready = true;
                    Post(JsonSerializer.Serialize(new { type = "theme", theme = _theme }));   // apply theme once JS is listening
                    if (_pendingBookJson != null) { Post(_pendingBookJson); _pendingBookJson = null; }
                    break;

                case "finished":
                    if (_folder.Length > 0) BookFinished?.Invoke(_folder);
                    break;

                case "savepos":
                    if (_folder.Length > 0)
                        Library.SaveProgress(Path.GetFileName(_folder),
                            m.GetProperty("idx").GetInt32(), m.GetProperty("r").GetDouble());
                    break;

                case "get":
                {
                    int idx = m.GetProperty("idx").GetInt32();
                    if (idx < 0 || idx >= _chapters.Count) break;
                    var ch = _chapters[idx];
                    string html = ch.Body ?? LoadChapterFile(ch.File!);
                    html = EpubRead.InlineImages(html, _book, _folder);   // illustrations → data: URIs
                    Post(JsonSerializer.Serialize(new { type = "chapter", idx, title = ch.Title, html }));
                    break;
                }

                case "savekey":
                {
                    var s = AppSettings.Load();
                    s.GoogleTtsKey = (m.GetProperty("key").GetString() ?? "").Trim();
                    s.Save();
                    _voicesCache = null;
                    await SendVoicesAsync();
                    break;
                }

                case "voices":
                    await SendVoicesAsync();
                    break;

                case "gtts":
                {
                    string id = m.GetProperty("id").GetString() ?? "";
                    string text = m.GetProperty("text").GetString() ?? "";
                    string voice = m.GetProperty("voice").GetString() ?? "en-US-AriaNeural";
                    string engine = m.TryGetProperty("engine", out var en) ? (en.GetString() ?? "e") : "e";
                    double rate = m.TryGetProperty("rate", out var r) ? r.GetDouble() : 1.0;
                    try
                    {
                        string audio = engine == "g"
                            ? await GoogleSynthesize(text, voice, rate)
                            : Convert.ToBase64String(await EdgeTts.SynthesizeAsync(text, voice, rate));
                        Post(JsonSerializer.Serialize(new { type = "gtts", id, ok = true, audio }));
                    }
                    catch (Exception ex)
                    {
                        Post(JsonSerializer.Serialize(new { type = "gtts", id, ok = false, err = Short(ex.Message) }));
                    }
                    break;
                }
            }
        }
        catch (Exception ex) { _log("Reader message error: " + ex.Message); }
    }

    private string LoadChapterFile(string relFile)
    {
        var path = Path.Combine(_folder, relFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return "<p>(chapter file missing)</p>";
        string xhtml = File.ReadAllText(path);
        string body = EpubRead.BodyInner(xhtml);
        body = Regex.Replace(body, @"<h2 class=""ch-title"">.*?</h2>", "", RegexOptions.Singleline);   // reader shows its own heading
        return body;
    }

    // ---------------- Google Cloud TTS ----------------

    private static string Short(string s) => s.Length > 220 ? s[..220] + "…" : s;

    /// <summary>Sends the combined online voice list: free Edge neural voices (src "e")
    /// plus Google Cloud voices (src "g") when an API key is saved.</summary>
    private async Task SendVoicesAsync()
    {
        string err = "";
        if (_voicesCache == null)
        {
            var list = new List<object>();
            try
            {
                foreach (var v in await EdgeTts.VoicesAsync())
                    list.Add(new { name = v.Name, lang = v.Lang, gender = v.Gender, src = "e" });
            }
            catch (Exception ex) { err = "Natural voices unavailable: " + Short(ex.Message); }

            string key = AppSettings.Load().GoogleTtsKey;
            if (key.Length > 0)
            {
                try
                {
                    var resp = await Http.GetAsync("https://texttospeech.googleapis.com/v1/voices?key=" + Uri.EscapeDataString(key));
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) throw new Exception(ApiError(body, (int)resp.StatusCode));
                    foreach (var v in JsonDocument.Parse(body).RootElement.GetProperty("voices").EnumerateArray())
                    {
                        string name = v.GetProperty("name").GetString() ?? "";
                        string lang = v.GetProperty("languageCodes")[0].GetString() ?? "";
                        string gender = v.TryGetProperty("ssmlGender", out var g) ? (g.GetString() ?? "") : "";
                        list.Add(new { name, lang, gender, src = "g" });
                    }
                }
                catch (Exception ex) { err = (err.Length > 0 ? err + " · " : "") + "Google: " + Short(ex.Message); }
            }
            if (list.Count > 0) _voicesCache = JsonSerializer.Serialize(list);
        }

        if (_voicesCache != null)
        {
            string errJson = JsonSerializer.Serialize(err);
            Post("{\"type\":\"voices\",\"ok\":true,\"err\":" + errJson + ",\"list\":" + _voicesCache + "}");
        }
        else
            Post(JsonSerializer.Serialize(new { type = "voices", ok = false, err = err.Length > 0 ? err : "No online voices available." }));
    }

    private static async Task<string> GoogleSynthesize(string text, string voiceName, double rate)
    {
        string key = AppSettings.Load().GoogleTtsKey;
        if (key.Length == 0) throw new Exception("No Google API key saved.");
        string lang = string.Join("-", voiceName.Split('-').Take(2));   // en-US-Neural2-J -> en-US
        var payload = JsonSerializer.Serialize(new
        {
            input = new { text },
            voice = new { languageCode = lang, name = voiceName },
            audioConfig = new { audioEncoding = "MP3", speakingRate = Math.Clamp(rate, 0.25, 4.0) }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync("https://texttospeech.googleapis.com/v1/text:synthesize?key=" + Uri.EscapeDataString(key), content);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(ApiError(body, (int)resp.StatusCode));
        return JsonDocument.Parse(body).RootElement.GetProperty("audioContent").GetString() ?? "";
    }

    private static string ApiError(string body, int status)
    {
        try
        {
            var msg = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString();
            if (!string.IsNullOrWhiteSpace(msg)) return msg!;
        }
        catch { }
        return "Google TTS HTTP " + status;
    }
}
