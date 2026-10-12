// Reader mode. Function body: it is wrapped as (function(g){ ... })(window) and run in the page when you ask for it.
// Returns 'on' (reader opened), 'off' (reader closed) or 'none' (no article-like text found).
// It builds the reader with createElement and textContent only, never innerHTML, so nothing from the page can run inside it.
var doc = g.document, ID = '__recognition_reader__';
var existing = doc.getElementById(ID);
if (existing) {
  var prev = existing.__prevOverflow;
  existing.remove();
  try { doc.documentElement.style.overflow = prev || ''; } catch (e) {}
  return 'off';
}

var SKIP = { SCRIPT:1, STYLE:1, NOSCRIPT:1, NAV:1, ASIDE:1, FOOTER:1, FORM:1, BUTTON:1, SVG:1, IFRAME:1, CANVAS:1, SELECT:1, INPUT:1, TEXTAREA:1, TEMPLATE:1, DIALOG:1, MENU:1, VIDEO:1, AUDIO:1, OBJECT:1, EMBED:1 };
var MAX_BLOCKS = 400, MAX_CHARS = 80000;

function tag(e) { return (e.tagName || '').toUpperCase(); }
function kids(e) { return e.children ? Array.prototype.slice.call(e.children) : []; }
function txt(e) { return String(e.textContent || '').replace(/\s+/g, ' ').trim(); }
function attr(e, n) { try { return e.getAttribute ? (e.getAttribute(n) || '') : ''; } catch (x) { return ''; } }
function hidden(e) { return attr(e, 'aria-hidden') === 'true' || (e.hasAttribute && e.hasAttribute('hidden')); }
function linkText(e) {
  var total = 0, as = e.querySelectorAll ? Array.prototype.slice.call(e.querySelectorAll('a')) : [];
  for (var i = 0; i < as.length; i++) total += txt(as[i]).length;
  return total;
}

// the element that most likely holds the article: a semantic container, else the one with the most paragraph text
function pickRoot() {
  var sem = ['article', 'main', '[role=main]'];
  for (var s = 0; s < sem.length; s++) {
    var list = Array.prototype.slice.call(doc.querySelectorAll(sem[s]));
    var best = null, bestLen = 0;
    for (var i = 0; i < list.length; i++) { var l = txt(list[i]).length; if (l > bestLen) { best = list[i]; bestLen = l; } }
    if (best && bestLen > 400) return best;
  }
  var cands = Array.prototype.slice.call(doc.querySelectorAll('div,section,article,main,body')), top = null, topScore = 0;
  for (var c = 0; c < cands.length; c++) {
    var ps = kids(cands[c]).filter(function (k) { return tag(k) === 'P'; }), score = 0;
    for (var p = 0; p < ps.length; p++) { var t = txt(ps[p]); if (t.length > 40) score += t.length - 1.5 * linkText(ps[p]); }
    if (score > topScore) { top = cands[c]; topScore = score; }
  }
  return topScore > 200 ? top : null;
}

