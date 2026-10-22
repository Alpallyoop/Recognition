using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Recognition.Browser
{
    // Passwords page (recognition:passwords). Core rules (storage, validation, exact-origin matching, generator,
    // audit, fill script) live in PasswordVault.cs and are executed by browser.tests; this file is the WPF glue.
    //  - Nothing is ever auto-filled or auto-saved. Fill / Copy / Reveal are explicit clicks.
    //  - The list sent to the page never contains passwords. Reveal sends one for a few seconds.
    //  - Receipts (hash-chained) record that an action happened and for which site, never the secret.
    public partial class MainWindow
    {
        private PasswordVault _vault = null!;
        private DispatcherTimer? _clipTimer;

        private void EnsureVault()
        {
            if (_vault != null) return;
            _vault = new PasswordVault(Path.Combine(_repoRoot, "runtime", "vault_passwords.v1.enc"));
            var skipped = _vault.Load();
            if (skipped > 0) Status("password vault: " + skipped + " damaged record(s) were skipped");
        }

        // ---- key button in the address bar -------------------------------------------------------------------------------
        // Shown only when this page's exact site has saved logins. Clicking it lists them; choosing one fills the page (not submitted).
        // Nothing is ever filled by the page itself, by hovering or on load: it is always your click here.
        private void UpdateKeyBtn(BrowserTab tab)
        {
            try
            {
                bool show = false;
                if (!tab.IsInternal && !string.IsNullOrEmpty(tab.CurrentUrl))
                {
                    EnsureVault();
                    EnsureCards();
                    // with SoteriaVault as the password manager, Recognition's own vault is deferred: SoteriaVault's logins show the key
                    bool sv = SoteriaPmActive;
                    show = (!sv && _vault.ForPage(PageAddressOf(tab)).Count > 0) || (_cards.Entries.Count > 0 && Uri.TryCreate(PageAddressOf(tab), UriKind.Absolute, out var cu) && cu.Scheme == Uri.UriSchemeHttps);
                    if (sv && !show) _ = UpdateKeyBtnSoteriaAsync(tab);
                }
                KeyBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { KeyBtn.Visibility = Visibility.Collapsed; }
        }

        private void Key_Click(object sender, RoutedEventArgs e)
        {
            var a = Active; if (a == null) return;
            if (SoteriaPmActive) { SoteriaKeyMenu(a); return; }   // SoteriaVault asks you itself; no Recognition PIN in between
            EnsureVault(); EnsurePin();
            if (_pin.IsSet && !EnsureUnlocked("Show the saved logins and cards for this page.")) return;
            var list = _vault.ForPage(PageAddressOf(a));
            var cm = new System.Windows.Controls.ContextMenu();
            foreach (var entry in list.OrderBy(x => x.Username))
            {
                var en = entry;
                var mi = new System.Windows.Controls.MenuItem { Header = "Fill  " + (en.Username.Length > 0 ? en.Username : "(no user name)") };
                mi.Click += (_, __) => PwFill(en);
                cm.Items.Add(mi);
            }
            EnsureCards();
            if (_cards.Entries.Count > 0 && Uri.TryCreate(PageAddressOf(a), UriKind.Absolute, out var pu) && pu.Scheme == Uri.UriSchemeHttps)
            {
                if (list.Count > 0) cm.Items.Add(new System.Windows.Controls.Separator());
                foreach (var card in _cards.Entries.OrderBy(x => x.Label).ThenBy(x => x.Last4))
                {
                    var cd = card;
                    var mi = new System.Windows.Controls.MenuItem { Header = "Pay with  " + cd.Brand + " " + CardRules.Mask(cd.Number) + (cd.Label.Length > 0 ? "  (" + cd.Label + ")" : "") };
                    mi.Click += (_, __) => CardFill(cd, a);
                    cm.Items.Add(mi);
                }
            }
            cm.Items.Add(new System.Windows.Controls.Separator());
            var manage = new System.Windows.Controls.MenuItem { Header = "Manage passwords" };
            manage.Click += (_, __) => OpenInternalInActiveTab("passwords");
            cm.Items.Add(manage);
            cm.PlacementTarget = KeyBtn; cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; cm.IsOpen = true;
        }

        private void Extensions_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("extensions");

        private void MenuPasswords_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("passwords");

        private void PostToPwPage(object payload)
        {
            var t = Active; if (t == null || t.Internal != "passwords") return;
            try { t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
        }

        private string? CurrentSiteSuggestion()
        {
            foreach (var t in Enumerable.Reverse(_tabs))
                if (!t.IsInternal && t.Web?.CoreWebView2 != null) { var o = PasswordRules.OriginOf(t.Web.Source?.ToString()); if (o != null) return o; }
            return null;
        }

        private void SendPwSnapshot()
        {
            EnsureVault();
            bool vis = VaultVisible();
            var audit = _vault.Audit();
            PostToPwPage(new
            {
                type = "snapshot", current = vis ? CurrentSiteSuggestion() : null, sv = SoteriaPmActive,
                entries = (vis ? _vault.Entries : new System.Collections.Generic.List<VaultEntry>()).OrderBy(e => e.Origin).ThenBy(e => e.Username).Select(e => new
                {
                    id = e.Id, origin = e.Origin, username = e.Username, strength = PasswordRules.Strength(e.Password), updated = e.UpdatedUtc,
                    reused = audit.Any(a => a.Id == e.Id && a.Kind == "reused"), hasNote = e.Note.Length > 0
                }).ToList()
            });
            PostPinState(); PostCards();
        }

        private void CopySecret(string value, string what)
        {
            try
            {
                var dob = new DataObject();
                dob.SetText(value);
                // Best effort: ask Windows not to keep this in clipboard history / cloud clipboard.
                dob.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
                Clipboard.SetDataObject(dob, true);
                _clipTimer?.Stop();
                _clipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _clipTimer.Tick += (_, __) =>
                {
                    _clipTimer?.Stop();
                    try { if (Clipboard.ContainsText() && Clipboard.GetText() == value) Clipboard.Clear(); } catch { }
                };
                _clipTimer.Start();
                PostToPwPage(new { type = "action", ok = true, text = what + " copied. It is cleared from the clipboard in 30 seconds." });
            }
            catch (Exception ex) { PostToPwPage(new { type = "action", ok = false, text = "could not use the clipboard: " + ex.Message }); }
        }

        private async void PwFill(VaultEntry e)
        {
            if (!EnsureUnlocked("Fill the saved login for " + e.Origin + ".")) return;
            var target = _tabs.FirstOrDefault(t => !t.IsInternal && t.Web?.CoreWebView2 != null && PasswordRules.MayFill(e.Origin, t.Web.Source?.ToString()));
            if (target == null)
            {
                PostToPwPage(new { type = "action", ok = false, text = "no open tab is on exactly https://" + e.Origin + ". Open the site in a tab first, then click Fill. (Subdomains and look-alike sites are never filled.)" });
                return;
            }
            Tabs.SelectedItem = target.Item; ShowActiveWebView();
            try
            {
                // re-check at the moment of injection: the page may have navigated since the match above
                if (!PasswordRules.MayFill(e.Origin, target.Web.Source?.ToString())) { Status("fill cancelled: the page changed"); return; }
                var res = await target.Web.CoreWebView2.ExecuteScriptAsync(FillScript.Build(e.Username, e.Password));
                _actions?.Append("vault.fill", e.Origin + ":" + e.Id);
                Status(res.Contains("no-password-field") ? "no password field found on the page" : "filled for " + e.Origin + " (not submitted)");
            }
            catch (Exception ex) { Status("fill failed: " + ex.Message); }
        }

        private void HandlePwMessage(string msg)
        {
            EnsureVault();
            string Dec(string s) { try { return Uri.UnescapeDataString(s); } catch { return ""; } }
            if (msg == "pw-list") { SendPwSnapshot(); return; }
            if (msg == "pw-sv-open") { OpenSoteriaApp(); return; }
            if (msg == "pw-sv-move") { SoteriaMoveVault(t => PostToPwPage(new { type = "action", ok = true, text = t })); return; }
            if (msg.StartsWith("pw-gen:"))
            {
                try
                {
                    using var d = JsonDocument.Parse(Dec(msg.Substring("pw-gen:".Length))); var r = d.RootElement;
                    bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                    var pw = PasswordGenerator.Generate(r.GetProperty("length").GetInt32(), B("lower"), B("upper"), B("digits"), B("symbols"), B("ambig"));
                    PostToPwPage(new { type = "generated", pw });
                }
                catch (ArgumentException ex) { PostToPwPage(new { type = "action", ok = false, text = ex.Message }); }
                catch { PostToPwPage(new { type = "action", ok = false, text = "invalid generator settings" }); }
            }
            else if (msg.StartsWith("pw-add:"))
            {
                try
                {
                    using var d = JsonDocument.Parse(Dec(msg.Substring("pw-add:".Length))); var r = d.RootElement;
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    if (SoteriaPmActive) { SoteriaSaveFromPage(S("origin"), S("username"), S("password"), S("note")); return; }
                    var err = _vault.Add(S("origin"), S("username"), S("password"), S("note"), DateTime.UtcNow, out var added);
                    if (err != null) { PostToPwPage(new { type = "action", ok = false, text = err }); return; }
                    _actions?.Append("vault.add", added!.Origin + ":" + added.Id);
                    PostToPwPage(new { type = "added" }); SendPwSnapshot();
                }
                catch { PostToPwPage(new { type = "action", ok = false, text = "invalid entry" }); }
            }
            else if (msg.StartsWith("pw-reveal:"))
            {
                var e = _vault.Get(msg.Substring("pw-reveal:".Length));
                if (e == null || !EnsureUnlocked("Show the password for " + e.Origin + ".")) return;
                _actions?.Append("vault.reveal", e.Origin + ":" + e.Id);
                PostToPwPage(new { type = "reveal", id = e.Id, pw = e.Password });
            }
            else if (msg.StartsWith("pw-copy:"))
            {
                var parts = msg.Substring("pw-copy:".Length).Split(':');
                var e = parts.Length == 2 ? _vault.Get(parts[0]) : null;
                if (e == null || !EnsureUnlocked("Copy from the saved login for " + e.Origin + ".")) return;
                if (parts[1] == "pass") { _actions?.Append("vault.copy_password", e.Origin + ":" + e.Id); CopySecret(e.Password, "password"); }
                else if (parts[1] == "user") CopySecret(e.Username, "username");
            }
            else if (msg.StartsWith("pw-fill:"))
            {
                var e = _vault.Get(msg.Substring("pw-fill:".Length));
                if (e != null) PwFill(e);
            }
            else if (msg.StartsWith("pw-del:"))
            {
                var e = _vault.Get(msg.Substring("pw-del:".Length));
                if (e == null) return;
                if (MessageBox.Show(this, "Delete the saved login for " + e.Username + " at " + e.Origin + "?\n\nThis cannot be undone.", "Recognition — Passwords", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _vault.Remove(e.Id);
                _actions?.Append("vault.delete", e.Origin + ":" + e.Id);
                SendPwSnapshot();
            }
        }

        private string PasswordsHtml()
        {
            var sb = new StringBuilder(PageHead);
            sb.Append(@"<title>Passwords</title><style>body.locked .tabs a:not(:first-child){display:none}body.locked section.sect:not(:first-child){display:none!important}</style><h1>Passwords</h1>
<div class='muted'>Logins are stored encrypted for your Windows account (DPAPI). <b>Nothing is filled or saved automatically</b>: you click Fill, Copy or Reveal. Fill only works on a tab that is on <b>exactly</b> the saved site over https (no subdomains, no look-alikes) and never submits the form. Copied passwords are cleared from the clipboard after 30 seconds. Passwords are never included in setup snapshots or receipts. Limit: any program running as you could still ask Windows to decrypt this file.</div>
<div id='svb' class='row' style='display:none;border:1px solid #3a5bd0;background:#151b2e'><div><div class='t'>Your passwords are kept in SoteriaVault</div><div class='u'>New logins you save here go to SoteriaVault, which tells you at once if one is already saved. The key button in the address bar fills from SoteriaVault after you allow it there. The logins listed below are the ones still stored in Recognition itself.</div></div><div class='ts'><a class='btn' onclick=""send('pw-sv-move')"">Move these to SoteriaVault</a><a class='btn ghost' onclick=""send('pw-sv-open')"">Open SoteriaVault</a></div></div>
<div id='msg' class='u' style='min-height:18px;margin:8px 0'></div>

<h1 style='font-size:16px'>Security</h1>
<div class='row'><div><div class='t'>Unlock PIN</div><div class='u' id='pinst'>Checking...</div></div><div class='ts' id='pinbtns'></div></div>
<div class='muted'>The PIN is typed in a Windows window of the browser, never in a web page, and is stored only as a salted hash. With a PIN set, Fill, Copy and Reveal ask for it, then stay unlocked for 5 idle minutes. Five wrong tries lock it for 30 seconds, doubling each time (up to 15 minutes). Honest limit: this stops someone who sits down at your open browser; it does not stop other programs running as you from reading your files.</div>

<h1 style='font-size:16px'>Payment cards</h1>
<div class='muted'>Cards need a PIN. The <b>security code (CVC) is never stored</b>, so you type it yourself. A card is filled only when you click it, only on an https page, only into the page you are viewing, and the page is never submitted. Click the key button in the address bar on a payment page, or use Fill here.</div>
<div style='display:grid;grid-template-columns:110px 1fr;gap:8px;max-width:640px;align-items:center'>
 <div>Label</div><input id='cl' placeholder='Personal Visa' autocomplete='off'>
 <div>Name on card</div><input id='ch' autocomplete='off'>
 <div>Card number</div><input id='cn' inputmode='numeric' autocomplete='off' placeholder='1234 5678 9012 3456'>
 <div>Expires</div><div style='display:flex;gap:8px;align-items:center'><input id='cm' placeholder='MM' style='width:70px' autocomplete='off'><span>/</span><input id='cy' placeholder='YY' style='width:80px' autocomplete='off'></div>
</div>
<div style='margin:10px 0'><a class='btn' onclick='cardAdd()'>Save card</a></div>
<div id='clist'></div>

<h1 style='font-size:16px'>Add a login</h1>
<div style='display:grid;grid-template-columns:110px 1fr;gap:8px;max-width:640px;align-items:center'>
 <div>Site</div><input id='o' placeholder='example.com' style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px'>
 <div>Username</div><input id='u' autocomplete='off' style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px'>
 <div>Password</div><div style='display:flex;gap:6px'><input id='p' type='password' autocomplete='new-password' style='flex:1;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px'><a class='btn ghost' onclick='togglePw()'>Show</a></div>
 <div>Note</div><input id='n' style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px'>
</div>
<div style='margin:8px 0;display:flex;gap:10px;flex-wrap:wrap;align-items:center'>
 <span class='u'>Generator:</span>
 <input id='len' type='number' min='8' max='128' value='20' style='width:64px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'>
 <label><input type='checkbox' id='g_lower' checked> a-z</label><label><input type='checkbox' id='g_upper' checked> A-Z</label><label><input type='checkbox' id='g_digits' checked> 0-9</label><label><input type='checkbox' id='g_symbols' checked> symbols</label><label><input type='checkbox' id='g_ambig' checked> avoid look-alikes</label>
 <a class='btn ghost' onclick='gen()'>Generate</a> <a class='btn' onclick='add()'>Save login</a></div>

<h1 style='font-size:16px'>Saved logins</h1>
<div id='list'></div>
<script>
document.body.classList.add('locked');
function $(i){return document.getElementById(i)}
function el(t,txt,cls){var e=document.createElement(t);if(txt!=null)e.textContent=txt;if(cls)e.className=cls;return e}
var enc=encodeURIComponent;
function togglePw(){var p=$('p');p.type=p.type==='password'?'text':'password'}
function gen(){send('pw-gen:'+enc(JSON.stringify({length:parseInt($('len').value,10)||0,lower:$('g_lower').checked,upper:$('g_upper').checked,digits:$('g_digits').checked,symbols:$('g_symbols').checked,ambig:$('g_ambig').checked})))}
function add(){send('pw-add:'+enc(JSON.stringify({origin:$('o').value,username:$('u').value,password:$('p').value,note:$('n').value})))}
function btn(t,cmd,ghost){var a=el('a',t,'btn'+(ghost?' ghost':''));a.style.marginLeft='6px';a.onclick=function(){send(cmd)};return a}
function cardAdd(){send('card-add:'+enc(JSON.stringify({label:$('cl').value,holder:$('ch').value,number:$('cn').value,month:$('cm').value,year:$('cy').value})))}
function renderPin(m){var s=$('pinst'),b=$('pinbtns');b.textContent='';var open=m.set&&m.unlocked&&!m.damaged;document.body.classList.toggle('locked',!open);if(!open){var t0=document.querySelector('.tabs a');if(t0)t0.click();$('list').textContent='';$('clist').textContent=''}
  if(m.damaged){s.textContent='The PIN file is damaged: the vault stays locked until it is reset (see the message that appears when you use it).';return}
  if(!m.set){s.textContent='No PIN set. Set a PIN to open your saved logins and cards.';b.appendChild(btn('Set a PIN','pin-set'));return}
  s.textContent=m.unlocked?'Unlocked (locks again after 5 idle minutes).':'Locked. Enter your PIN to see your saved logins and cards.';
  if(!m.unlocked){b.appendChild(btn('Unlock','pin-unlock'));return}
  b.appendChild(btn('Change PIN','pin-change',true));b.appendChild(btn('Remove PIN','pin-remove',true));b.appendChild(btn('Lock now','pin-lock',true))}
function renderCards(m){var l=$('clist');l.textContent='';
  if(!m.entries.length){l.appendChild(el('div','No saved cards yet.','empty'));return}
  m.entries.forEach(function(c){var r=el('div',null,'row');var d=el('div');
    d.appendChild(el('div',c.brand+'  '+c.mask+(c.label?'  ('+c.label+')':''),'t'));
    d.appendChild(el('div',c.holder+'  ·  expires '+c.exp+(c.expired?'  ·  EXPIRED':''),'u'));r.appendChild(d);
    var b=el('div',null,'ts');b.appendChild(btn('Fill','card-fill:'+c.id));b.appendChild(btn('Copy number','card-copy:'+c.id,true));b.appendChild(btn('Delete','card-del:'+c.id,true));r.appendChild(b);l.appendChild(r)})}
function render(m){
  $('svb').style.display=m.sv?'flex':'none';
  if(m.current&&!$('o').value)$('o').value=m.current;
  var l=$('list');l.textContent='';
  if(!m.entries.length){l.appendChild(el('div','No saved logins yet.','empty'));return}
  m.entries.forEach(function(e){var r=el('div',null,'row');r.id='r_'+e.id;var d=el('div');
    d.appendChild(el('div',e.origin,'t'));
    d.appendChild(el('div',(e.username||'(no username)')+'  ·  strength: '+e.strength+(e.reused?'  ·  REUSED on another site':'')+(e.hasNote?'  ·  has note':''),'u'));
    var pv=el('div',null,'u');pv.id='pv_'+e.id;d.appendChild(pv);r.appendChild(d);
    var b=el('div',null,'ts');
    b.appendChild(btn('Fill','pw-fill:'+e.id));b.appendChild(btn('Copy password','pw-copy:'+e.id+':pass',true));b.appendChild(btn('Copy username','pw-copy:'+e.id+':user',true));
    b.appendChild(btn('Reveal','pw-reveal:'+e.id,true));b.appendChild(btn('Delete','pw-del:'+e.id,true));r.appendChild(b);l.appendChild(r)})}
window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m)return;
  if(m.type==='snapshot')render(m);
  else if(m.type==='pin')renderPin(m);
  else if(m.type==='cards')renderCards(m);
  else if(m.type==='cardadded'){$('cl').value='';$('ch').value='';$('cn').value='';$('cm').value='';$('cy').value='';$('msg').textContent='Card saved.'}
  else if(m.type==='generated'){$('p').value=m.pw;$('p').type='text'}
  else if(m.type==='added'){$('u').value='';$('p').value='';$('p').type='password';$('n').value='';$('msg').textContent='Saved.'}
  else if(m.type==='reveal'){var s=$('pv_'+m.id);if(s){s.textContent='Password: '+m.pw+'   (hides in 10 s)';setTimeout(function(){s.textContent=''},10000)}}
  else if(m.type==='action'){$('msg').textContent=m.text}});
window.addEventListener('load',function(){send('pw-list')});   // the vault page lists its own entries once loaded (internal page only; never fills web pages)
setInterval(function(){send('pin-state')},15000);
</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }
    }
}
