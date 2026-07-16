using System;
using System.Text.Json;

namespace NovelGrabber;

public sealed class ChapterLink
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class ExtractResult
{
    public string NovelTitle { get; set; } = "";
    public string ChapterTitle { get; set; } = "";
    public string Content { get; set; } = "";
    public string NextUrl { get; set; } = "";
    public string FirstUrl { get; set; } = "";
    public string OgTitle { get; set; } = "";
    public string OgImage { get; set; } = "";
    public string DocTitle { get; set; } = "";
    public string Url { get; set; } = "";
    public ChapterLink[] Chapters { get; set; } = Array.Empty<ChapterLink>();
}

public static class Extractor
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static string BuildScript(SiteRule rule)
    {
        string cfg = JsonSerializer.Serialize(new
        {
            content = rule.Content,
            novelTitle = rule.NovelTitle,
            chapterTitle = rule.ChapterTitle,
            next = rule.Next,
            list = rule.List,
            first = rule.First
        });

        return @"(function(){
  var CFG = " + cfg + @";
  function qs(s){ try{ return s?document.querySelector(s):null; }catch(e){ return null; } }
  function qsa(s){ try{ return s?Array.prototype.slice.call(document.querySelectorAll(s)):[]; }catch(e){ return []; } }
  function meta(p){ var e=document.querySelector('meta[property=""'+p+'""]')||document.querySelector('meta[name=""'+p+'""]'); return e?(e.getAttribute('content')||''):''; }
  function chapterish(u){ return /\d|chapter|chapitre|cap-?itulo|read|\/c\//i.test(u||''); }
  function generic(){
     var best=null,bs=0,nodes=document.querySelectorAll('div,article,section,main,td');
     for(var i=0;i<nodes.length;i++){ var n=nodes[i];
        var cls=((n.className||'')+' '+(n.id||'')).toString().toLowerCase();
        if(/nav|menu|footer|header|sidebar|comment|rating|relate|recommend|breadcrumb|disqus|widget/.test(cls)) continue;
        var t=n.innerText||''; if(t.length<200) continue;
        var links=n.querySelectorAll('a').length, ps=n.querySelectorAll('p').length, brs=n.querySelectorAll('br').length;
        var score=t.length - links*120 + ps*30 + brs*10;
        if(score>bs){ bs=score; best=n; }
     }
     return best;
  }
  function bad(node){ var cls=((node.className||'')+' '+(node.id||'')).toString().toLowerCase();
     return /(^|\s)(ad|ads|promo|share|nav|comment|footer|caption)(\s|$|-)/.test(cls); }
  // Rebuild paragraphs from the LIVE element (a detached clone loses layout, which
  // makes innerText collapse the whole chapter into one block).
  function getText(el){
     if(!el) return '';
     var ps=el.querySelectorAll('p');
     if(ps && ps.length>=3){
        var parts=[];
        for(var i=0;i<ps.length;i++){ var p=ps[i]; if(bad(p)) continue;
           var t=(p.innerText||p.textContent||'').replace(/ /g,' ').replace(/\s+\n/g,'\n').trim();
           if(t) parts.push(t); }
        var joined=parts.join('\n\n');
        if(joined.replace(/\s/g,'').length>40) return joined;
     }
     // fall back to the attached element's innerText (keeps \n from <br>/pre-wrap)
     return (el.innerText||el.textContent||'').replace(/\r/g,'').replace(/\n{3,}/g,'\n\n').trim();
  }
  function abs(u){ try{ return u?new URL(u,location.href).href:''; }catch(e){ return u||''; } }
  function pickCover(){
     var m=meta('og:image'); if(m) return abs(m);
     var l=document.querySelector('link[rel=""image_src""]'); if(l&&l.getAttribute('href')) return abs(l.getAttribute('href'));
     var sels=['.book img','.info-holder img','.books img','.cover img','.book-cover img','.novel-cover img',
               '.summary_image img','figure.cover img','img.cover','.fixed-img img','.book-img img','.series-cover img','.thumb img','.det-info img'];
     for(var k=0;k<sels.length;k++){ var i=document.querySelector(sels[k]); if(i){ var s=i.getAttribute('src')||i.getAttribute('data-src')||i.src; if(s) return abs(s); } }
     var imgs=document.querySelectorAll('img');
     for(var j=0;j<imgs.length;j++){ var im=imgs[j]; var src=(im.getAttribute('src')||'').toLowerCase();
        if(/cover|\/thumbs?\//.test(src) && im.src && !/logo|icon|avatar|banner/.test(src)) return abs(im.src); }
     return '';
  }
  var contentEl=null;
  (CFG.content||[]).some(function(s){ var e=qs(s); if(e){ var t=(e.innerText||e.textContent||'').trim(); if(t.length>40){ contentEl=e; return true; } } return false; });
  if(!contentEl) contentEl=generic();
  var content=getText(contentEl);

  var chapTitle='';
  (CFG.chapterTitle||[]).some(function(s){ var e=qs(s); if(e&&(e.innerText||'').trim()){ chapTitle=e.innerText.trim(); return true; } return false; });
  if(!chapTitle){ var h=qs('h1')||qs('h2'); if(h) chapTitle=(h.innerText||'').trim(); }

  var novelTitle='';
  (CFG.novelTitle||[]).some(function(s){ var e=qs(s); if(e&&(e.innerText||'').trim()){ novelTitle=e.innerText.trim(); return true; } return false; });

  // next: trust site selectors; for rel/text fallback require a chapter-looking url
  var nextUrl='';
  (CFG.next||[]).some(function(s){ var e=qs(s); if(e&&e.href){ nextUrl=e.href; return true; } return false; });
  if(!nextUrl){ var rn=qs('a[rel=next]'); if(rn&&rn.href&&chapterish(rn.href)) nextUrl=rn.href; }
  if(!nextUrl){ var as=qsa('a'); for(var j=0;j<as.length;j++){ var a=as[j]; var tx=((a.innerText||'')+' '+(a.title||'')+' '+(a.getAttribute('aria-label')||'')).trim().toLowerCase(); if(a.href && chapterish(a.href) && /(^|\b)(next|next chapter|next page)\b|^›$|^»$|下一[章页]|次[のへ]|다음/.test(tx)){ nextUrl=a.href; break; } } }

  // first chapter link (climb to anchor if selector hit a child)
  var firstUrl='';
  (CFG.first||[]).some(function(s){ var e=qs(s); if(e){ var a=(e.tagName==='A')?e:(e.closest?e.closest('a'):null); if(a&&a.href){ firstUrl=a.href; return true; } } return false; });

  // table-of-contents links
  var chapters=[];
  (CFG.list||[]).some(function(s){ var arr=qsa(s); if(arr.length>3){ chapters=arr.map(function(a){ return { title:((a.innerText||a.title||'').trim().slice(0,200)), url:a.href }; }).filter(function(x){ return x.url; }); return true; } return false; });

  return { novelTitle:novelTitle, chapterTitle:chapTitle, content:content, nextUrl:nextUrl, firstUrl:firstUrl,
           ogTitle:meta('og:title'), ogImage:pickCover(), docTitle:document.title,
           chapters:chapters, url:location.href };
})();";
    }

    public static ExtractResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try { return JsonSerializer.Deserialize<ExtractResult>(json, Opts); }
        catch { return null; }
    }
}
