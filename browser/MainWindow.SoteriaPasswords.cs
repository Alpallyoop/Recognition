using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using SoteriaVault.Bridge;

namespace Recognition.Browser
{
    // SoteriaVault as the password manager (rules: SoteriaPasswords.cs; protocol: SoteriaBridgeProtocol.cs; proposal
    // docs\proposals\0006-soteriavault-password-manager.md). Kept apart from MainWindow.Soteria.cs on purpose: that file is the
    // read-only link page and stays free of processes and secrets (static check in _selftest_recognition_browser_governed_actions_v1).
    //
    //  * Off until you switch it on (SoteriaVault page) and SoteriaVault's contract allows it.
    //  * The key button lists the logins SoteriaVault has for this exact site (names only). Choosing one asks SoteriaVault, which
    //    asks you; the password goes into the page and is dropped. Nothing is auto-filled or auto-submitted.
    //  * Saving a login from the Passwords page goes to SoteriaVault, which tells you at once when it is a duplicate.
    //  * Recognition's own vault stays as it is and is used again whenever SoteriaVault is not the password manager.
    public partial class MainWindow
    {
        private bool _soteriaPasswords;                 // persisted in browser_settings.json as soteria_passwords
        private SoteriaStatus? _svPmStatus;
        private DateTime _svPmCheckedUtc = DateTime.MinValue;

        private bool SoteriaPmActive
        {
            get
            {
                if (!_soteriaPasswords || string.IsNullOrEmpty(_soteriaRoot)) return false;
                if (_svPmStatus == null || DateTime.UtcNow - _svPmCheckedUtc > TimeSpan.FromSeconds(20)) { _svPmStatus = SoteriaCheck(); _svPmCheckedUtc = DateTime.UtcNow; }
                return SoteriaBridge.IsPasswordManager(_svPmStatus);
            }
        }

        /// <summary>One request over the bridge, with its integration receipt. Never throws.</summary>
        private async Task<JsonObject> SvRequestAsync(JsonObject req, string what, int timeoutMs = 120000)
        {
            var reqSha = SoteriaPasswords.RedactedSha(req);
            JsonObject res;
            try
            {
                using var c = await SoteriaBridgeClient.TryConnectAsync(SoteriaPasswords.ClientName, AppVersionText(), 1500);
                if (c == null) res = new JsonObject { ["ok"] = false, ["error"] = "NOT_RUNNING" };
                else if (SoteriaBridgeProtocol.Str(c.Hello, "state") == "locked") res = new JsonObject { ["ok"] = false, ["error"] = "LOCKED" };
                else if (!SoteriaBridgeProtocol.Bool(c.Hello, "paired")) res = new JsonObject { ["ok"] = false, ["error"] = "NOT_PAIRED" };
                else res = await c.RequestAsync(req, timeoutMs);
            }
            catch { res = new JsonObject { ["ok"] = false, ["error"] = "DISCONNECTED" }; }
            finally { if (req.ContainsKey("password")) req["password"] = ""; if (req["entries"] is JsonArray ea) foreach (var en in ea.OfType<JsonObject>()) en["password"] = ""; }
            var (outcome, err) = SoteriaPasswords.Outcome(res);
            _ = WriteSoteriaExchangeReceiptAsync(SoteriaPasswords.ReceiptContract, reqSha, SoteriaPasswords.RedactedSha(res), outcome, err, "");
            _actions?.Append("soteria." + what, outcome + (err != null ? ":" + err : ""));
            return res;
        }

        private static string AppVersionText() => typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0";

        // ---- key button --------------------------------------------------------------------------------------------------

        private async Task UpdateKeyBtnSoteriaAsync(BrowserTab tab)
        {
            var url = PageAddressOf(tab);
            var res = await SvRequestAsync(new JsonObject { ["op"] = "list", ["page_url"] = url }, "list", 5000);
            if (Active != tab || PageAddressOf(tab) != url) return;
            bool has = SoteriaBridgeProtocol.Bool(res, "ok") && res["entries"] is JsonArray a && a.Count > 0;
            if (has) KeyBtn.Visibility = Visibility.Visible;
        }

