using System;

namespace Recognition.Browser
{
    // The governed-agent decision core (proposal 0012). Pure logic, no WPF/WebView2: it decides whether an agent request may run,
    // nothing more. Every transport (hosted MCP server, CDP bridge, native-messaging host) and the embedded assistant route EVERY
    // request through AgentRules.Decide before anything touches a page. There is no path from a transport to WebView2 that skips this.
    //
    // The one rule behind all of it: the agent is LESS privileged than the user, never more. So the pure layer refuses, with no mode
    // and no grant able to override it:
    //   * Recognition's own pages (the `recognition:` scheme) — the agent must never drive settings, the vault, setup or export, which
    //     is how it would grant itself rights, read saved logins, or export credentials;
    //   * private tabs — they leave no trace by definition, so the agent cannot see or act in them;
    //   * the vault / saved-login capabilities — the agent never reads or fills a credential;
    //   * local files (`file:`) and anything that is not http(s).
    // Beyond those hard refusals, an origin must be GRANTED before the agent reads or acts there, and RunScript / FileUpload ask on
    // EVERY call regardless of mode. Developer mode only skips the repeat per-site prompt for an already-granted origin; it never
    // removes a refusal, never silences RunScript/FileUpload, and is itself recorded.

    internal enum AgentCapability
    {
        // read / observe
        TabsList, ReadPage, GetText, Find, Screenshot, ReadConsole, ReadNetwork,
        // act on a page
        Navigate, Click, Type, Scroll, FormInput, FileUpload, RunScript,
        // act on the tab set / window
        TabCreate, TabClose, Resize
    }

    internal enum AgentVerdict
    {
        Allow,        // decided: the executor may run it
        NeedConsent,  // the origin (or this call) needs an explicit grant/confirm first
        Deny          // refused outright; never becomes Allow
    }

    internal readonly record struct AgentRequest(
        AgentCapability Capability,
        string? Url,            // the page the action targets (the tab's current URL)
        bool IsInternal,        // the tab is one of Recognition's own `recognition:` pages
        bool IsPrivate,         // the tab is a private tab
        bool OriginGranted,     // this origin has been granted to the agent this session
        bool PerCallConfirmed); // the user confirmed THIS call (only meaningful for RunScript / FileUpload)

    internal static class AgentRules
    {
        // Capabilities that change page or tab state (everything else only observes).
        public static bool IsActing(AgentCapability c) => c switch
        {
            AgentCapability.TabsList or AgentCapability.ReadPage or AgentCapability.GetText or AgentCapability.Find
                or AgentCapability.Screenshot or AgentCapability.ReadConsole or AgentCapability.ReadNetwork => false,
            _ => true
        };

        // Capabilities that are powerful enough to ask on every single call, in any mode.
        public static bool NeedsPerCallConfirm(AgentCapability c) => c is AgentCapability.RunScript or AgentCapability.FileUpload;

        // Capabilities that work on the tab set / window and so are not tied to one page origin.
        static bool IsWindowLevel(AgentCapability c) => c is AgentCapability.TabsList or AgentCapability.TabCreate or AgentCapability.TabClose or AgentCapability.Resize;

        // A target the agent is NEVER allowed to touch, whatever the mode or grant.
        public static bool IsForbiddenTarget(string? url, bool isInternal, bool isPrivate)
        {
            if (isInternal || isPrivate) return true;
            if (string.IsNullOrEmpty(url)) return false;   // window-level actions carry no URL; the per-tab flags above still guard them
            if (url.StartsWith("recognition:", StringComparison.OrdinalIgnoreCase)) return true;   // defence in depth beyond IsInternal
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
            var scheme = SchemeOf(url);
            // only ever drive real web pages
            return scheme is not ("http" or "https");
        }

        static string SchemeOf(string url)
        {
            var i = url.IndexOf(':');
            return i <= 0 ? "" : url.Substring(0, i).ToLowerInvariant();
        }

        /// <summary>
        /// The whole decision. <paramref name="agentEnabled"/> is the master switch (off by default, per launch). <paramref name="devMode"/>
        /// only skips the repeat per-origin prompt for an already-granted origin. Returns Allow only when it is safe to run now.
        /// </summary>
        public static AgentVerdict Decide(bool agentEnabled, bool devMode, in AgentRequest r)
        {
            // 1. master switch
            if (!agentEnabled) return AgentVerdict.Deny;

            // 2. hard refusals — no mode, no grant, ever. Window-level actions (no URL) are judged only by the per-tab flags.
            if (IsWindowLevel(r.Capability))
            {
                // these don't target a page, but must still never operate from inside a private/internal context
                if (r.IsPrivate || r.IsInternal) return AgentVerdict.Deny;
            }
            else
            {
                if (IsForbiddenTarget(r.Url, r.IsInternal, r.IsPrivate)) return AgentVerdict.Deny;
            }

            // 3. the most powerful acts ask every time, in every mode
            if (NeedsPerCallConfirm(r.Capability))
                return r.PerCallConfirmed ? AgentVerdict.Allow : AgentVerdict.NeedConsent;

            // 4. window/tab-set actions need only the master switch (already on); they carry no origin to grant
            if (IsWindowLevel(r.Capability)) return AgentVerdict.Allow;

            // 5. page reads and ordinary acts need the origin granted. Dev mode does not invent a grant — it only means that, once an
            //    origin is granted, the prompt isn't repeated. A missing grant is still NeedConsent in both modes.
            if (r.OriginGranted) return AgentVerdict.Allow;
            return AgentVerdict.NeedConsent;
        }

        // The receipt line for an agent action: capability, origin and outcome only. Never the typed text, the script body, the page
        // content, a cookie or a token. Window-level actions have no origin.
        public static string Receipt(AgentCapability cap, string? url, bool ok)
        {
            var origin = PasswordRules.OriginOf(url) ?? (IsWindowLevel(cap) ? "window" : "unknown");
            return "agent." + cap.ToString().ToLowerInvariant() + ":" + origin + ":" + (ok ? "ok" : "fail");
        }
    }
}