var blocks = [], chars = 0;
function push(kind, text, extra) {
  if (blocks.length >= MAX_BLOCKS || chars >= MAX_CHARS) return;
  blocks.push({ k: kind, t: text, x: extra }); chars += (text || '').length;
}
function walk(e, depth) {
  if (depth > 40 || blocks.length >= MAX_BLOCKS) return;
  var kids2 = kids(e);
  for (var i = 0; i < kids2.length; i++) {
    var k = kids2[i], t = tag(k);
    if (SKIP[t] || hidden(k)) continue;
    if (/^H[1-6]$/.test(t)) { var h = txt(k); if (h) push('h', h, t.charAt(1)); }
    else if (t === 'P') { var p = txt(k); if (p.length >= 25 && linkText(k) < 0.8 * p.length) push('p', p); else if (p.length >= 2 && p.length < 25 && !linkText(k)) push('p', p); }
    else if (t === 'BLOCKQUOTE') { var q = txt(k); if (q) push('q', q); }
    else if (t === 'PRE') { var c = String(k.textContent || ''); if (c.trim()) push('c', c.slice(0, 6000)); }
    else if (t === 'UL' || t === 'OL') {
      var items = kids(k).filter(function (li) { return tag(li) === 'LI'; }).map(txt).filter(function (s) { return s.length > 0; });
      if (items.length && items.join('').length > 20 && linkText(k) < 0.7 * items.join('').length) push(t === 'OL' ? 'ol' : 'ul', items.join('\n'));
    }
    else if (t === 'IMG') {
      var src = attr(k, 'src');
      if (/^https?:\/\//i.test(src) || /^data:image\/(png|jpeg|gif|webp);base64,/i.test(src)) push('i', attr(k, 'alt'), src);
    }
    else if (t === 'FIGURE' || t === 'DIV' || t === 'SECTION' || t === 'ARTICLE' || t === 'MAIN' || t === 'SPAN' || t === 'HEADER' || t === 'A' || t === 'PICTURE' || t === 'LI' || t === 'BODY') walk(k, depth + 1);
  }
}

var root = pickRoot();
if (!root) return 'none';
walk(root, 0);
var words = blocks.filter(function (b) { return b.k === 'p' || b.k === 'q'; }).reduce(function (n, b) { return n + b.t.split(' ').length; }, 0);
if (blocks.length < 2 || words < 40) return 'none';

var themes = {
  light: { bg: '#ffffff', fg: '#1d2430', mut: '#5b6577', line: '#d9dde4' },
  sepia: { bg: '#f4ecd8', fg: '#3b3226', mut: '#7a6b55', line: '#dccfb4' },
  dark:  { bg: '#15181d', fg: '#e4e7ec', mut: '#9aa3b2', line: '#2a2f38' }
};
var cur = 'dark', size = 19;
var wrap = doc.createElement('div');
wrap.setAttribute('id', ID);
wrap.__prevOverflow = doc.documentElement.style.overflow;
var bar = doc.createElement('div'), col = doc.createElement('div');
function css(el, s) { el.style.cssText = s; }
function paint() {
  var th = themes[cur];
  css(wrap, 'position:fixed;inset:0;z-index:2147483647;overflow:auto;background:' + th.bg + ';color:' + th.fg + ';font-family:Georgia,"Times New Roman",serif;');
  css(bar, 'position:sticky;top:0;display:flex;gap:8px;justify-content:flex-end;padding:10px 16px;background:' + th.bg + ';border-bottom:1px solid ' + th.line + ';font:13px "Segoe UI",Arial,sans-serif;');
  css(col, 'max-width:720px;margin:0 auto;padding:28px 20px 90px;font-size:' + size + 'px;line-height:1.7;');
}
function button(label, fn) {
  var b = doc.createElement('button'); b.textContent = label;
  css(b, 'padding:5px 11px;border:1px solid #8884;border-radius:6px;background:transparent;color:inherit;font:inherit;cursor:pointer;');
  b.addEventListener('click', fn); return b;
}
function close() { var prev = wrap.__prevOverflow; wrap.remove(); try { doc.documentElement.style.overflow = prev || ''; } catch (e) {} }
bar.appendChild(button('A-', function () { size = Math.max(13, size - 1); paint(); }));
bar.appendChild(button('A+', function () { size = Math.min(32, size + 1); paint(); }));
bar.appendChild(button('Light', function () { cur = 'light'; paint(); }));
bar.appendChild(button('Sepia', function () { cur = 'sepia'; paint(); }));
bar.appendChild(button('Dark', function () { cur = 'dark'; paint(); }));
bar.appendChild(button('Close (Esc)', close));
wrap.appendChild(bar);

var title = doc.createElement('h1');
title.textContent = String(doc.title || '').slice(0, 300);
css(title, 'font-size:1.7em;line-height:1.25;margin:0 0 .7em;');
col.appendChild(title);
for (var i = 0; i < blocks.length; i++) {
  var b = blocks[i], el;
  if (b.k === 'h') { el = doc.createElement('h' + (b.x === '1' ? '2' : b.x)); el.textContent = b.t; css(el, 'margin:1.3em 0 .4em;line-height:1.3;'); }
  else if (b.k === 'p') { el = doc.createElement('p'); el.textContent = b.t; css(el, 'margin:0 0 1em;'); }
  else if (b.k === 'q') { el = doc.createElement('blockquote'); el.textContent = b.t; css(el, 'margin:1em 0;padding:.2em 1em;border-left:3px solid #8886;font-style:italic;'); }
  else if (b.k === 'c') { el = doc.createElement('pre'); el.textContent = b.t; css(el, 'overflow:auto;padding:12px;border-radius:8px;background:#8881;font:13px Consolas,monospace;line-height:1.5;'); }
  else if (b.k === 'ul' || b.k === 'ol') {
    el = doc.createElement(b.k); css(el, 'margin:0 0 1em;padding-left:1.4em;');
    b.t.split('\n').forEach(function (s) { var li = doc.createElement('li'); li.textContent = s; el.appendChild(li); });
  }
  else if (b.k === 'i') { el = doc.createElement('img'); el.setAttribute('src', b.x); el.setAttribute('alt', b.t || ''); el.setAttribute('referrerpolicy', 'no-referrer'); css(el, 'max-width:100%;height:auto;display:block;margin:1em auto;border-radius:6px;'); }
  if (el) col.appendChild(el);
}
wrap.appendChild(col);
paint();
try { doc.documentElement.style.overflow = 'hidden'; } catch (e) {}
doc.documentElement.appendChild(wrap);
wrap.addEventListener('keydown', function (ev) { if (ev.key === 'Escape') close(); });
return 'on';
