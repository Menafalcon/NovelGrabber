using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NovelGrabber;

/// <summary>
/// Lightweight ad/tracker blocker for the built-in preview browser. Host-suffix matching
/// against the networks novel sites actually use (adsterra/monetag/propeller/google ads/
/// native widgets) plus popunder URL patterns for sub-resources. Deliberately conservative
/// so Cloudflare challenges and site functionality never break.
/// </summary>
public static class AdBlock
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // google ads & tracking
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "adservice.google.com", "google-analytics.com", "googletagmanager.com",
        "googletagservices.com", "adsense.google.com",
        // big exchanges / SSPs
        "adnxs.com", "criteo.com", "criteo.net", "rubiconproject.com", "pubmatic.com",
        "openx.net", "casalemedia.com", "33across.com", "gumgum.com", "sharethrough.com",
        "smartadserver.com", "smaato.net", "inmobi.com", "amazon-adsystem.com",
        "adsafeprotected.com", "adroll.com", "yieldmo.com", "sonobi.com", "indexww.com",
        // native "around the web" widgets
        "taboola.com", "outbrain.com", "revcontent.com", "mgid.com", "zergnet.com",
        // the popunder/push networks novel sites love
        "propellerads.com", "propellerclick.com", "adsterra.com", "adsterratech.com",
        "highperformanceformat.com", "effectiveratecpm.com", "profitableratecpm.com",
        "monetag.com", "onclickalgo.com", "onclasrv.com", "popads.net", "popcash.net",
        "poptm.com", "hilltopads.net", "clickadu.com", "adcash.com", "exoclick.com",
        "exosrv.com", "juicyads.com", "trafficjunky.com", "tsyndicate.com",
        "galaksion.com", "richads.com", "bidvertiser.com", "yllix.com",
        "creative-sb.com", "aclickads.com", "adskeeper.com",
        // crypto/casino banners (BC.GAME etc.)
        "bc.game", "bcgame.link", "a-ads.com", "coinzilla.io", "cointraffic.io", "adshares.net",
        // analytics/heatmaps that just slow pages down
        "scorecardresearch.com", "quantserve.com", "hotjar.com", "mouseflow.com",
        "an.yandex.ru", "mc.yandex.ru",
    };

    private static readonly Regex UrlPat = new(@"pop(under|up)|adsbygoogle|/ads/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>mainFrame: page navigations only block on HOST matches (an ad-network navigation
    /// is a popunder) — URL patterns could false-positive a legit chapter URL.</summary>
    public static bool ShouldBlock(string url, bool mainFrame)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Host.Length == 0) return false;
        string host = u.Host;
        // suffix match: x.y.doubleclick.net hits "doubleclick.net"
        int idx = 0;
        while (idx >= 0)
        {
            if (Hosts.Contains(idx == 0 ? host : host[(idx + 1)..])) return true;
            idx = host.IndexOf('.', idx + 1);
        }
        return !mainFrame && UrlPat.IsMatch(url);
    }
}
