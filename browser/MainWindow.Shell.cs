using System;
using System.Linq;
using System.Text;

namespace Recognition.Browser
{
    // The one frame every internal page shares: a left menu of all the pages in four groups, then the page itself in a
    // centred column. Pages with three or more sections (Settings, Shield, Network...) show them as tabs instead of one
    // long scroll. The pages themselves are unchanged; the frame is added around them here, so a new page gets it for free.
    public partial class MainWindow
    {
        private string _shellPage = "";   // the internal page being built; empty = no frame (viewer, result pages)

        private static readonly (string Group, (string Id, string Label)[] Items)[] ShellMenu =
        {
            ("Browse",     new[] { ("overview", "Overview"), ("bookmarks", "Bookmarks"), ("history", "History"), ("downloads", "Downloads") }),
            ("Protect",    new[] { ("shield", "Shield"), ("passwords", "Passwords"), ("passkeys", "Passkeys"), ("vpn", "VPN and proxy"), ("network", "Network") }),
            ("Tools",      new[] { ("tools", "Tools"), ("extensions", "Extensions"), ("setup", "Setup snapshot"), ("soteria", "SoteriaVault") }),
            ("Personalize", new[] { ("customize", "Customize"), ("settings", "Settings") }),
        };

        private bool ShellOn => _shellPage.Length > 0;

        private string ShellOpen()
        {
            if (!ShellOn) return "<div class='wrap'>";
            var sb = new StringBuilder("<div class='shell'><nav><div class='brand'>Recognition</div>");
            foreach (var (group, items) in ShellMenu)
            {
                sb.Append("<div class='grp'>").Append(group).Append("</div>");
                foreach (var (id, label) in items)
                    sb.Append("<a class='nav").Append(id == _shellPage ? " on" : "").Append("' href='recognition:").Append(id).Append("'>").Append(label).Append("</a>");
            }
            sb.Append("</nav><main><div class='wrap'>");
            return sb.ToString();
        }

        private string ShellClose() => ShellOn ? "</div></main></div><script>" + ShellJs + "</script></body></html>" : "</div></body></html>";

        // appended after the base stylesheet, so it wins; colours come from variables the light theme can replace
        private const string ShellCss =
            ":root{--nav:#14171c;--navline:#232730;--navtxt:#aab1bd;--navon:#fff;--navonbg:#222833;--card:#1d2129;--cardline:#272c36;--acc:#4c9bf0;--tabtxt:#98a0ad}" +
            "*{box-sizing:border-box}" +
            "::-webkit-scrollbar{width:10px;height:10px}::-webkit-scrollbar-thumb{background:#343a46;border-radius:6px}::-webkit-scrollbar-track{background:transparent}" +
            ".shell{display:flex;height:100vh}" +
            "nav{width:224px;flex:none;background:var(--nav);border-right:1px solid var(--navline);padding:22px 12px;overflow-y:auto}" +
            "nav .brand{font-size:15px;font-weight:600;color:var(--navon);padding:2px 12px 14px;letter-spacing:.2px}" +
            "nav .grp{font-size:10.5px;text-transform:uppercase;letter-spacing:1.1px;color:#6c7380;padding:18px 12px 6px}" +
            "nav a.nav{display:block;padding:8px 12px;margin:1px 0;border-radius:8px;color:var(--navtxt);text-decoration:none;font-size:13.5px}" +
            "nav a.nav:hover{background:var(--navonbg);color:var(--navon)}" +
            "nav a.nav.on{background:var(--navonbg);color:var(--navon);box-shadow:inset 3px 0 0 var(--acc)}" +
            "main{flex:1;min-width:0;overflow-y:auto}" +
            ".shell .wrap{max-width:860px;margin:0 auto;padding:36px 40px 80px}" +
            ".shell .wrap>h1:not([style]){font-size:26px;font-weight:600;margin:0 0 6px}" +
            ".shell .muted{font-size:13px;line-height:1.5;margin-bottom:26px;max-width:680px}" +
            ".shell h1[style]{font-size:12px!important;font-weight:600;text-transform:uppercase;letter-spacing:1.1px;color:#8a92a0;margin:34px 0 12px!important}" +
            ".shell .row{background:var(--card);border-color:var(--cardline);border-radius:12px;padding:15px 18px;margin-bottom:10px;align-items:center}" +
            ".shell .row .t{font-size:14px;font-weight:500}.shell .row .u{margin-top:3px;line-height:1.45;font-size:12px}" +
            ".shell .btn{padding:7px 14px;border-radius:8px;font-size:12.5px}" +
            ".shell .btn.ghost:hover{background:#262b35}" +
            ".shell input[type=text],.shell input[type=search],.shell input[type=password],.shell input:not([type]),.shell select,.shell textarea{background:#12151a;color:#e8e8e8;border:1px solid #2c323d;border-radius:8px;padding:8px 11px;font-size:13px}" +
            ".shell .kv{padding:12px 0}" +
            ".shell .wrap{overflow-wrap:anywhere}" +
            ".shell .row{flex-wrap:wrap}" +
            ".shell .row>div:first-child{flex:1 1 260px;min-width:0}" +
            ".shell .row>.ts{flex:0 1 auto;display:flex;flex-wrap:wrap;gap:6px;justify-content:flex-end;align-items:center;max-width:100%}" +
            ".shell .btn{margin-left:0}" +
            ".shell .row a.t+.u,.shell .u[style*='ellipsis']{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
            ".shell label{display:inline-flex;align-items:center;gap:8px;font-size:13px;color:#c7ccd4}" +
            ".shell input[type=color]{width:40px;height:30px;padding:2px;border-radius:8px;background:#12151a;border:1px solid #2c323d;cursor:pointer}" +
            ".shell table{width:100%;border-collapse:collapse}.shell img{max-width:100%}.shell pre,.shell code{white-space:pre-wrap;overflow-wrap:anywhere}" +
            ".shell input[type=text],.shell input[type=search],.shell input[type=password],.shell input:not([type]){max-width:100%;min-width:0}" +
            ".tabs{display:flex;flex-wrap:wrap;gap:6px;margin:4px 0 24px;border-bottom:1px solid var(--cardline);padding-bottom:12px;position:sticky;top:0;background:#191c22;z-index:5;padding-top:8px}" +
            ".tabs a{padding:7px 14px;border-radius:999px;font-size:12.5px;color:var(--tabtxt);cursor:pointer;text-decoration:none;border:1px solid transparent}" +
            ".tabs a:hover{color:#fff;background:var(--navonbg)}" +
            ".tabs a.on{background:var(--acc);color:#fff}" +
            "section.sect{display:none}section.sect.on{display:block}section.sect>h1:first-child{display:none}" +
            "@media(max-width:760px){.shell{flex-direction:column;height:auto}nav{width:auto;display:flex;flex-wrap:wrap;gap:2px;padding:10px}nav .grp,nav .brand{display:none}main{overflow:visible}.shell .wrap{padding:20px 16px}}";

