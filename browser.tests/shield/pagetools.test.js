// node pagetools.test.js : tests reader.js and pip.js against a small simulated DOM.
const fs=require('fs'),path=require('path');
const root=path.join(__dirname,'..','..','browser','shield');
const readerSrc=fs.readFileSync(path.join(root,'reader.js'),'utf8');
const pipSrc=fs.readFileSync(path.join(root,'pip.js'),'utf8');
let pass=0,fail=0; const ck=(c,l)=>{ if(c)pass++; else {fail++; console.log('FAIL: '+l);} };

class El{
  constructor(tag){ this.tagName=String(tag).toUpperCase(); this.children=[]; this.parent=null; this.attrs={}; this._text=''; this.style={cssText:''}; this.listeners={}; }
  get textContent(){ let out='',stack=[this]; while(stack.length){ const e=stack.pop(); out+=e._text; for(let i=e.children.length-1;i>=0;i--) stack.push(e.children[i]); } return out; }   // iterative like the real DOM
  set textContent(v){ this._text=String(v); this.children=[]; }
  set innerHTML(v){ throw new Error('innerHTML must never be used'); }
  getAttribute(n){ return Object.prototype.hasOwnProperty.call(this.attrs,n)?this.attrs[n]:null; }
  setAttribute(n,v){ this.attrs[n]=String(v); }
  hasAttribute(n){ return Object.prototype.hasOwnProperty.call(this.attrs,n); }
  removeAttribute(n){ delete this.attrs[n]; }
  appendChild(c){ c.parent=this; this.children.push(c); return c; }
  remove(){ if(this.parent){ this.parent.children=this.parent.children.filter(x=>x!==this); this.parent=null; } }
  addEventListener(t,f){ (this.listeners[t]=this.listeners[t]||[]).push(f); }
  all(){ const r=[],stack=[]; for(let i=this.children.length-1;i>=0;i--) stack.push(this.children[i]); while(stack.length){ const e=stack.pop(); r.push(e); for(let i=e.children.length-1;i>=0;i--) stack.push(e.children[i]); } return r; }
  querySelectorAll(sel){
    const parts=sel.split(',').map(s=>s.trim()); const out=[];
    for(const e of this.all()){ for(const p of parts){ if(p==='[role=main]'){ if(e.attrs.role==='main') {out.push(e);break;} } else if(e.tagName===p.toUpperCase()){ out.push(e); break; } } }
    return out;
  }
  getBoundingClientRect(){ return {width:this.w||0,height:this.h||0}; }
}
function h(tag,attrs,...kids){ const e=new El(tag); Object.assign(e.attrs,attrs||{}); for(const k of kids){ if(typeof k==='string') e._text+=k; else e.appendChild(k); } return e; }
function makeDoc(bodyKids,title='A page title'){
  const html=new El('html'), body=new El('body'); html.appendChild(body); for(const k of bodyKids) body.appendChild(k);
  const doc={documentElement:html,body,title,pictureInPictureEnabled:true,pictureInPictureElement:null,
    createElement:t=>new El(t),
    getElementById:id=>[html,...html.all()].find(e=>e.attrs.id===id)||null,
    querySelectorAll:s=>html.querySelectorAll(s)};
  return doc;
}
const run=(doc)=>new Function('g',readerSrc)({document:doc});
const para=(n)=>h('p',null,('Sentence number '+n+' of the article body, long enough to count as text. ').repeat(2));