        private async void SoteriaKeyMenu(BrowserTab a)
        {
            var url = PageAddressOf(a);
            var res = await SvRequestAsync(new JsonObject { ["op"] = "list", ["page_url"] = url }, "list", 8000);
            var cm = new System.Windows.Controls.ContextMenu();
            if (SoteriaBridgeProtocol.Bool(res, "ok") && res["entries"] is JsonArray arr)
            {
                foreach (var e in arr.OfType<JsonObject>())
                {
                    var id = SoteriaBridgeProtocol.Str(e, "id");
                    var mi = new System.Windows.Controls.MenuItem { Header = "Fill  " + SoteriaPasswords.MenuLabel(e) };
                    mi.Click += (_, __) => SoteriaFill(id, a, url);
                    cm.Items.Add(mi);
                }
                if (arr.Count == 0) cm.Items.Add(new System.Windows.Controls.MenuItem { Header = "SoteriaVault has no login for this site", IsEnabled = false });
            }
            else
            {
                cm.Items.Add(new System.Windows.Controls.MenuItem { Header = SoteriaPasswords.Explain(res), IsEnabled = false });
                if (SoteriaBridgeProtocol.Str(res, "error") == "NOT_RUNNING")
                {
                    var open = new System.Windows.Controls.MenuItem { Header = "Open SoteriaVault" };
                    open.Click += (_, __) => OpenSoteriaApp();
                    cm.Items.Add(open);
                }
            }
            EnsureCards();
            if (_cards.Entries.Count > 0 && Uri.TryCreate(url, UriKind.Absolute, out var pu) && pu.Scheme == Uri.UriSchemeHttps)
            {
                cm.Items.Add(new System.Windows.Controls.Separator());
                foreach (var card in _cards.Entries.OrderBy(x => x.Label).ThenBy(x => x.Last4))
                {
                    var cd = card;
                    var mi = new System.Windows.Controls.MenuItem { Header = "Pay with  " + cd.Brand + " " + CardRules.Mask(cd.Number) + (cd.Label.Length > 0 ? "  (" + cd.Label + ")" : "") };
                    mi.Click += (_, __) => CardFill(cd, a);
                    cm.Items.Add(mi);
                }
            }
            cm.Items.Add(new System.Windows.Controls.Separator());
            var manage = new System.Windows.Controls.MenuItem { Header = "Open SoteriaVault (passwords)" };
            manage.Click += (_, __) => OpenSoteriaApp();
            cm.Items.Add(manage);
            cm.PlacementTarget = KeyBtn; cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; cm.IsOpen = true;
        }

        private async void SoteriaFill(string entryId, BrowserTab target, string pageUrl)
        {
            Status("asking SoteriaVault (allow it in the SoteriaVault window)...");
            var res = await SvRequestAsync(new JsonObject { ["op"] = "fill", ["page_url"] = pageUrl, ["entry_id"] = entryId }, "fill");
            if (!SoteriaBridgeProtocol.Bool(res, "ok")) { Status(SoteriaPasswords.Explain(res)); return; }
            var user = SoteriaBridgeProtocol.Str(res, "username");
            var pass = SoteriaBridgeProtocol.Str(res, "password");
            res["password"] = "";
            try
            {
                // the page may have moved on while you were deciding: fill only the exact page that asked
                if (!_tabs.Contains(target) || target.Web?.CoreWebView2 == null || PageAddressOf(target) != pageUrl
                    || PasswordRules.OriginOf(pageUrl) is not { } o || !PasswordRules.MayFill(o, target.Web.Source?.ToString()))
                { Status("fill cancelled: the page changed"); return; }
                Tabs.SelectedItem = target.Item; ShowActiveWebView();
                var r = await target.Web.CoreWebView2.ExecuteScriptAsync(FillScript.Build(user, pass));
                Status(r.Contains("no-password-field") ? "no password field found on the page" : "filled from SoteriaVault for " + o + " (not submitted)");
            }
            catch (Exception ex) { Status("fill failed: " + ex.Message); }
            finally { pass = ""; }
        }

        // ---- Passwords page: save to SoteriaVault, move Recognition's logins ---------------------------------------------

        private async void SoteriaSaveFromPage(string origin, string username, string password, string note)
        {
            var url = "https://" + (PasswordRules.NormalizeOrigin(origin) ?? origin.Trim()) + "/";
            if (PasswordRules.OriginOf(url) == null) { PostToPwPage(new { type = "action", ok = false, text = "type the site as example.com" }); return; }
            var req = new JsonObject { ["op"] = "save", ["page_url"] = url, ["username"] = username, ["password"] = password, ["title"] = "" };
            var res = await SvRequestAsync(req, "save");
            if (SoteriaBridgeProtocol.Str(res, "outcome") == "needs_decision")
            {
                var choice = MessageBox.Show(this, SoteriaBridgeProtocol.Str(res, "message") + "\n\nYes: replace the saved password (the old one is kept in SoteriaVault's history).\nNo: keep both.\nCancel: do nothing.",
                    "SoteriaVault - duplicate login", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) { PostToPwPage(new { type = "action", ok = false, text = "not saved" }); return; }
                res = await SvRequestAsync(new JsonObject { ["op"] = "save", ["page_url"] = url, ["username"] = username, ["password"] = password, ["title"] = "", ["on_conflict"] = choice == MessageBoxResult.Yes ? "update" : "separate" }, "save");
            }
            password = "";
            if (!SoteriaBridgeProtocol.Bool(res, "ok")) { PostToPwPage(new { type = "action", ok = false, text = SoteriaPasswords.Explain(res) }); return; }
            var outcome = SoteriaBridgeProtocol.Str(res, "outcome");
            var msg = outcome switch
            {
                "saved" => "Saved in SoteriaVault.",
                "updated" => "Password updated in SoteriaVault.",
                "already_saved" => "Already saved in SoteriaVault: " + SoteriaBridgeProtocol.Str(res, "message"),
                _ => "SoteriaVault did not save it: " + SoteriaBridgeProtocol.Str(res, "message"),
            };
            var reused = res["duplicates"] is JsonArray d ? d.OfType<JsonObject>().FirstOrDefault(x => SoteriaBridgeProtocol.Str(x, "kind") == "ReusedPassword") : null;
            if (reused != null) msg += "  Note: " + SoteriaBridgeProtocol.Str(reused, "message");
            PostToPwPage(new { type = outcome is "saved" or "updated" ? "added" : "action", ok = true, text = msg });
            PostToPwPage(new { type = "action", ok = true, text = msg });
        }