        // light theme: the same frame in light colours (appended to the appearance css)
        internal const string ShellLightCss =
            ":root{--nav:#fff;--navline:#e0e3e9;--navtxt:#4a5160;--navon:#14171c;--navonbg:#eceff4;--card:#fff;--cardline:#dfe2e8;--tabtxt:#566070}" +
            ".tabs{background:#f4f5f7!important}.shell input,.shell select,.shell textarea{background:#fff!important;color:#1d2026!important;border-color:#c6cbd4!important}.shell h1[style]{color:#5d6470!important}";

        private const string ShellJs =
            "(function(){try{" +
            "var wrap=document.querySelector('.wrap');if(!wrap)return;" +
            "var kids=Array.prototype.slice.call(wrap.children);" +
            "var secs=kids.filter(function(e){return e.tagName==='H1'&&/font-size:\\s*16px/.test(e.getAttribute('style')||'')});" +
            "if(secs.length<3)return;" +
            "var groups=[],cur=null;" +
            "kids.forEach(function(e){if(secs.indexOf(e)>=0){cur={title:e.textContent,els:[e]};groups.push(cur);}else if(cur){cur.els.push(e);}});" +
            "var key='recsec:'+document.title+':';var start=0;" +
            "if(window.name.indexOf(key)===0){var n=parseInt(window.name.substring(key.length),10);if(n>=0&&n<groups.length)start=n;}" +
            "var bar=document.createElement('div');bar.className='tabs';" +
            "var sections=[],links=[];" +
            "groups.forEach(function(g,i){var s=document.createElement('section');s.className='sect';wrap.insertBefore(s,g.els[0]);g.els.forEach(function(el){s.appendChild(el);});sections.push(s);" +
            "var a=document.createElement('a');a.textContent=g.title;a.onclick=function(){show(i);};links.push(a);bar.appendChild(a);});" +
            "function show(i){sections.forEach(function(s,j){s.className='sect'+(j===i?' on':'');});links.forEach(function(a,j){a.className=j===i?'on':'';});try{window.name=key+i;}catch(e){}var m=document.querySelector('main');if(m)m.scrollTop=0;}" +
            "wrap.insertBefore(bar,sections[0]);show(start);" +
            "}catch(e){}})();";
    }
}
