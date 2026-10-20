using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;

namespace Recognition.Browser
{
    // Settings > Updates > "Check for updates": on your click only, asks GitHub for the latest release, and if it is newer
    // downloads the zip + its SHA-256 (https, GitHub hosts only, size-capped), refuses on any checksum mismatch, then closes this
    // window and lets scripts\recognition_self_update_v1.ps1 (a copy of it) swap the program files, keep your data, roll back on
    // failure, remove old copies and reopen. Nothing is checked or downloaded in the background. No text from a page reaches this code.
    public partial class MainWindow
    {
        private bool _selfUpdateBusy;
        private string UpdateScratch => Path.Combine(_repoRoot, ".scratch", "update");

        private async Task<string> GetUpdateTextAsync(HttpClient http, string url)
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("GitHub answered " + (int)resp.StatusCode);
            if (!SelfUpdateRules.IsAllowedHost(resp.RequestMessage?.RequestUri)) throw new InvalidOperationException("the answer came from an address that is not GitHub");
            var s = await resp.Content.ReadAsStringAsync();
            if (s.Length > 2_000_000) throw new InvalidOperationException("the answer was unexpectedly large");
            return s;
        }

        private async void CheckForUpdate(BrowserTab tab)
        {
            if (_selfUpdateBusy) { Status("an update is already in progress"); return; }
            _selfUpdateBusy = true;
            try
            {
                Status("checking GitHub for a newer version…");
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Recognition-updater/1.0");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var rel = SelfUpdateRules.ParseRelease(await GetUpdateTextAsync(http, SelfUpdateRules.ApiUrl));
                if (rel == null)
                {
                    Status("update check: no usable release found");
                    MessageBox.Show(this, "GitHub has no release with both " + SelfUpdateRules.ZipName + " and its .sha256 file, so there is nothing safe to install.", "Recognition — Update", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var cur = InstalledVersion();
                if (!SelfUpdateRules.IsNewer(cur, rel.Version))
                {
                    Status("you are up to date (" + cur + ")");
                    MessageBox.Show(this, "You are up to date.\n\nInstalled: " + cur + "\nLatest on GitHub: " + rel.Tag, "Recognition — Update", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (Directory.Exists(Path.Combine(_repoRoot, ".git")))
                {
                    MessageBox.Show(this, "Version " + rel.Tag + " is available, but this copy runs from a source folder.\n\nUse \"Update everything\" (or UPDATE_ALL.cmd) in the source folder instead; it also runs the tests.", "Recognition — Update", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(this, "Version " + rel.Tag + " is available (you have " + cur + ", download about " + Math.Max(1, rel.Size / 1024 / 1024) + " MB).\n\n" +
                        "Recognition will download it from GitHub, check its SHA-256, close, replace the program files (your bookmarks, history, passwords and settings stay), remove old copies, and reopen. If anything fails it rolls back.\n\nInstall now?",
                        "Recognition — Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Status("update cancelled"); return; }

                try { if (Directory.Exists(UpdateScratch)) Directory.Delete(UpdateScratch, true); } catch { }
                Directory.CreateDirectory(UpdateScratch);
                var want = SelfUpdateRules.ParseSha256(await GetUpdateTextAsync(http, rel.ShaUrl));
                if (want == null) throw new InvalidOperationException("the release's checksum file is not a SHA-256");

                Status("downloading " + rel.Tag + "…");
                var zip = Path.Combine(UpdateScratch, SelfUpdateRules.ZipName);
                string got;
                using (var resp = await http.GetAsync(rel.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("GitHub answered " + (int)resp.StatusCode);
                    if (!SelfUpdateRules.IsAllowedHost(resp.RequestMessage?.RequestUri)) throw new InvalidOperationException("the download came from an address that is not GitHub");
                    using var sha = SHA256.Create();
                    long total = 0;
                    using (var net = await resp.Content.ReadAsStreamAsync())
                    using (var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920]; int n; long lastShown = 0;
                        while ((n = await net.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > SelfUpdateRules.MaxZipBytes) throw new InvalidOperationException("the download is larger than allowed");
                            sha.TransformBlock(buf, 0, n, null, 0);
                            await fs.WriteAsync(buf, 0, n);
                            if (total - lastShown > 4 * 1024 * 1024) { lastShown = total; Status("downloading " + rel.Tag + "… " + total / 1024 / 1024 + " MB"); }
                        }
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                }
                if (got != want)
                {
                    try { File.Delete(zip); } catch { }
                    _actions?.Append("update.refused", "checksum mismatch");
                    MessageBox.Show(this, "The download does not match the release's SHA-256, so it was deleted and nothing was changed.", "Recognition — Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var helperSrc = Path.Combine(_repoRoot, "scripts", "recognition_self_update_v1.ps1");
                if (!File.Exists(helperSrc)) throw new InvalidOperationException("the update helper script is missing from this install");
                // run a copy: the helper replaces the scripts folder it was started from
                var helper = Path.Combine(UpdateScratch, "self_update_" + Guid.NewGuid().ToString("N") + ".ps1");
                File.Copy(helperSrc, helper, true);
                var args = "-NoProfile -ExecutionPolicy Bypass -File \"" + helper + "\" -InstallRoot \"" + _repoRoot + "\" -ZipPath \"" + zip + "\" -ExpectedSha256 " + want +
                           " -NewVersion " + rel.Version + " -WaitPid " + Environment.ProcessId + " -Relaunch";
                _actions?.Append("update.started", rel.Tag);
                Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, WorkingDirectory = UpdateScratch });
                Status("closing to install " + rel.Tag + "…");
                _closeConfirmed = true;   // the person already said yes to closing for this update
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Status("update failed: " + ex.Message);
                MessageBox.Show(this, "The update did not start and nothing was changed.\n\n" + ex.Message, "Recognition — Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _selfUpdateBusy = false; }
        }
    }
}
