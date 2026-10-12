using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

namespace Recognition.Browser
{
    // Settings > Updates > "Update everything": starts scripts\UPDATE_ALL_V1.ps1 from your source checkout in a visible PowerShell 7 window.
    // Only on your click, only a fixed command line (no text from a page ever reaches it), and only when a source checkout is known:
    // either this copy runs from one, or the last update run left runtime\source_root.txt in the install folder.
    public partial class MainWindow
    {
        private string? SourceRootForUpdate()
        {
            try
            {
                string? root = null;
                if (Directory.Exists(Path.Combine(_repoRoot, ".git"))) root = _repoRoot;
                else
                {
                    var p = Path.Combine(_repoRoot, "runtime", "source_root.txt");
                    if (File.Exists(p) && new FileInfo(p).Length < 1024) root = File.ReadAllText(p).Trim();
                }
                if (string.IsNullOrEmpty(root) || !Regex.IsMatch(root, @"^[A-Za-z]:\\[^""<>|*?\r\n]+$")) return null;
                root = root.TrimEnd('\\');
                if (!Directory.Exists(Path.Combine(root, ".git")) || !File.Exists(Path.Combine(root, "scripts", "UPDATE_ALL_V1.ps1"))) return null;
                return root;
            }
            catch { return null; }
        }

        private string UpdateAllHtmlRow()
        {
            var root = SourceRootForUpdate();
            if (root == null)
                return "<div class='row'><div><div class='t'>Update everything</div><div class='u'>One command that pulls the source repositories, runs the tests and release gate, builds, installs and reopens Recognition. It appears here once you have run <code>UPDATE_ALL.cmd</code> from your source folder once.</div></div></div>";
            return "<div class='row'><div><div class='t'>Update everything</div><div class='u'>Pulls Recognition, SoteriaVault and Constellation (fast-forward only), checks the ecosystem map, runs the tests and release gate, builds, then closes this window, installs and reopens it. Source: " + Esc(root) + ". Nothing is pushed or committed.</div></div>" +
                   "<div class='ts'><a class='btn' onclick=\"send('update-all')\">Update everything</a></div></div>";
        }

        private void RunUpdateAll()
        {
            var root = SourceRootForUpdate();
            if (root == null) { Status("no source folder is known for updating"); return; }
            if (MessageBox.Show(this, "Recognition will close, update from " + root + ", run its checks, install the new build and reopen. If any check fails nothing is installed.\n\nContinue?",
                    "Update everything", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var script = Path.Combine(root, "scripts", "UPDATE_ALL_V1.ps1");
                Process.Start(new ProcessStartInfo("pwsh.exe", "-NoProfile -ExecutionPolicy Bypass -NoExit -File \"" + script + "\" -RepoRoot \"" + root + "\" -CloseApp -Relaunch")
                { UseShellExecute = true, WorkingDirectory = root });
                _actions?.Append("update_all.started", "");
            }
            catch (Exception ex) { Status("could not start the update (is PowerShell 7 installed? winget install Microsoft.PowerShell): " + ex.Message); }
        }
    }
}