// ---- reader: article page
{
  const art=h('article',null,h('h1',null,'The headline'),para(1),para(2),h('h2',null,'A section'),para(3),para(4),
    h('p',null,h('a',null,'a link only paragraph that is made of nothing but one single link text')),
    h('blockquote',null,'A quoted passage that is long enough.'),h('pre',null,'let x = 1;\nlet y = 2;'),
    h('ul',null,h('li',null,'first item here'),h('li',null,'second item here')),
    h('img',{src:'https://img.example/a.png',alt:'good picture'}),
    h('img',{src:'javascript:alert(1)',alt:'bad js'}),h('img',{src:'data:image/svg+xml;base64,AAAA',alt:'bad svg'}),
    h('p',{'aria-hidden':'true'},'hidden text that must not be shown, it is long enough to look real'),
    h('script',null,'var secret = 1;'),h('aside',null,h('p',null,'related story teaser text that is long enough to be a paragraph here')),h('p',null,'<script>alert(1)</script> real text shown as text, not run as markup, in a paragraph.'));
  const doc=makeDoc([h('nav',null,h('a',null,'Home'),h('a',null,'About')),art,h('footer',null,'copyright footer text that is quite long indeed for a footer')]);
  doc.documentElement.style.overflow='auto';
  ck(run(doc)==='on','article page opens the reader');
  const ov=doc.getElementById('__recognition_reader__'); ck(!!ov,'overlay exists');
  const all=ov.all(); const text=ov.textContent;
  ck(text.includes('A page title')&&text.includes('The headline')&&text.includes('Sentence number 3')&&text.includes('A section'),'title, headings and paragraphs are shown');
  ck(!text.includes('secret')&&!text.includes('hidden text')&&!text.includes('copyright footer')&&!text.includes('related story')&&!text.includes('Home'),'script, hidden, footer, aside and nav are left out');
  ck(!text.includes('a link only paragraph'),'a paragraph that is only a link is left out');
  ck(text.includes('<script>alert(1)</script> real text'),'markup in text stays text');
  ck(all.filter(e=>e.tagName==='IMG').length===1&&all.find(e=>e.tagName==='IMG').attrs.src==='https://img.example/a.png','only the https picture is kept (javascript: and svg dropped)');
  ck(all.find(e=>e.tagName==='IMG').attrs.referrerpolicy==='no-referrer','pictures are fetched without a referrer');
  ck(all.some(e=>e.tagName==='PRE'&&e.textContent.includes('let y = 2;'))&&all.some(e=>e.tagName==='BLOCKQUOTE')&&all.filter(e=>e.tagName==='LI').length===2,'code, quotes and lists are kept');
  ck(!all.some(e=>e.tagName==='A'||e.tagName==='SCRIPT'),'no links or scripts inside the reader');
  ck(doc.documentElement.style.overflow==='hidden','page scrolling is locked while reading');
  // controls
  const bar=ov.children[0]; const btns=bar.children; const col=ov.children[1];
  ck(btns.map(b=>b.textContent).join('|')==='A-|A+|Light|Sepia|Dark|Close (Esc)','controls');
  btns[1].listeners.click[0](); ck(col.style.cssText.includes('font-size:20px'),'A+ makes the text bigger');
  btns[0].listeners.click[0](); btns[0].listeners.click[0](); ck(col.style.cssText.includes('font-size:18px'),'A- makes it smaller');
  btns[2].listeners.click[0](); ck(ov.style.cssText.includes('#ffffff'),'light theme');
  btns[3].listeners.click[0](); ck(ov.style.cssText.includes('#f4ecd8'),'sepia theme');
  for(let i=0;i<30;i++) btns[1].listeners.click[0](); ck(col.style.cssText.includes('font-size:32px'),'text size is capped at 32');
  for(let i=0;i<40;i++) btns[0].listeners.click[0](); ck(col.style.cssText.includes('font-size:13px'),'text size is floored at 13');
  ov.listeners.keydown[0]({key:'Enter'}); ck(!!doc.getElementById('__recognition_reader__'),'other keys do not close it');
  ov.listeners.keydown[0]({key:'Escape'}); ck(!doc.getElementById('__recognition_reader__')&&doc.documentElement.style.overflow==='auto','Escape closes it and restores scrolling');
  ck(run(doc)==='on'&&run(doc)==='off'&&!doc.getElementById('__recognition_reader__'),'a second call closes the reader');
}
// ---- reader: no article
{
  const doc=makeDoc([h('nav',null,h('a',null,'one'),h('a',null,'two')),h('div',null,h('p',null,'short'))]);
  ck(run(doc)==='none'&&!doc.getElementById('__recognition_reader__'),'a page without article text gives none');
  const doc2=makeDoc([h('article',null,h('p',null,'tiny'))]); ck(run(doc2)==='none','a tiny article gives none');
}
// ---- reader: fallback without <article>
{
  const wrap=h('div',null,para(1),para(2),para(3),para(4),para(5));
  const doc=makeDoc([h('div',null,h('p',null,'menu')),wrap]);
  ck(run(doc)==='on','a plain div full of paragraphs is found');
  ck(doc.getElementById('__recognition_reader__').textContent.includes('Sentence number 5'),'its text is shown');
}
// ---- reader: limits and hostile titles
{
  const many=[]; for(let i=0;i<1200;i++) many.push(para(i));
  const doc=makeDoc([h('article',null,...many)],'T'.repeat(1000));
  ck(run(doc)==='on','a huge page still opens');
  const ov=doc.getElementById('__recognition_reader__'); const col=ov.children[1];
  ck(col.children.length<=401,'at most 400 blocks plus the title ('+col.children.length+')');
  ck(col.children[0].textContent.length===300,'the title is cut to 300 characters');
  ck(ov.textContent.length<90000,'total text is capped');
  let deep=h('p',null,'x'); for(let i=0;i<5000;i++){ deep=h('div',null,deep); }
  const doc2=makeDoc([h('article',null,para(1),para(2),para(3),deep)]);
  let ok=true; try{ run(doc2); }catch(e){ ok=false; } ck(ok,'a 5000-level deep page does not overflow the stack');
}

// ---- pip
const pip=(doc)=>new (Object.getPrototypeOf(async function(){}).constructor)('g',pipSrc)({document:doc});
function vid(o){ const v=new El('video'); Object.assign(v,{readyState:4,ended:false,paused:true,w:100,h:100,disablePictureInPicture:true,requests:0},o); v.requestPictureInPicture=async function(){ if(this.fail) throw Object.assign(new Error('x'),{name:this.fail}); this.requests++; return {}; }; return v; }
(async()=>{
  let doc=makeDoc([]); doc.pictureInPictureEnabled=false; ck(await pip(doc)==='error:unsupported','no PiP support is reported');
  doc=makeDoc([]); ck(await pip(doc)==='novideo','no video is reported');
  const small=vid({w:200,h:100}), big=vid({w:800,h:450}), playing=vid({w:100,h:50,paused:false}), ended=vid({ended:true,w:2000,h:2000}), notReady=vid({readyState:0,w:3000,h:3000});
  doc=makeDoc([small,big,ended,notReady]); ck(await pip(doc)==='opened'&&big.requests===1&&small.requests===0&&ended.requests===0&&notReady.requests===0,'the biggest usable video is chosen');
  ck(big.disablePictureInPicture===false,'a page that disabled PiP is overridden');
  doc=makeDoc([big,playing]); ck(await pip(doc)==='opened'&&playing.requests===1&&big.requests===1,'a playing video wins over a bigger paused one');
  doc=makeDoc([vid({})]); doc.pictureInPictureElement={}; let exited=false; doc.exitPictureInPicture=async()=>{exited=true;}; ck(await pip(doc)==='closed'&&exited,'a second call closes picture-in-picture');
  doc=makeDoc([vid({fail:'NotAllowedError'})]); ck(await pip(doc)==='error:NotAllowedError','a refusal is reported by name');
  console.log('\nchecks passed: '+pass+'  failed: '+fail);
  process.exit(fail?1:0);
})();
