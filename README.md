# NovelGrabber

**Download web novels and light novels, read them offline, and listen to them with text‑to‑speech — on Windows.**

NovelGrabber turns web serials into a clean, ad‑free personal library. It ships with a built‑in browser that clears Cloudflare and login walls, a proper reader with themes and TTS, and EPUB/PDF export. There's no account, no server, and nothing leaves your device.

|  |  |
| --- | --- |
| 🖥️ **Desktop** | Windows app (C# / WinForms + WebView2) |

---

## Features

* **Grab whole novels** from a chapter‑1 or contents URL, in batches of any size.


* **Built‑in browser** to browse supported sites, solve captchas/log in, and scrape the page you're on — no copy‑pasting links.


* **Library** with cover‑art cards, reading progress, search, and **categories** (General / Completed / your own) plus one‑click **Auto‑sort** that groups multi‑volume series by title similarity.


* **Reader** with four themes (Dark / Dark Sepia / Sepia / Light — applied app‑wide), adjustable font/size/spacing, resume‑where‑you‑left‑off, and LN **illustrations** rendered inline.


* **Text‑to‑speech:** Free Microsoft **Edge neural voices** out of the box, plus optional **Google Cloud TTS** with your own key. Includes a "read any pasted text" mode.


* **EPUB & PDF export** per novel (images included), and **import** existing `.epub` files/folders into the library.


* **Built‑in ad blocker** for the browser (host‑based, tuned for the ad networks novel sites use).



## Supported sites

NovelLunar · Novel Fire · Ranobes · NovelHall · KAT Reading Cafe · Novelpia (Global) · Novel Arrow · Light Novel World · NovelFull
— plus a **generic extractor** that grabs the largest text block on any other site.

Per‑site rules live in [`sites.json`](https://www.google.com/search?q=sites.json) (CSS selectors + navigation) and can be edited **without recompiling**.

---

## Desktop (Windows)

**Requirements:** Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download), and the WebView2 Runtime (bundled with modern Windows/Edge).

```bash
cd "Novel Scraper Final"
dotnet build -c Release
# run it
bin/Release/net8.0-windows/NovelGrabber.exe

```

Novels are stored under `Documents\NovelGrabber\<Title>\` (`meta.json` + chapter XHTML + cover) — change the folder in **Settings**.

---

## How it works

1. A hidden **WebView** loads each chapter page so JavaScript‑rendered sites and Cloudflare challenges work (a plain HTTP client can't do this).


2. Injected JS extracts the chapter title + body using the per‑site selectors in `sites.json`, falling back to a largest‑text‑block heuristic.


3. Navigation follows the site's *Next* link, increments the URL, or walks the table of contents — whichever the site needs.


4. Chapters are written to disk as XHTML with a `meta.json` index; the reader and EPUB/PDF writers build from that.



## Legal license.md

For personal, offline reading only. Respect the terms of service of the sites you use and the copyright of the works you download. This project is not affiliated with any of the sites listed.

## License

MIT — see LICENSE
