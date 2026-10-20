using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Recognition.Browser
{
    // Closing and starting.
    //  * Closing asks first when something would be lost: a download in progress, a page you typed into that asked to be kept open,
    //    a PDF that is open, or several tabs (then you choose what happens next time: reopen these tabs, or start fresh).
    //  * Settings > Tabs, startup and tools: start with the home page / continue where I left off / ask me, and whether to ask when closing
    //    with several tabs. Nothing here sends anything anywhere; tab lists stay encrypted on this computer and never include private tabs.
    public partial class MainWindow
    {
        private string _closeAsk = "on";     // persisted as close_ask: on | off
        private string _nextStart = "";       // persisted as next_start: "" | restore | fresh (chosen in the close window, used once)
        private bool _closeConfirmed, _closePrompting;

        // ---- closing -----------------------------------------------------------------------------------------------------------
        // Returns true when the close must wait for the question; the window is closed again by ConfirmCloseAsync.
        private bool HoldCloseForQuestion(System.ComponentModel.CancelEventArgs e)
        {
            if (_closeConfirmed || !_preflightOk || !_startupDone || _tabs.Count == 0) return false;
            e.Cancel = true;
            if (!_closePrompting) _ = ConfirmCloseAsync();
            return true;
        }

        private async Task ConfirmCloseAsync()
        {
            _closePrompting = true;
            await Task.Yield();   // leave the Closing event first: calling Close() inside it throws, which made the first click do nothing
            bool proceed = true;
            try
            {
                var facts = await GatherCloseFactsAsync();
                var plan = CloseRules.Plan(facts);
                if (plan.Prompt)
                {
                    var r = ShowClosePrompt(plan, CloseRules.DefaultNext(_startupMode));
                    proceed = r.Close;
                    if (proceed && plan.OfferSession)
                    {
                        _nextStart = r.Next;
                        if (r.DontAsk) _closeAsk = "off";
                        _actions?.Append("close.choice", r.Next);
                    }
                    else if (!proceed) _actions?.Append("close.cancelled", "");
                    SaveSettings();
                }
            }
            catch { proceed = true; }   // a failure here must never trap the person in the window
            finally { _closePrompting = false; }
            if (proceed) { _closeConfirmed = true; Close(); }
        }

        private async Task<CloseFacts> GatherCloseFactsAsync()
        {
            var tabs = _tabs.ToList();
            int downloads = _downloads.Count(d => d.State == "InProgress");
            int restorable = SnapshotSession()?.Tabs.Count ?? 0;
            var pdf = new List<string>();
            foreach (var t in tabs)
                if (!t.IsInternal && t.PendingUrl == null && CloseRules.LooksLikePdf(t.CurrentUrl))
                    pdf.Add(CloseRules.Clean(t.CurrentTitle, System.IO.Path.GetFileName(new Uri(t.CurrentUrl).AbsolutePath)));

            // ask every loaded page at once, but never wait more than about a second
            var probes = new List<(BrowserTab Tab, Task<string> Result)>();
            foreach (var t in tabs)
            {
                if (t.IsInternal || !t.Ready || t.PendingUrl != null) continue;
                try { if (t.Web.CoreWebView2 != null) probes.Add((t, t.Web.CoreWebView2.ExecuteScriptAsync(CloseRules.PageProbe))); } catch { }
            }
            if (probes.Count > 0) await Task.WhenAny(Task.WhenAll(probes.Select(p => p.Result)), Task.Delay(1200));
            var unsaved = new List<string>();
            foreach (var (t, res) in probes)
                if (res.IsCompletedSuccessfully && res.Result == "true")
                    unsaved.Add(CloseRules.Clean(t.CurrentTitle, "a page"));
            return new CloseFacts(tabs.Count, restorable, downloads, unsaved, pdf, _closeAsk == "on");
        }

        private (bool Close, string Next, bool DontAsk) ShowClosePrompt(ClosePlan plan, string defaultNext)
        {
            Brush B(string key, Brush fallback) => Application.Current.Resources[key] as Brush ?? fallback;
            var text = B("ChromeText", Brushes.White);
            var muted = B("ChromeMuted", Brushes.LightGray);
            bool close = false;
            var win = new Window
            {
                Title = "Close Recognition?", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height,
                Width = 480, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = B("ChromeWin", Brushes.Black)
            };
            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            panel.Children.Add(new TextBlock { Text = plan.Warnings.Count > 0 ? "Before you close" : "Close Recognition?", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 0, 0, 10) });
            foreach (var w in plan.Warnings)
                panel.Children.Add(new TextBlock { Text = w, TextWrapping = TextWrapping.Wrap, Foreground = text, Margin = new Thickness(0, 0, 0, 8) });
            RadioButton? keep = null, fresh = null; CheckBox? dont = null;
            if (plan.OfferSession)
            {
                panel.Children.Add(new TextBlock { Text = _tabs.Count + " tabs are open. Next time Recognition starts:", Foreground = muted, Margin = new Thickness(0, 4, 0, 6) });
                keep = new RadioButton { Content = "Reopen the tabs I have open now", Foreground = text, IsChecked = defaultNext == "restore", GroupName = "next", Margin = new Thickness(0, 0, 0, 4) };
                fresh = new RadioButton { Content = "Start fresh with my home page", Foreground = text, IsChecked = defaultNext != "restore", GroupName = "next", Margin = new Thickness(0, 0, 0, 10) };
                dont = new CheckBox { Content = "Don't ask when I close with several tabs (you can turn this back on in Settings)", Foreground = muted, Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(keep); panel.Children.Add(fresh); panel.Children.Add(dont);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = plan.Warnings.Count > 0 ? "Close anyway" : "Close Recognition", MinWidth = 130, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
            var stay = new Button { Content = "Stay open", MinWidth = 100, Padding = new Thickness(12, 6, 12, 6), IsDefault = true, IsCancel = true };
            ok.Click += (_, __) => { close = true; win.Close(); };
            row.Children.Add(ok); row.Children.Add(stay);
            panel.Children.Add(row);
            win.Content = panel;
            win.ShowDialog();
            var next = keep?.IsChecked == true ? "restore" : "fresh";
            return (close, next, dont?.IsChecked == true);
        }

        // ---- starting ----------------------------------------------------------------------------------------------------------
        // Decides what the first tabs are. Returns the session to reopen, or null for a fresh start.
        private SessionData? ChooseStartupSession()
        {
            SessionData? d = null;
            bool has = System.IO.File.Exists(SessionPath());
            var eff = StartupRules.Effective(_startupMode, _nextStart, has);
            if (_nextStart.Length > 0) { _nextStart = ""; SaveSettings(); }
            if (eff != "fresh")
            {
                try { d = SessionRules.Parse(ReadSecure(SessionPath())); } catch { }
                if (d == null || d.Tabs.Count == 0) { d = null; eff = "fresh"; }
            }
            if (eff == "ask" && d != null)
            {
                var n = d.Tabs.Count;
                var yes = MessageBox.Show(this, "Reopen the " + (n == 1 ? "tab" : n + " tabs") + " you had open last time?\n\nYes: reopen " + (n == 1 ? "it" : "them") + "\nNo: start fresh with your home page\n\nYou can change this in Settings.",
                    "Recognition", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (!yes) d = null;
            }
            if (d == null && _startupMode == "start") ClearSession();   // a one-time "reopen" choice has been used up
            return d;
        }

        // ---- settings ----------------------------------------------------------------------------------------------------------
        private string StartupSettingsRowsHtml()
        {
            string Btn(string id, string label) => "<a class='btn" + (_startupMode == id ? "" : " ghost") + "' onclick=\"send('ws-startup:" + id + "')\">" + label + "</a> ";
            var what = _startupMode == "restore" ? "Your tabs from last time reopen (only the tab you were on loads at once)."
                     : _startupMode == "ask" ? "Recognition asks whether to reopen your tabs from last time or start fresh."
                     : "A new tab opens with your home page.";
            return "<div class='row'><div><div class='t'>When Recognition starts</div><div class='u'>" + what + " The tab list is encrypted on this computer and never includes private tabs.</div></div><div class='ts'>" +
                   Btn("start", "Open my home page") + Btn("restore", "Continue where I left off") + Btn("ask", "Ask me") + "</div></div>" +
                   "<div class='row'><div><div class='t'>When closing</div><div class='u'>" +
                   (_closeAsk == "on" ? "Recognition asks first when you have several tabs, a PDF open, or a page with changes you may not have saved, and lets you choose to reopen these tabs next time or start fresh. A download in progress always asks."
                                      : "Recognition closes without asking, except while a download is in progress.") +
                   "</div></div><div class='ts'><a class='btn" + (_closeAsk == "on" ? "" : " ghost") + "' onclick=\"send('ws-closeask:" + (_closeAsk == "on" ? "off" : "on") + "')\">" + (_closeAsk == "on" ? "Ask first: on" : "Ask first: off") + "</a></div></div>";
        }

        private void LoadExtraSettings(JsonElement r)
        {
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            _closeAsk = StartupRules.NormalizeCloseAsk(S("close_ask"));
            _nextStart = StartupRules.NormalizeNext(S("next_start"));
            LoadWeatherSettings(r);
            LoadThemeExtraSettings(r);
        }

        private string ExtraSettingsJson() =>
            J("close_ask") + ":" + J(_closeAsk) + "," + J("next_start") + ":" + J(_nextStart) + "," + WeatherSettingsJson() + ThemeExtraSettingsJson();
    }
}
