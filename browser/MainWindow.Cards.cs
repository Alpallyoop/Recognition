using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Recognition.Browser
{
    // Payment cards and the unlock PIN: the WPF glue around CardVault.cs / PinGate (rules and tests live there).
    //  - The PIN is typed in a Windows dialog of the browser itself, never in a web page.
    //  - With a PIN set, Fill / Copy / Reveal for logins and every card use need it (then stay unlocked for 5 idle minutes).
    //  - Cards cannot be saved or used until a PIN is set. The CVC is never stored.
    //  - Receipts record that a card was used on a site (brand and id), never the number.
    public partial class MainWindow
    {
        private PinGate _pin = new();
        private bool _pinLoaded, _pinDamaged;
        private CardVault _cards = null!;

        private string PinPath() => Path.Combine(_repoRoot, "runtime", "vault_lock.v1.enc");

        private void EnsurePin()
        {
            if (_pinLoaded) return;
            _pinLoaded = true;
            try
            {
                var g = PinGate.Parse(File.Exists(PinPath()) ? ReadSecure(PinPath()) : "");
                if (g == null) { _pinDamaged = true; _pin = new PinGate(); } else _pin = g;
            }
            catch { _pinDamaged = true; _pin = new PinGate(); }
        }

        private void SavePin() { try { WriteSecure(PinPath(), _pin.ToJson()); } catch (Exception ex) { Status("could not save the PIN: " + ex.Message); } }

        private void EnsureCards()
        {
            if (_cards != null) return;
            _cards = new CardVault(Path.Combine(_repoRoot, "runtime", "vault_cards.v1.enc"));
            var skipped = _cards.Load();
            if (skipped > 0) Status("card vault: " + skipped + " damaged record(s) were skipped");
        }

        // a masked PIN box in its own window; null when cancelled
        private string? AskPin(string title, string prompt)
        {
            Brush B(string key, string fallback) => Application.Current.Resources[key] as Brush ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;
            var box = new PasswordBox { MaxLength = PinGate.MaxLen, FontSize = 20, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 10, 0, 14), HorizontalContentAlignment = HorizontalAlignment.Center, Background = B("ChromeField", "#0E1015"), Foreground = B("ChromeText", "#D6DAE2"), BorderBrush = B("ChromeLine2", "#2E333C"), CaretBrush = B("ChromeText", "#D6DAE2") };
            string? result = null;
            var win = new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = B("ChromeBar", "#1B1E24") };
            var ok = new Button { Content = "OK", IsDefault = true, Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("AccentBtn") };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 84, Height = 32, Style = (Style)FindResource("NavBtn") };
            ok.Click += (_, __) => { result = box.Password; win.DialogResult = true; };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(22), Width = 320 };
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Foreground = B("ChromeText", "#D6DAE2"), FontSize = 13 });
            panel.Children.Add(box); panel.Children.Add(buttons);
            win.Content = panel;
            win.Loaded += (_, __) => box.Focus();
            return win.ShowDialog() == true ? result : null;
        }

        private void Tell(string text) => MessageBox.Show(this, text, "Recognition: Passwords and cards", MessageBoxButton.OK, MessageBoxImage.Information);

        // false (after telling the person why) when the result is not Ok
        private bool ReportPin(UnlockResult r)
        {
            if (r == UnlockResult.Ok) return true;
            if (r == UnlockResult.LockedOut) Tell("Too many wrong PINs. Try again in " + _pin.RetryAfterSeconds(DateTime.UtcNow) + " seconds.");
            else if (r == UnlockResult.Wrong) Tell("That PIN is not right.");
            return false;
        }

        // The gate in front of every fill, copy and reveal. With no PIN set it lets the action through (as before) and the
        // Passwords page keeps asking you to set one.
        private bool EnsureUnlocked(string why)
        {
            EnsurePin();
            if (_pinDamaged) { Tell("The PIN file is damaged, so the vault stays locked.\n\nTo start again, close Recognition, delete runtime\\vault_lock.v1.enc and set a new PIN. Your saved logins and cards are not deleted."); return false; }
            var now = DateTime.UtcNow;
            if (!_pin.IsSet) return true;
            if (_pin.IsUnlocked(now)) { _pin.Touch(now); return true; }
            for (int i = 0; i < 3; i++)
            {
                if (_pin.RetryAfterSeconds(now) > 0) { ReportPin(UnlockResult.LockedOut); return false; }
                var s = AskPin("Unlock", why + "\n\nEnter your PIN.");
                if (s == null) return false;
                var r = _pin.TryUnlock(s, DateTime.UtcNow); SavePin();
                if (r == UnlockResult.Ok) { SendPwSnapshot(); return true; }
                if (!ReportPin(r) && r == UnlockResult.LockedOut) return false;
                now = DateTime.UtcNow;
            }
            return false;
        }

        // cards need a PIN to exist at all
        private bool RequirePinSet()
        {
            EnsurePin();
            if (_pinDamaged) return EnsureUnlocked("");
            if (_pin.IsSet) return true;
            Tell("Set a PIN first (Passwords, Security tab). Cards are only saved and filled behind a PIN.");
            return false;
        }

        private void PostPinState()
        {
            EnsurePin();
            PostToPwPage(new { type = "pin", set = _pin.IsSet, unlocked = _pin.IsUnlocked(DateTime.UtcNow), damaged = _pinDamaged });
        }

        // saved logins and cards are only listed after the PIN has been entered (and while it is still unlocked)
        private bool VaultVisible() { EnsurePin(); return !_pinDamaged && _pin.IsSet && _pin.IsUnlocked(DateTime.UtcNow); }

        private void PostCards()
        {
            EnsureCards();
            bool vis = VaultVisible();
            PostToPwPage(new
            {
                type = "cards",
                entries = (vis ? _cards.Entries : new System.Collections.Generic.List<CardEntry>()).OrderBy(c => c.Label).ThenBy(c => c.Last4).Select(c => new { id = c.Id, label = c.Label, brand = c.Brand, mask = CardRules.Mask(c.Number), exp = CardRules.Expiry(c.Month, c.Year), holder = c.Holder, expired = !CardRules.ExpiryOk(c.Month, c.Year, DateTime.UtcNow) }).ToList()
            });
        }

        private BrowserTab? CardTarget() =>
            Enumerable.Reverse(_tabs).FirstOrDefault(t => !t.IsInternal && t.Web?.CoreWebView2 != null && Uri.TryCreate(PageAddressOf(t), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps);

        private async void CardFill(CardEntry c, BrowserTab? target)
        {
            if (target == null) { Status("open the page with the payment form (https) first, then fill the card"); PostToPwPage(new { type = "action", ok = false, text = "no open tab is on an https page. Open the payment page first." }); return; }
            if (!RequirePinSet() || !EnsureUnlocked("Fill " + c.Brand + " " + CardRules.Mask(c.Number) + " into the page.")) return;
            try
            {
                Tabs.SelectedItem = target.Item; ShowActiveWebView();
                // check again at the moment of filling: the page may have moved on, and only https pages may get a card
                if (!Uri.TryCreate(PageAddressOf(target), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { Status("card fill cancelled: the page changed"); return; }
                var res = await target.Web.CoreWebView2.ExecuteScriptAsync(CardFillScript.Build(c.Holder, c.Number, c.Month, c.Year));
                _actions?.Append("card.fill", u.Host + ":" + c.Id);
                Status(res.Contains("filled:0") ? "no card fields found on the page (the security code is never filled)" : "card filled for " + u.Host + " (not submitted; type the security code yourself)");
            }
            catch (Exception ex) { Status("card fill failed: " + ex.Message); }
        }

        private void HandleCardPinMessage(string msg)
        {
            try { HandleCardPinMessageCore(msg); }
            catch (Exception ex)
            {
                Status("PIN/cards error: " + ex.Message);
                PostToPwPage(new { type = "action", ok = false, text = "Something went wrong: " + ex.Message });
            }
        }

        private void HandleCardPinMessageCore(string msg)
        {
            EnsureCards(); EnsurePin();
            string Dec(string s) { try { return Uri.UnescapeDataString(s); } catch { return ""; } }
            if (msg == "pin-state") { PostPinState(); PostCards(); return; }
            if (msg == "pin-unlock") { if (_pin.IsSet) EnsureUnlocked("Unlock your saved logins and cards."); SendPwSnapshot(); return; }
            if (msg == "card-list") { PostCards(); return; }

            if (msg == "pin-set")
            {
                if (_pinDamaged) { EnsureUnlocked(""); return; }
                if (_pin.IsSet) { Tell("A PIN is already set. Use Change PIN."); return; }
                var a = AskPin("Set a PIN", "Choose a PIN of " + PinGate.MinLen + " to " + PinGate.MaxLen + " digits. It protects Fill, Copy, Reveal and your cards."); if (a == null) return;
                var err = PinGate.ValidatePin(a); if (err != null) { Tell("Not set: " + err + "."); return; }
                var b = AskPin("Set a PIN", "Enter the same PIN again."); if (b == null) return;
                if (a != b) { Tell("The two PINs were not the same. Nothing was set."); return; }
                _pin.SetPin(a); _pin.TryUnlock(a, DateTime.UtcNow); SavePin(); _actions?.Append("vault.pin_set", "");
                SendPwSnapshot(); PostToPwPage(new { type = "action", ok = true, text = "PIN set. The vault is unlocked for 5 idle minutes." });
            }
            else if (msg == "pin-change")
            {
                if (!_pin.IsSet) return;
                var old = AskPin("Change PIN", "Enter your current PIN."); if (old == null) return;
                var n1 = AskPin("Change PIN", "Enter the new PIN (" + PinGate.MinLen + " to " + PinGate.MaxLen + " digits)."); if (n1 == null) return;
                var n2 = AskPin("Change PIN", "Enter the new PIN again."); if (n2 == null) return;
                if (n1 != n2) { Tell("The two new PINs were not the same. Nothing was changed."); return; }
                var r = _pin.Change(old, n1, DateTime.UtcNow, out var err); SavePin();
                if (err != null) { Tell("Not changed: " + err + "."); return; }
                if (!ReportPin(r)) return;
                _actions?.Append("vault.pin_change", ""); SendPwSnapshot(); PostToPwPage(new { type = "action", ok = true, text = "PIN changed." });
            }
            else if (msg == "pin-remove")
            {
                if (!_pin.IsSet) return;
                var p = AskPin("Remove PIN", "Enter your PIN to remove it. Saved cards stay stored but cannot be used until a new PIN is set."); if (p == null) return;
                var r = _pin.Remove(p, DateTime.UtcNow); SavePin();
                if (!ReportPin(r)) return;
                _actions?.Append("vault.pin_remove", ""); SendPwSnapshot(); PostToPwPage(new { type = "action", ok = true, text = "PIN removed." });
            }
            else if (msg == "pin-lock") { _pin.Lock(); SendPwSnapshot(); PostToPwPage(new { type = "action", ok = true, text = "Locked." }); }
            else if (msg.StartsWith("card-add:"))
            {
                try
                {
                    if (!RequirePinSet() || !EnsureUnlocked("Save a card.")) return;
                    using var d = JsonDocument.Parse(Dec(msg.Substring("card-add:".Length))); var r = d.RootElement;
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    int I(string k) { var t = S(k).Trim(); return int.TryParse(t, out var n) ? n : 0; }
                    var err = _cards.Add(S("label"), S("holder"), S("number"), I("month"), I("year"), DateTime.UtcNow, out var added);
                    if (err != null) { PostToPwPage(new { type = "action", ok = false, text = err }); return; }
                    _actions?.Append("card.add", added!.Brand + ":" + added.Id);
                    PostToPwPage(new { type = "cardadded" }); PostCards();
                }
                catch { PostToPwPage(new { type = "action", ok = false, text = "invalid card" }); }
            }
            else if (msg.StartsWith("card-fill:"))
            {
                var c = _cards.Get(msg.Substring("card-fill:".Length));
                if (c != null) CardFill(c, CardTarget());
            }
            else if (msg.StartsWith("card-copy:"))
            {
                var c = _cards.Get(msg.Substring("card-copy:".Length));
                if (c == null || !RequirePinSet() || !EnsureUnlocked("Copy the card number.")) return;
                _actions?.Append("card.copy", c.Brand + ":" + c.Id);
                CopySecret(c.Number, "card number");
            }
            else if (msg.StartsWith("card-del:"))
            {
                var c = _cards.Get(msg.Substring("card-del:".Length));
                if (c == null) return;
                if (MessageBox.Show(this, "Delete " + c.Brand + " " + CardRules.Mask(c.Number) + "?\n\nThis cannot be undone.", "Recognition: Cards", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _cards.Remove(c.Id); _actions?.Append("card.delete", c.Brand + ":" + c.Id); PostCards();
            }
        }
    }
}
