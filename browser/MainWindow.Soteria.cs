using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Recognition.Browser
{
    // Optional link to SoteriaVault. The decisions are in SoteriaBridge.cs (executed by tests). This file only reads two small files
    // from the folder you chose, shows the result and records that you checked. It starts no process and receives no secrets.
    public partial class MainWindow
    {
        private string _soteriaRoot = "";   // chosen by you; persisted in browser_settings.json as soteria_root

        private void MenuSoteria_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("soteria");

        private SoteriaStatus SoteriaCheck()
        {
            var root = _soteriaRoot;
            return SoteriaBridge.Evaluate(root, rel =>
            {
                var full = Path.Combine(root, rel);
                var fi = new FileInfo(full);
                if (!fi.Exists) return null;
                if (fi.Length > SoteriaBridge.MaxFileBytes) return new byte[SoteriaBridge.MaxFileBytes + 1];   // too large: reported as untrusted
                return File.ReadAllBytes(full);
            });
        }

        private void PostToSoteriaPage(object payload)
        {
            var t = Active; if (t == null || t.Internal != "soteria") return;
            try { t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
        }

        private void SendSoteriaSnapshot(bool record)
        {
            var st = SoteriaCheck();
            if (record && st.State != SoteriaState.NotConfigured) _actions?.Append("soteria.check", st.State + ":" + (st.ContractSha256.Length >= 12 ? st.ContractSha256.Substring(0, 12) : "none"));
            if (record) WriteSoteriaReceipt(st);
            PostToSoteriaPage(new { type = "snapshot", root = _soteriaRoot, state = st.State.ToString(), reason = st.Reason, sha = st.ContractSha256, caps = st.Capabilities });
        }

        private void HandleSoteriaMessage(string msg)
        {
            try
            {
                if (msg.StartsWith("sv-pm-", StringComparison.Ordinal)) { HandleSoteriaPmMessage(msg, PostToSoteriaPage); return; }
                if (msg == "sv-state") { SendSoteriaSnapshot(false); return; }
                if (msg == "sv-check") { SendSoteriaSnapshot(true); return; }
                if (msg == "sv-clear")
                {
                    _soteriaRoot = ""; SaveSettings(); _actions?.Append("soteria.unlink");
                    Status("SoteriaVault link removed"); SendSoteriaSnapshot(false); return;
                }
                if (msg.StartsWith("sv-set:", StringComparison.Ordinal))
                {
                    var p = msg.Substring("sv-set:".Length).Trim();
                    if (!SoteriaBridge.IsSafeRoot(p) || !Directory.Exists(p))
                    {
                        PostToSoteriaPage(new { type = "action", ok = false, text = "That is not an existing folder on a local drive." }); return;
                    }
                    _soteriaRoot = p; SaveSettings(); _actions?.Append("soteria.link", "chosen");
                    SendSoteriaSnapshot(true); return;
                }
            }
            catch (Exception ex) { PostToSoteriaPage(new { type = "action", ok = false, text = "error: " + ex.Message }); }
        }

        private string SoteriaHtml()
        {
            var sb = new StringBuilder(PageHead);
            sb.Append(@"<title>SoteriaVault</title><h1>SoteriaVault</h1>
<div class='muted'>SoteriaVault is a separate program that keeps files, photos and passwords in encrypted containers. Recognition can be linked to it, but never depends on it, and it never depends on Recognition. This page is the link. It reads two small files that SoteriaVault publishes about the link (its connector contract), checks that they follow SoteriaVault's own rules, and tells you the state. <b>Photos and keys never leave SoteriaVault.</b> When SoteriaVault's contract allows it and you switch it on below, SoteriaVault becomes Recognition's password manager: Recognition asks the running SoteriaVault app for one password at a time, for the exact site you are on, and you allow each one in SoteriaVault.</div>
<div id='msg' class='u' style='min-height:18px;margin:8px 0'></div>
<div id='info' class='row' style='display:block'></div>
<h1 style='font-size:16px'>Password manager</h1>
<div id='pm' class='row' style='display:block'><div class='u'>Checking...</div></div>
<div style='margin:8px 0;display:flex;gap:6px;flex-wrap:wrap'><a class='btn' id='pmon' onclick=""send('sv-pm-on')"">Use SoteriaVault for passwords</a><a class='btn ghost' id='pmoff' onclick=""send('sv-pm-off')"">Stop using it</a><a class='btn ghost' onclick=""send('sv-pm-move')"">Move my passwords to SoteriaVault</a><a class='btn ghost' onclick=""send('sv-pm-open')"">Open SoteriaVault</a></div>
<div id='pmmsg' class='u' style='min-height:18px'></div>
<h1 style='font-size:16px'>Choose the SoteriaVault folder</h1>
<div class='muted'>Type the folder where SoteriaVault is installed. Nothing is searched for. The folder is only read, never changed.</div>
<div style='margin:8px 0;display:flex;gap:6px;flex-wrap:wrap;align-items:center'>
 <input id='p' placeholder='the folder that holds SoteriaVault (for example D:\path\to\privacy-sector)' style='min-width:320px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'>
 <a class='btn' onclick=""setp()"">Link</a><a class='btn ghost' onclick=""send('sv-check')"">Check again</a><a class='btn ghost' onclick=""send('sv-clear')"">Remove link</a></div>
<script>
function $(i){return document.getElementById(i)}
function el(t,txt,cls){var e=document.createElement(t);if(txt!=null)e.textContent=txt;if(cls)e.className=cls;return e}
function setp(){var v=$('p').value.trim();if(v)send('sv-set:'+v)}
var NAMES={NotConfigured:'Not linked',Unavailable:'Not found',Invalid:'Refused',ContractOnly:'Declared, not switched on',Ready:'Switched on by SoteriaVault'};
function render(m){
  var i=$('info');i.textContent='';
  i.appendChild(el('div','State: '+(NAMES[m.state]||m.state),'t'));
  i.appendChild(el('div',m.reason,'u'));
  if(m.root)i.appendChild(el('div','Folder: '+m.root,'u'));
  if(m.sha)i.appendChild(el('div','Contract fingerprint (SHA-256): '+m.sha,'u'));
  if(m.caps&&m.caps.length)i.appendChild(el('div','SoteriaVault says it allows now: '+m.caps.join(', '),'u'));
  if(m.root&&!$('p').value)$('p').value=m.root}
var CONN={ready:'connected, unlocked',locked:'connected, SoteriaVault is locked (unlock it there)',not_running:'SoteriaVault is not running',not_paired:'waiting for you to allow Recognition in SoteriaVault',off:'off'};
function renderPm(m){var p=$('pm');p.textContent='';
  p.appendChild(el('div',m.active?'SoteriaVault is your password manager':(m.on?'Switched on, but not active':'Recognition uses its own password vault'),'t'));
  p.appendChild(el('div',!m.allowed?'SoteriaVault has not allowed this yet (its contract must list per_fill_release_to_paired_client).':(m.active?'Connection: '+(CONN[m.conn]||m.conn):'Switch it on to keep your passwords in SoteriaVault.'),'u'));
  if(m.local)p.appendChild(el('div',m.local+' login(s) are still saved in Recognition. Move them to SoteriaVault (duplicates are merged there).','u'));
  if(!m.app)p.appendChild(el('div','SoteriaVault\'s app is not built in that folder yet: run Build in SoteriaVault first.','u'));
  $('pmon').style.display=m.on?'none':'';$('pmoff').style.display=m.on?'':'none'}
window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m)return;if(m.type==='snapshot'){render(m);$('msg').textContent='';send('sv-pm-state')}else if(m.type==='pm')renderPm(m);else if(m.type==='pm-action'){$('pmmsg').textContent=m.text;send('sv-pm-state')}else if(m.type==='action')$('msg').textContent=m.text});
send('sv-state');
</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }
    }
}
