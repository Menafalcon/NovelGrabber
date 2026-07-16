using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovelGrabber;

/// <summary>Paste any text → hear it with the free Edge neural voices. Lives INSIDE the Reader
/// tab (swaps the reader surface for an input). Audio plays through a hidden WebView2 —
/// Chromium ships its own MP3 decoder, so no Windows Media Player dependency (N editions).</summary>
public sealed class TtsSpeakPanel : Panel
{
    private readonly TextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Segoe UI", 11f) };
    private readonly PillCombo _voice = new() { Width = 250, Height = 34 };
    private readonly PillCombo _rate = new() { Width = 72, Height = 34 };
    private readonly FlatButton _play = new() { Text = "▶  Speak", Width = 104, Height = 34, Style = FlatButton.Kind.Accent };
    private readonly FlatButton _stopBtn = new() { Text = "■  Stop", Width = 84, Height = 34, Style = FlatButton.Kind.Ghost, Enabled = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.SubText, Font = Theme.FontSub, Text = "Paste some text and press Speak." };

    private readonly CoreWebView2Environment? _env;
    private WebView2? _audio;
    private TaskCompletionSource<bool>? _audioDone;
    private CancellationTokenSource? _cts;
    private bool _voicesLoaded;

    public TtsSpeakPanel(CoreWebView2Environment? env)
    {
        _env = env;
        BackColor = Theme.Panel;
        Padding = new Padding(16, 10, 16, 8);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, BackColor = Theme.Panel };
        _voice.Margin = new Padding(0, 4, 8, 0);
        _rate.Margin = new Padding(0, 4, 10, 0);
        _play.Margin = new Padding(0, 0, 8, 0);
        top.Controls.Add(_voice); top.Controls.Add(_rate);
        top.Controls.Add(_play); top.Controls.Add(_stopBtn);

        var box = new Card { Dock = DockStyle.Fill, Padding = new Padding(10), Radius = 10 };
        box.BackColor = Theme.Panel; box.Stroke = Theme.Border;
        box.Controls.Add(_text);

        Controls.Add(box);
        Controls.Add(_status);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Panel });
        Controls.Add(top);

        Theme.DarkScrollbars(_text);

        _rate.SetItems(new[] { "0.75×", "1×", "1.25×", "1.5×", "1.75×", "2×" }, "1×");
        _voice.SetItems(new[] { "en-US-AvaNeural" });

        _play.Click += async (_, _) => await PlayAsync();
        _stopBtn.Click += (_, _) => StopPlayback("Stopped.");
        VisibleChanged += async (_, _) =>
        {
            if (Visible && !_voicesLoaded) await LoadVoicesAsync();
            if (!Visible) StopPlayback("");
        };
        _ = LoadVoicesAsync();   // head-start so the list is ready before the panel is opened
    }

    private bool _loadingVoices;
    private async Task LoadVoicesAsync()
    {
        if (_loadingVoices || _voicesLoaded) return;
        _loadingVoices = true;
        _status.Text = "Loading voices…";
        try
        {
            var voices = await EdgeTts.VoicesAsync();
            var names = voices.Select(v => v.Name)
                .OrderBy(n => n.StartsWith("en-US") ? 0 : n.StartsWith("en") ? 1 : 2).ThenBy(n => n).ToList();
            if (names.Count == 0) { _status.Text = "No voices available — check your connection."; return; }
            string keep = _voice.SelectedItem ?? "en-US-AvaNeural";
            _voice.SetItems(names, names.Contains(keep) ? keep : names[0]);
            _voicesLoaded = true;
            _status.Text = $"{names.Count} voices ready. Paste text and press Speak.";
        }
        catch (Exception ex) { _status.Text = "Couldn't load voices: " + ex.Message; }
        finally { _loadingVoices = false; }
    }

    // ---------------- playback via hidden WebView2 ----------------

    private async Task<bool> EnsureAudioAsync()
    {
        if (_audio?.CoreWebView2 != null) return true;
        if (_env == null) { _status.Text = "Browser engine isn't ready — try again in a moment."; return false; }
        _audio = new WebView2 { Size = new Size(1, 1), Location = new Point(-10, -10) };
        Controls.Add(_audio);
        await _audio.EnsureCoreWebView2Async(_env);
        _audio.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            string msg = e.TryGetWebMessageAsString();
            if (msg == "ended") _audioDone?.TrySetResult(true);
            else if (msg == "err") _audioDone?.TrySetException(new Exception("Audio playback failed"));
        };
        _audio.CoreWebView2.NavigateToString("<html><body></body></html>");
        return true;
    }

    private async Task PlayMp3Async(byte[] mp3, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioDone = done;
        ct.Register(() => done.TrySetCanceled());
        string b64 = Convert.ToBase64String(mp3);
        await _audio!.CoreWebView2.ExecuteScriptAsync(
            "(function(){if(!window.a){window.a=new Audio();" +
            "window.a.onended=()=>chrome.webview.postMessage('ended');" +
            "window.a.onerror=()=>chrome.webview.postMessage('err');}" +
            $"window.a.src='data:audio/mpeg;base64,{b64}';" +
            "window.a.play().catch(()=>chrome.webview.postMessage('err'));})()");
        await done.Task;
    }

    private async Task PlayAsync()
    {
        string text = _text.Text.Trim();
        if (text.Length == 0) { _status.Text = "Nothing to read — paste some text first."; return; }
        StopPlayback("");
        if (!await EnsureAudioAsync()) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _play.Enabled = false; _stopBtn.Enabled = true;
        string voice = _voice.SelectedItem ?? "en-US-AvaNeural";
        double rate = _rate.SelectedIndex switch { 0 => 0.75, 2 => 1.25, 3 => 1.5, 4 => 1.75, 5 => 2.0, _ => 1.0 };
        var chunks = Chunks(text);
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                if (cts.IsCancellationRequested) return;
                _status.Text = chunks.Count > 1 ? $"Speaking…  part {i + 1} of {chunks.Count}" : "Speaking…";
                byte[] mp3 = await EdgeTts.SynthesizeAsync(chunks[i], voice, rate);
                if (cts.IsCancellationRequested) return;
                await PlayMp3Async(mp3, cts.Token);
            }
            if (!cts.IsCancellationRequested) _status.Text = "Done.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Couldn't speak: " + ex.Message; }
        finally
        {
            if (_cts == cts) { _play.Enabled = true; _stopBtn.Enabled = false; }
        }
    }

    private void StopPlayback(string msg)
    {
        _cts?.Cancel();
        try { _ = _audio?.CoreWebView2?.ExecuteScriptAsync("window.a&&window.a.pause();"); } catch { }
        _play.Enabled = true; _stopBtn.Enabled = false;
        if (msg.Length > 0) _status.Text = msg;
    }

    /// <summary>≤3000-char pieces split on paragraph boundaries (Edge TTS sweet spot).</summary>
    private static List<string> Chunks(string text)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        foreach (var para in Regex.Split(text, @"\n+"))
        {
            var p = para.Trim();
            if (p.Length == 0) continue;
            if (sb.Length > 0 && sb.Length + p.Length > 3000) { res.Add(sb.ToString()); sb.Clear(); }
            while (p.Length > 3000) { res.Add(p[..3000]); p = p[3000..]; }
            if (p.Length > 0) sb.AppendLine(p);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }
}
