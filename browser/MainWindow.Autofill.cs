using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Recognition.Browser
{
    // Autofill and the save-password offer (rules: AutofillRules.cs; proposal 0010). This is the WPF/WebView2 glue.
    //
    // Autofill is host-driven: on a completed navigation to an exact-origin https page with exactly one saved login, Recognition
    // injects the same fill used by the key button (visible fields only, never submitted). It never runs when the vault is locked,
    // never from SoteriaVault (which asks you per fill), and never guesses between several logins.
    //
    // The save offer is driven by a tiny content script that notices a login being submitted and hands the entered username and
    // password to the host. The host shows a native bar (never a web page) offering to Save / Update, "Never for this site", or
    // dismiss. Nothing is saved without that click. Captured values are checked to come from the page's own top-frame https origin.
    public partial class MainWindow
    {
        private bool _autofillLogins = true;      // fill the one saved login on load
        private bool _offerSaveLogins = true;     // offer to save a login the page submits
        private readonly HashSet<string> _neverSaveOrigins = new(StringComparer.Ordinal);

        private (string Origin, string Username, string Password, SaveKind Kind)? _pendingSave;
        private string _saveBarOrigin = "";
        private DispatcherTimer? _saveBarTimer;

        // Runs in every top-level document; posts the entered login when one is submitted. It only reads fields the page already
        // holds and only messages Recognition (never the network). The host ignores it unless "offer to save" is on.
        private const string PwCaptureScript = @"
(function(){
  if (window.top !== window) return;
  var h = location.hostname;
  if (location.protocol !== 'https:' && h !== 'localhost' && h !== '127.0.0.1' && !/\.localhost$/.test(h)) return;
  function vis(e){var r=e.getBoundingClientRect();var s=getComputedStyle(e);return r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none'&&!e.disabled;}
  function grab(){
    var pws=[].slice.call(document.querySelectorAll('input[type=password]')).filter(function(e){return e.value&&vis(e);});
    if(!pws.length) return null;
    var pw=pws[0], scope=pw.form||document;
    var us=[].slice.call(scope.querySelectorAll('input')).filter(function(e){var t=(e.type||'text').toLowerCase();return (t==='text'||t==='email'||t==='tel')&&e.value&&vis(e);});
    var uf=null; for(var i=0;i<us.length;i++){ if(us[i].compareDocumentPosition(pw)&Node.DOCUMENT_POSITION_FOLLOWING) uf=us[i]; }
    return {u: uf?uf.value:'', p: pw.value};
  }
  var last='';
  function offer(){ try{ var g=grab(); if(!g||!g.p) return; var k=g.u+'\n'+g.p; if(k===last) return; last=k;
    window.chrome.webview.postMessage('pwcap:'+encodeURIComponent(JSON.stringify(g))); }catch(e){} }
  document.addEventListener('submit', function(){ offer(); }, true);
  document.addEventListener('click', function(ev){ var t=ev.target; if(t&&t.closest&&t.closest('button,[type=submit],input[type=submit]')) setTimeout(offer,60); }, true);
  window.addEventListener('pagehide', function(){ offer(); }, true);
})();";

        private void LoadAutofillSettings(JsonElement r)
        {
            if (r.TryGetProperty("autofill_logins", out var a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)) _autofillLogins = a.GetBoolean();
            if (r.TryGetProperty("offer_save_logins", out var s) && (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False)) _offerSaveLogins = s.GetBoolean();
            _neverSaveOrigins.Clear();
            if (r.TryGetProperty("never_save_origins", out var n) && n.ValueKind == JsonValueKind.Array)
                foreach (var o in n.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.String && PasswordRules.NormalizeOrigin(o.GetString()) is { } no && _neverSaveOrigins.Count < 2000) _neverSaveOrigins.Add(no);
        }

        private string AutofillSettingsJson()
        {
            var list = string.Join(",", _neverSaveOrigins.OrderBy(x => x, StringComparer.Ordinal).Select(o => JsonSerializer.Serialize(o)));
            return J("autofill_logins") + ":" + (_autofillLogins ? "true" : "false") + "," +
                   J("offer_save_logins") + ":" + (_offerSaveLogins ? "true" : "false") + "," +
                   J("never_save_origins") + ":[" + list + "],";
        }

        // ---- autofill on navigation --------------------------------------------------------------------------------------
        private async void TryAutofill(BrowserTab tab)
        {
            try
            {
                if (!_autofillLogins || SoteriaPmActive || tab.IsInternal || tab.Web?.CoreWebView2 == null) return;
                var url = tab.Web.CoreWebView2.Source;
                if (!PasswordRules.MayFill(PasswordRules.OriginOf(url) ?? "\0", url)) return;   // exact https origin only
                EnsureVault();
                var matches = _vault.ForPage(url);
                if (!AutofillRules.ShouldAutofill(_autofillLogins, SoteriaPmActive, true, matches.Count, _pin.IsSet, _pin.IsUnlocked(DateTime.UtcNow))) return;
                var e = matches[0];
                var res = await tab.Web.CoreWebView2.ExecuteScriptAsync(FillScript.Build(e.Username, e.Password));
                if (res != null && res.Contains("no-password-field")) return;   // nothing to fill; don't record a fill
                if (_pin.IsSet) _pin.Touch(DateTime.UtcNow);
                if (!tab.Private) _actions?.Append("vault.autofill", e.Origin + ":" + e.Id);
            }
            catch { }
        }

        // ---- save offer ---------------------------------------------------------------------------------------------------
        private void OnPwCapture(BrowserTab tab, string source, string payload)
        {
            try
            {
                if (!_offerSaveLogins || tab.Private || tab.IsInternal) return;
                // the capture must come from the page the tab is actually showing, over https
                var pageUrl = tab.Web?.CoreWebView2?.Source;
                var origin = PasswordRules.OriginOf(pageUrl);
                if (origin == null || PasswordRules.OriginOf(source) != origin) return;
                if (!PasswordRules.MayFill(origin, pageUrl)) return;

                using var d = JsonDocument.Parse(Uri.UnescapeDataString(payload));
                string F(string k) => d.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var user = F("u"); var pass = F("p");
                if (!PasswordRules.CleanField(user, 256) || !PasswordRules.CleanField(pass, 256)) return;

                EnsureVault();
                IReadOnlyList<(string, string)> existing = SoteriaPmActive
                    ? Array.Empty<(string, string)>()   // SoteriaVault owns its store; offer and let it dedupe on save
                    : _vault.ForPage(pageUrl).Select(x => (x.Username, x.Password)).ToList();
                var kind = AutofillRules.ClassifySave(_offerSaveLogins, tab.Private, _neverSaveOrigins.Contains(origin), true, origin, user, pass, existing);
                if (SoteriaPmActive && kind == SaveKind.None && !_neverSaveOrigins.Contains(origin) && pass.Length > 0) kind = SaveKind.New;
                if (kind is SaveKind.None or SaveKind.Duplicate or SaveKind.Blocked) return;

                _pendingSave = (origin, user, pass, kind);
                ShowSaveBar(origin, AutofillRules.SaveBarText(kind, origin, user), kind == SaveKind.Update ? "Update" : "Save");
            }
            catch { }
        }

        private void ShowSaveBar(string origin, string text, string primaryLabel)
        {
            _saveBarOrigin = origin;
            SaveBarText.Text = text;
            SaveBarSave.Content = primaryLabel;
            SaveBar.Visibility = Visibility.Visible;
            _saveBarTimer?.Stop();
            _saveBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            _saveBarTimer.Tick += (_, __) => HideSaveBar();
            _saveBarTimer.Start();
        }

        private void HideSaveBar()
        {
            _saveBarTimer?.Stop(); _saveBarTimer = null;
            _pendingSave = null; _saveBarOrigin = "";
            try { SaveBar.Visibility = Visibility.Collapsed; } catch { }
        }

        // hide the bar only when the page leaves the origin the offer was for (a same-origin post-login redirect keeps it)
        private void SaveBarOnNav(string? url)
        {
            if (SaveBar.Visibility != Visibility.Visible) return;
            if (PasswordRules.OriginOf(url) != _saveBarOrigin) HideSaveBar();
        }

        private void SaveBarSave_Click(object sender, RoutedEventArgs e)
        {
            var p = _pendingSave; if (p == null) { HideSaveBar(); return; }
            var (origin, user, pass, kind) = p.Value;
            if (SoteriaPmActive) { SoteriaSaveFromPage(origin, user, pass, ""); HideSaveBar(); return; }
            if (!EnsureUnlocked("Save the login for " + origin + ".")) return;   // may prompt for the PIN
            string? err;
            if (kind == SaveKind.Update)
            {
                var ex = _vault.ForPage("https://" + origin + "/").FirstOrDefault(x => string.Equals(x.Username, user, StringComparison.Ordinal));
                if (ex == null) { err = _vault.Add(origin, user, pass, "", DateTime.UtcNow, out var a2); if (err == null) _actions?.Append("vault.add", a2!.Origin + ":" + a2.Id); }
                else { err = _vault.Update(ex.Id, user, pass, ex.Note, DateTime.UtcNow); if (err == null) _actions?.Append("vault.update", ex.Origin + ":" + ex.Id); }
            }
            else
            {
                err = _vault.Add(origin, user, pass, "", DateTime.UtcNow, out var added);
                if (err == null) _actions?.Append("vault.add", added!.Origin + ":" + added.Id);
            }
            Status(err ?? (kind == SaveKind.Update ? "updated the saved password for " + origin : "saved the login for " + origin));
            HideSaveBar();
        }

        private void SaveBarNever_Click(object sender, RoutedEventArgs e)
        {
            if (_saveBarOrigin.Length > 0) { _neverSaveOrigins.Add(_saveBarOrigin); SaveSettings(); Status("Recognition won't offer to save logins for " + _saveBarOrigin + " (change this in Settings)"); }
            HideSaveBar();
        }

        private void SaveBarDismiss_Click(object sender, RoutedEventArgs e) => HideSaveBar();
    }
}