        /// <summary>Moves every login Recognition saved itself into SoteriaVault (duplicates merged there), then offers to remove them here.</summary>
        private async void SoteriaMoveVault(Action<string> report)
        {
            EnsureVault();
            if (_vault.Entries.Count == 0) { report("Recognition has no saved logins to move."); return; }
            EnsurePin();
            if (_pin.IsSet && !EnsureUnlocked("Move your saved logins to SoteriaVault.")) return;
            var count = _vault.Entries.Count;
            var res = await SvRequestAsync(SoteriaPasswords.ImportRequest(_vault.Entries), "import");
            if (!SoteriaBridgeProtocol.Bool(res, "ok")) { report(SoteriaPasswords.Explain(res)); return; }
            var added = res["added"]?.GetValue<int>() ?? 0; var merged = res["merged"]?.GetValue<int>() ?? 0; var conflicts = res["conflicts"]?.GetValue<int>() ?? 0;
            var text = $"Moved {count} login(s) to SoteriaVault: {added} new, {merged} already there (merged), {conflicts} with a different password (SoteriaVault asks you to review them).";
            if (MessageBox.Show(this, text + "\n\nRemove them from Recognition now? SoteriaVault keeps them; Recognition will use SoteriaVault for passwords.",
                    "Recognition - passwords moved", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (var id in _vault.Entries.Select(e => e.Id).ToList()) _vault.Remove(id);
                _actions?.Append("vault.moved_to_soteriavault", count.ToString());
                text += " Removed from Recognition.";
            }
            report(text);
            SendPwSnapshot();
        }

        private void OpenSoteriaApp()
        {
            var exe = SoteriaPasswords.AppPath(_soteriaRoot, File.Exists);
            if (exe == null) { Status("SoteriaVault's app is not built in " + _soteriaRoot + " yet (run its Build button)"); return; }
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
                _actions?.Append("soteria.open_app");
                Status("starting SoteriaVault...");
            }
            catch (Exception ex) { Status("could not start SoteriaVault: " + ex.Message); }
        }

        // Messages from the SoteriaVault page (sv-pm-*) and the Passwords page (pw-sv-*).
        private void HandleSoteriaPmMessage(string msg, Action<object> post)
        {
            switch (msg)
            {
                case "sv-pm-on":
                case "sv-pm-off":
                    _soteriaPasswords = msg == "sv-pm-on"; _svPmStatus = null; SaveSettings();
                    _actions?.Append("soteria.password_manager", _soteriaPasswords ? "on" : "off");
                    _ = SendSoteriaPmState(post);
                    return;
                case "sv-pm-state": _ = SendSoteriaPmState(post); return;
                case "sv-pm-open": OpenSoteriaApp(); return;
                case "sv-pm-move": SoteriaMoveVault(t => post(new { type = "pm-action", text = t })); return;
            }
        }

        private async Task SendSoteriaPmState(Action<object> post)
        {
            var active = SoteriaPmActive;
            string conn = "off";
            if (active)
            {
                var res = await SvRequestAsync(new JsonObject { ["op"] = "status" }, "status", 4000);
                conn = SoteriaBridgeProtocol.Bool(res, "ok") ? (SoteriaBridgeProtocol.Str(res, "state") == "locked" ? "locked" : "ready") : SoteriaBridgeProtocol.Str(res, "error").ToLowerInvariant();
            }
            EnsureVault();
            post(new
            {
                type = "pm", on = _soteriaPasswords, allowed = _svPmStatus != null && SoteriaBridge.IsPasswordManager(_svPmStatus), active, conn,
                local = _vault.Entries.Count, app = SoteriaPasswords.AppPath(_soteriaRoot, File.Exists) != null,
            });
        }
    }
}
