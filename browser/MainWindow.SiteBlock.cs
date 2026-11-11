using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // User site blocking: the person's own list of sites not to open (separate from the ad/tracker filter and the governed host
    // blocklist). Rules are in SiteBlockRules (tested); this is the storage, the navigation enforcement, the blocked page, and the
    // Settings section. Blocks cover subdomains and are stored per profile in browser_settings.json (key blocked_sites).
    public partial class MainWindow
    {
        private readonly HashSet<string> _blockedSites = new(StringComparer.Ordinal);
        private string _lastBlockedHost = "";

        private void LoadSiteBlockSettings(JsonElement r)
        {
            _blockedSites.Clear();
            if (r.TryGetProperty("blocked_sites", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var e in a.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && SiteBlockRules.CleanEntry(e.GetString()) is { } h && _blockedSites.Count < SiteBlockRules.MaxEntries)
                        _blockedSites.Add(h);
        }

        private string SiteBlockSettingsJson()
            => J("blocked_sites") + ":[" + string.Join(",", _blockedSites.OrderBy(x => x, StringComparer.Ordinal).Select(h => JsonSerializer.Serialize(h))) + "],";

        // Called from OnNavStarting. Returns true (and shows the blocked page) when the destination is on the person's block list.
        private bool SiteBlockedNav(BrowserTab tab, string uri)
        {
            if (!SiteBlockRules.IsBlocked(uri, _blockedSites)) return false;
            _lastBlockedHost = SiteBlockRules.HostOf(uri) ?? "";
            if (!tab.Private) _actions?.Append("site.blocked", _lastBlockedHost);
            LoadInternal(tab, "blocked");
            return true;
        }

        private bool AddBlockedSite(string raw)
        {
            var h = SiteBlockRules.CleanEntry(raw);
            if (h == null || _blockedSites.Count >= SiteBlockRules.MaxEntries) return false;
            if (_blockedSites.Add(h)) { SaveSettings(); _actions?.Append("site.block_add", h); }
            return true;
        }

        private void RemoveBlockedSite(string raw)
        {
            var h = SiteBlockRules.CleanEntry(raw) ?? (raw ?? "").Trim().ToLowerInvariant();
            if (_blockedSites.Remove(h)) { SaveSettings(); _actions?.Append("site.block_remove", h); }
        }

        private void HandleSiteBlockMessage(BrowserTab tab, string msg)
        {
            if (msg.StartsWith("block-add:", StringComparison.Ordinal))
            {
                var raw = Uri.UnescapeDataString(msg.Substring("block-add:".Length));
                if (!AddBlockedSite(raw)) Status("that doesn't look like a site to block");
                else Status("blocked " + (SiteBlockRules.CleanEntry(raw) ?? raw));
            }
            else if (msg.StartsWith("block-remove:", StringComparison.Ordinal))
            {
                RemoveBlockedSite(Uri.UnescapeDataString(msg.Substring("block-remove:".Length)));
            }
            // reflect the change on whichever page asked
            if (tab.Internal == "settings") LoadInternal(tab, "settings");
            else if (tab.Internal == "blocked") LoadInternal(tab, "blocked");
        }

        // The page shown in place of a blocked site.
        private string BlockedHtml()
        {
            var host = Esc(_lastBlockedHost);
            var sb = new StringBuilder(PageHead).Append("<div class='wrap'>");
            sb.Append("<h1>Site blocked</h1>");
            sb.Append("<div class='muted'><b>" + host + "</b> is on your blocked-sites list, so Recognition didn't open it.</div>");
            sb.Append("<div class='row'><div><div class='t'>Change your mind?</div><div class='u'>You can unblock this site, or manage your whole list in Settings.</div></div>" +
                      "<div class='ts'><a class='btn' onclick=\"send('block-remove:" + Attr(_lastBlockedHost) + "')\">Unblock " + host + "</a> <a class='btn ghost' href='recognition:settings'>Manage blocked sites</a></div></div>");
            sb.Append("</div>").Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }

        // The Settings section: add a site, and the current list with remove buttons. Includes a one-click "block the site I'm on".
        private string BlockedSitesHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<h1 style='font-size:16px'>Blocked sites</h1>");
            var cur = (Active != null && !Active.IsInternal) ? (SiteBlockRules.HostOf(Active.CurrentUrl) ?? "") : "";
            sb.Append("<div class='row'><div><div class='t'>Block a site</div><div class='u'>Recognition won't open it (or its subdomains). This is your own list — separate from ad and tracker blocking.</div>" +
                      "<div style='margin-top:8px;display:flex;gap:8px'><input id='bs' class='in' placeholder='example.com' style='flex:1'><a class='btn' onclick=\"var v=document.getElementById('bs').value.trim();if(v)send('block-add:'+encodeURIComponent(v))\">Block</a></div></div>" +
                      (cur.Length > 0 && !_blockedSites.Contains(cur) ? "<div class='ts'><a class='btn ghost' onclick=\"send('block-add:" + Attr(cur) + "')\">Block " + Esc(cur) + "</a></div>" : "") + "</div>");
            if (_blockedSites.Count == 0)
                sb.Append("<div class='muted' style='margin:6px 0 18px'>No sites blocked yet.</div>");
            else
                foreach (var h in _blockedSites.OrderBy(x => x, StringComparer.Ordinal))
                    sb.Append("<div class='row'><div><div class='t'>" + Esc(h) + "</div><div class='u'>blocked, including subdomains</div></div><div class='ts'><a class='btn ghost' onclick=\"send('block-remove:" + Attr(h) + "')\">Remove</a></div></div>");
            return sb.ToString();
        }
    }
}
