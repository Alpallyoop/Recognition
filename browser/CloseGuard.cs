using System;
using System.Collections.Generic;
using System.Linq;

namespace Recognition.Browser
{
    // Pure rules for closing and starting (no WPF, files or network; executed by browser.tests).
    //  * StartupRules: what happens to your tabs at start-up (home page / continue where I left off / ask me), including the one-time
    //    choice made in the close window ("next time reopen these tabs" / "start fresh").
    //  * CloseRules: when closing Recognition needs a question first, and the plain-language warnings shown in it.

    internal static class StartupRules
    {
        public static string NormalizeMode(string? m)
        {
            var k = (m ?? "").Trim().ToLowerInvariant();
            return k == "restore" || k == "ask" ? k : "start";
        }

        public static string NormalizeNext(string? m)
        {
            var k = (m ?? "").Trim().ToLowerInvariant();
            return k == "restore" || k == "fresh" ? k : "";
        }

        public static string NormalizeCloseAsk(string? m) => string.Equals((m ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase) ? "off" : "on";

        // "restore" | "fresh" | "ask". A one-time choice from the close window wins; without saved tabs there is nothing to reopen or ask about.
        public static string Effective(string? mode, string? next, bool hasSession)
        {
            var n = NormalizeNext(next);
            if (n == "fresh") return "fresh";
            if (!hasSession) return "fresh";
            if (n == "restore") return "restore";
            var m = NormalizeMode(mode);
            return m == "restore" ? "restore" : m == "ask" ? "ask" : "fresh";
        }
    }

    internal sealed record CloseFacts(int Tabs, int RestorableTabs, int ActiveDownloads, IReadOnlyList<string> UnsavedPages, IReadOnlyList<string> PdfPages, bool AskSetting);
    internal sealed record ClosePlan(bool Prompt, bool OfferSession, IReadOnlyList<string> Warnings);

    internal static class CloseRules
    {
        public const int MaxListed = 4;

        // A page title or file name that is safe to show in a window: no control characters, at most 50 characters.
        public static string Clean(string? title, string fallback)
        {
            var t = new string((title ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (t.Length == 0) t = fallback;
            return t.Length > 50 ? t.Substring(0, 47) + "..." : t;
        }

        // Does this address point at a PDF (http, https or a local file)?
        public static bool LooksLikePdf(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeFile) return false;
            return u.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static string Names(IReadOnlyList<string> items)
        {
            var shown = items.Take(MaxListed).ToList();
            var s = string.Join(", ", shown);
            return items.Count > MaxListed ? s + " and " + (items.Count - MaxListed) + " more" : s;
        }

        // Downloads in progress always ask (closing stops them). Unsaved pages, open PDFs and "several tabs" ask only while the setting is on.
        public static ClosePlan Plan(CloseFacts f)
        {
            var w = new List<string>();
            if (f.ActiveDownloads > 0)
                w.Add(f.ActiveDownloads == 1 ? "1 download is still in progress. Closing stops it." : f.ActiveDownloads + " downloads are still in progress. Closing stops them.");
            if (f.AskSetting && f.UnsavedPages.Count > 0)
                w.Add("You have typed into " + (f.UnsavedPages.Count == 1 ? "a page" : f.UnsavedPages.Count + " pages") + " that asks to be kept open, so changes may not be saved: " + Names(f.UnsavedPages) + ".");
            if (f.AskSetting && f.PdfPages.Count > 0)
                w.Add((f.PdfPages.Count == 1 ? "A PDF is open: " : f.PdfPages.Count + " PDFs are open: ") + Names(f.PdfPages) + ". Changes made in the viewer (filled-in fields, highlights) are lost unless you saved the file.");
            bool offer = f.AskSetting && f.Tabs >= 2 && f.RestorableTabs >= 1;
            return new ClosePlan(w.Count > 0 || offer, offer, w);
        }

        // what the close window pre-selects: keep the tabs unless the person chose to start at the home page
        public static string DefaultNext(string? startupMode) => StartupRules.NormalizeMode(startupMode) == "start" ? "fresh" : "restore";

        // Runs in every web page (not in the browser's own pages). It only notes (a) that the page asked to be warned before closing and
        // (b) that the person typed or changed something after that. The answer is read once, when closing, and never leaves this computer.
        public const string PageScript = @"
(function(){try{
 if(window.__rb_unsaved)return;
 var reg=false,edited=false;
 var aw=window.addEventListener;
 window.addEventListener=function(t){if(t==='beforeunload')reg=true;return aw.apply(this,arguments);};
 try{var d=Object.getOwnPropertyDescriptor(window,'onbeforeunload');
  if(d&&d.set&&d.configurable){Object.defineProperty(window,'onbeforeunload',{configurable:true,enumerable:d.enumerable,get:d.get,set:function(v){if(typeof v==='function')reg=true;return d.set.call(this,v);}});}}catch(e){}
 function mark(e){if(e.isTrusted)edited=true;}
 document.addEventListener('input',mark,true);document.addEventListener('change',mark,true);
 Object.defineProperty(window,'__rb_unsaved',{value:function(){return reg&&edited;},enumerable:false});
}catch(e){}})();";

        public const string PageProbe = "(function(){try{return !!(window.__rb_unsaved&&window.__rb_unsaved());}catch(e){return false;}})()";
    }
}
