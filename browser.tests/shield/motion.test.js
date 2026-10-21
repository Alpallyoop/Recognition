// node motion.test.js : runs the start page animation (the vendored ThemeForge runtime plus the boot script that StartMotion.Inject
// writes) in a small simulated browser. The pages are written by the C# tests (dotnet run -c Release --project browser.tests) into
// browser.tests\bin\Release\net8.0, so run that first.
const fs=require('fs'),path=require('path'),vm=require('vm'),crypto=require('crypto');
const bin=path.join(__dirname,'..','bin','Release','net8.0');
const runtimePath=path.join(__dirname,'..','..','browser','shield','motion.js');
let pass=0,fail=0; const ck=(c,l)=>{ if(c){pass++; console.log('  ok  - '+l);} else {fail++; console.log('  FAIL- '+l);} };

const runtime=fs.readFileSync(runtimePath);
ck(crypto.createHash('sha256').update(runtime).digest('hex')==='074198d978e3f01bb4729775c5643659dba2b06906838152591c1f992f2679ce','the vendored runtime is ThemeForge motion runtime 2.0.0 byte-for-byte');

function load(html, opts){
  const scripts=[...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m=>m[1]);
  const calls={fill:0,draw:0,removed:false,raf:0,net:0,log:[]};
  const ctx=new Proxy({},{get:(t,k)=>{
      if(k==='createRadialGradient'||k==='createLinearGradient') return ()=>({addColorStop(){}});
      if(k==='fillRect') return (...a)=>{calls.fill++; calls.log.push('R'+a.map(x=>Math.round(x)).join(','));};
      if(k==='drawImage') return (...a)=>{calls.draw++; calls.log.push('I'+a.slice(1).map(x=>Math.round(x)).join(','));};
      if(k==='arc') return (...a)=>calls.log.push('A'+a.slice(0,3).map(x=>Math.round(x)).join(','));
      return t[k]!==undefined?t[k]:()=>{}; },
    set:(t,k,v)=>{t[k]=v; if(k==='fillStyle'&&typeof v==='string') calls.log.push('F'+v); return true;}});
  const canvas={clientWidth:800,clientHeight:600,width:0,height:0,getContext:()=>ctx,parentNode:{removeChild(){calls.removed=true;}}};
  const win={devicePixelRatio:1,innerWidth:800,innerHeight:600,
    matchMedia:()=>({matches:!!opts.reduced}),
    requestAnimationFrame:f=>{calls.raf++; if(calls.raf<5) setTimeout(()=>f(16*calls.raf),0); return calls.raf;},
    cancelAnimationFrame(){}, addEventListener(){}, removeEventListener(){},
    fetch(){calls.net++;}, XMLHttpRequest:function(){calls.net++;}, WebSocket:function(){calls.net++;}};
  win.window=win;
  win.document={hidden:false,addEventListener(){},getElementById:id=>id==='rec-motion'?canvas:null,body:{}};
  win.getComputedStyle=()=>({backgroundImage:opts.bg||''});
  win.Image=function(){const im=this; im.naturalWidth=100; im.naturalHeight=50; Object.defineProperty(im,'src',{set(v){im._src=v; calls.src=v; setTimeout(()=>opts.imgFail?im.onerror():im.onload(),0);}});};
  const c=vm.createContext(win);
  for(const s of scripts) vm.runInContext(s,c);
  return new Promise(r=>setTimeout(()=>r(calls),80));
}

(async()=>{
  const darkFile=path.join(bin,'motion_page_dark.html'), picFile=path.join(bin,'motion_page_picture.html');
  if(!fs.existsSync(darkFile)||!fs.existsSync(picFile)){ console.log('  FAIL- the pages written by the C# tests are missing (run dotnet run -c Release --project browser.tests first)'); process.exit(1); }
  const dark=fs.readFileSync(darkFile,'utf8'), pic=fs.readFileSync(picFile,'utf8');
  ck(dark.includes(runtime.toString('utf8')),'the page carries the runtime exactly as vendored');
  const a=await load(dark,{});
  ck(a.raf>=4&&a.fill>0&&!a.removed&&a.net===0,'aurora: the animation runs frame after frame and makes no network request');
  const a2=await load(dark,{});
  ck(a.log.join('|')===a2.log.join('|'),'aurora: the same recipe draws exactly the same frames (deterministic)');
  const b=await load(dark,{reduced:true});
  ck(b.raf===0&&b.fill>0&&!b.removed,'reduce animations: one still frame is drawn and no animation runs');
  const bg='linear-gradient(rgba(10, 12, 16, 0.38), rgba(10, 12, 16, 0.58)), url("data:image/jpeg;base64,QUJD+/=")';
  const p=await load(pic,{bg});
  ck(p.draw>0&&!p.removed&&p.src==='data:image/jpeg;base64,QUJD+/=','picture: the runtime draws the picture taken from the page\'s own CSS background');
  const q=await load(pic,{bg:'url("https://evil.example/x.jpg")'});
  ck(q.removed&&q.draw===0&&q.src===undefined,'picture: anything but an embedded JPEG is not loaded; the canvas is removed and the CSS background shows');
  const f=await load(pic,{bg:'url("data:image/jpeg;base64,QUJD")',imgFail:true});
  ck(f.removed,'picture: a picture that fails to decode removes the canvas (CSS fallback)');
  const g=await load(dark.replace(/<script>\/\* ThemeForge[\s\S]*?<\/script>/,''),{});
  ck(g.removed&&g.fill===0,'without the runtime the canvas is removed (CSS fallback)');
  console.log('\nchecks passed: '+pass+'  failed: '+fail);
  process.exit(fail?1:0);
})();
