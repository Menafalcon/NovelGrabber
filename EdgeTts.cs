using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovelGrabber;

/// <summary>
/// Free Microsoft Edge "Read Aloud" neural voices — the same natural voices Edge/Android use.
/// No API key needed. Uses the public read-aloud endpoint with the Sec-MS-GEC token scheme.
/// If Microsoft ever changes the scheme, errors surface in the reader and device/Google
/// voices keep working.
/// </summary>
public static class EdgeTts
{
    private const string Token = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    // Edge version the endpoint sees; old versions get 403-blocklisted, so keep this reasonably fresh.
    private const string EdgeVersion = "140.0.3485.14";
    private const string GecVersion = "1-" + EdgeVersion;
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/140.0.0.0 Safari/537.36 Edg/" + EdgeVersion;

    private static string Gec()
    {
        long ticks = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11_644_473_600L;
        ticks -= ticks % 300;
        var bytes = Encoding.ASCII.GetBytes((ticks * 10_000_000L).ToString() + Token);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    // ---------------- voices ----------------

    private static List<(string Name, string Lang, string Gender)>? _voices;

    public static async Task<List<(string Name, string Lang, string Gender)>> VoicesAsync()
    {
        if (_voices != null) return _voices;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        string url = "https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list" +
                     $"?trustedclienttoken={Token}&Sec-MS-GEC={Gec()}&Sec-MS-GEC-Version={GecVersion}";
        string json = await http.GetStringAsync(url);
        var list = new List<(string, string, string)>();
        foreach (var v in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string name = v.GetProperty("ShortName").GetString() ?? "";
            string lang = v.GetProperty("Locale").GetString() ?? "";
            string gender = v.TryGetProperty("Gender", out var g) ? (g.GetString() ?? "") : "";
            if (name.Length > 0) list.Add((name, lang, gender));
        }
        if (list.Count == 0) throw new Exception("Edge voice list came back empty.");
        return _voices = list;
    }

    // ---------------- synthesis ----------------

    public static async Task<byte[]> SynthesizeAsync(string text, string voice, double rate, CancellationToken ct = default)
    {
        string ratePct = (rate >= 1 ? "+" : "") + Math.Round((rate - 1) * 100) + "%";
        string lang = voice.Split('-') is { Length: >= 2 } p ? p[0] + "-" + p[1] : "en-US";
        string ssml =
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{lang}'>" +
            $"<voice name='{voice}'><prosody pitch='+0Hz' rate='{ratePct}' volume='+0%'>{Escape(text)}</prosody></voice></speak>";

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("User-Agent", Ua);
        string url = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
                     $"?TrustedClientToken={Token}&Sec-MS-GEC={Gec()}&Sec-MS-GEC-Version={GecVersion}" +
                     $"&ConnectionId={Guid.NewGuid():N}";
        await ws.ConnectAsync(new Uri(url), ct);

        string ts = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'");
        string cfg = "X-Timestamp:" + ts + "\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\"," +
            "\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
        await Send(ws, cfg, ct);

        string req = "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\nContent-Type:application/ssml+xml\r\n" +
                     "X-Timestamp:" + ts + "Z\r\nPath:ssml\r\n\r\n" + ssml;
        await Send(ws, req, ct);

        var audio = new List<byte>();
        var buf = new byte[64 * 1024];
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Close) break;
            var data = ms.ToArray();
            if (r.MessageType == WebSocketMessageType.Text)
            {
                string msg = Encoding.UTF8.GetString(data);
                if (msg.Contains("Path:turn.end")) break;
            }
            else if (data.Length > 2)
            {
                int hlen = (data[0] << 8) | data[1];
                if (hlen + 2 <= data.Length)
                {
                    string header = Encoding.UTF8.GetString(data, 2, hlen);
                    if (header.Contains("Path:audio"))
                        for (int i = 2 + hlen; i < data.Length; i++) audio.Add(data[i]);
                }
            }
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        if (audio.Count == 0) throw new Exception("Edge TTS returned no audio (endpoint may have changed).");
        return audio.ToArray();
    }

    private static Task Send(ClientWebSocket ws, string msg, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, ct);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;").Replace("\"", "&quot;");
}
