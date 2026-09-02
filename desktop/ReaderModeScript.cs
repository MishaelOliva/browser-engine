namespace MishaWeb;

internal static class ReaderModeScript
{
    // This is intentionally app-owned and injected only after the user requests
    // reader mode. It never runs at document start and never makes a request.
    public const string Toggle = """
(() => {
  const id = '__misha_reader_overlay';
  const priorOverflow = document.documentElement?.dataset?.mishaReaderOverflow;
  const existing = document.getElementById(id);
  if (existing) {
    existing.remove();
    if (document.documentElement) {
      document.documentElement.style.overflow = priorOverflow ?? '';
      delete document.documentElement.dataset.mishaReaderOverflow;
    }
    return true;
  }
  if (!document.body || !document.documentElement) return false;
  const selectors = ['article', 'main', '[role="main"]', '[itemprop="articleBody"]', '.article-body', '.post-content', '.entry-content', '.story-body', '#content', '#main'];
  const candidates = [];
  const seen = new Set();
  for (const selector of selectors) {
    for (const element of document.querySelectorAll(selector)) {
      if (!seen.has(element)) { seen.add(element); candidates.push(element); }
    }
  }
  if (!candidates.length) candidates.push(document.body);
  const score = (element) => {
    const text = (element.innerText || '').trim();
    const paragraphs = element.querySelectorAll('p').length;
    const headings = element.querySelectorAll('h1,h2,h3').length;
    const links = [...element.querySelectorAll('a')].reduce((n, a) => n + ((a.innerText || '').length), 0);
    const density = text.length ? links / text.length : 1;
    const bad = element.querySelectorAll('nav,form,aside,footer,header, .sidebar, .nav, .comments, [role="navigation"]').length;
    return text.length + paragraphs * 180 + headings * 80 - density * 600 - bad * 320;
  };
  let winner = candidates[0];
  for (const candidate of candidates) if (score(candidate) > score(winner)) winner = candidate;
  if (((winner.innerText || '').trim()).length < 320) return false;
  const clone = winner.cloneNode(true);
  clone.querySelectorAll('script,style,form,button,input,select,textarea,nav,aside,header,footer,dialog,iframe,video,audio,canvas,[role="navigation"],[role="dialog"]').forEach(node => node.remove());
  clone.querySelectorAll('[id]').forEach(node => node.removeAttribute('id'));
  clone.querySelectorAll('a[href]').forEach(a => { try { a.href = new URL(a.getAttribute('href'), location.href).href; } catch (_) {} });
  clone.querySelectorAll('img[src]').forEach(img => { try { img.src = new URL(img.getAttribute('src'), location.href).href; } catch (_) {} });
  const text = (clone.innerText || '').trim();
  if (text.length < 240) return false;
  const overlay = document.createElement('div');
  overlay.id = id;
  overlay.setAttribute('role', 'document');
  overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483646;overflow:auto;background:Canvas;color:CanvasText;padding:clamp(18px,5vw,72px) 20px 64px;box-sizing:border-box;font:1em/1.65 system-ui,sans-serif;color-scheme:light dark;';
  const content = document.createElement('div');
  content.style.cssText = 'max-width:760px;margin:0 auto;font-size:1.12em;';
  content.appendChild(clone);
  content.querySelectorAll('h1,h2,h3,h4').forEach(h => h.style.lineHeight = '1.2');
  content.querySelectorAll('img').forEach(img => { img.style.maxWidth = '100%'; img.style.height = 'auto'; });
  overlay.appendChild(content);
  document.documentElement.dataset.mishaReaderOverflow = document.documentElement.style.overflow || '';
  document.documentElement.style.overflow = 'hidden';
  document.body.appendChild(overlay);
  return true;
})()
""";
}
