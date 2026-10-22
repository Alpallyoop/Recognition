using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Recognition.Browser
{
    // Writing integration receipts (constellation.integration.receipt.v1) for the integrations this browser starts (IntegrationReceipts.cs
    // has the rules). Kept out of MainWindow.Soteria.cs on purpose: the SoteriaVault link itself stays read-only (a static check in
    // scripts\_selftest_recognition_browser_governed_actions_v1.ps1 forbids writes there).
    //
    // Two witnesses (docs\proposals\0005-nfl-witness-for-integration-receipts.md):
    //   * the browser's own: every receipt hash in the DPAPI-encrypted, hash-chained action ledger;
    //   * NFL's, when NFL is found (RECOGNITION_NFL_ROOT, or the dev-root layout above this repository): before writing, NFL is asked
    //     which receipt it witnessed last ("receipt-head", NFL proposal 0003) and that receipt must still be in the file; after writing,
    //     the new receipt is handed to NFL at once ("witness-receipt"). NFL missing or slow never blocks: the receipt is written
    //     locally and the status bar says NFL was not reached.
    public partial class MainWindow
    {
        private string SoteriaReceiptDir => Path.Combine(_repoRoot, "proofs", "integration_receipts", "recognition__soteriavault");
        private string SoteriaReceiptPath => Path.Combine(SoteriaReceiptDir, "receipts.ndjson");
        private readonly SemaphoreSlim _receiptGate = new(1, 1);
        private const int NflTimeoutMs = 30_000;

        private string? FindNflRoot() =>
            IntegrationReceipts.FindNfl(Environment.GetEnvironmentVariable("RECOGNITION_NFL_ROOT"), _repoRoot,
                d => File.Exists(Path.Combine(d, "scripts", "nfl_cli_v1.ps1")));

        // Runs one NFL CLI command in NFL's own folder (argument list, never a shell string). Null when it could not run in time.
        private static async Task<string?> RunNflAsync(string nflRoot, params string[] args)
        {
            foreach (var host in new[] { "pwsh.exe", "powershell.exe" })
            {
                try
                {
                    var psi = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = nflRoot, WindowStyle = ProcessWindowStyle.Hidden };
                    foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(nflRoot, "scripts", "nfl_cli_v1.ps1") }.Concat(args).Concat(new[] { "-RepoRoot", nflRoot })) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
                    using var cts = new CancellationTokenSource(NflTimeoutMs);
                    try { await p.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
                    return await outTask + "\n" + await errTask;
                }
                catch (System.ComponentModel.Win32Exception) { /* this PowerShell is not installed: try the next */ }
            }
            return null;
        }

        private void WriteSoteriaReceipt(SoteriaStatus st) => _ = WriteSoteriaReceiptAsync(st);

        private async Task WriteSoteriaReceiptAsync(SoteriaStatus st)
        {
            var o = IntegrationReceipts.SoteriaOutcome(st.State); if (o == null) return;
            await WriteSoteriaExchangeReceiptAsync(SoteriaBridge.ContractSchema, null, IntegrationReceipts.Sha(st.ContractSha256) ? st.ContractSha256 : null,
                o.Value.Outcome, o.Value.Error, "SoteriaVault link checked");
        }

        // One receipt per exchange with SoteriaVault (link check, password list / fill / save / import over the bridge).
        // The caller passes hashes taken with every password blanked (SoteriaPasswords.RedactedSha).
        private async Task WriteSoteriaExchangeReceiptAsync(string? contract, string? requestSha256, string? responseSha256, string outcome, string? error, string what)
        {
            const string integration = "recognition>soteriavault";
            await _receiptGate.WaitAsync();
            try
            {
                // the first version (2026-10-21) used another file name, which the Witness Receipts button does not look for
                var old = Path.Combine(SoteriaReceiptDir, "constellation.integration.receipt.v1.ndjson");
                if (File.Exists(old) && !File.Exists(SoteriaReceiptPath)) File.Move(old, SoteriaReceiptPath);

                // 1. ask NFL what it witnessed last (if NFL is here)
                var nflRoot = FindNflRoot();
                IntegrationReceipts.NflView? nfl = null;
                if (nflRoot != null)
                {
                    var outText = await RunNflAsync(nflRoot, "receipt-head", "-Integration", integration);
                    nfl = IntegrationReceipts.ParseNflHead(outText, integration) ?? new IntegrationReceipts.NflView(false, 0, IntegrationReceipts.Genesis, "NFL did not answer");
                }

                // 2. check both witnesses, write the receipt, record it in the browser's own witness
                var witnessed = IntegrationReceipts.Witnessed(_actions.Items.Select(i => i.Action), "recognition", "soteriavault");
                var (h, note) = IntegrationReceipts.AppendWitnessed(SoteriaReceiptPath, witnessed,
                    hash => _actions.Append(IntegrationReceipts.WitnessEntry("recognition", "soteriavault", hash)), nfl,
                    "recognition", "soteriavault", "consumer", contract, requestSha256,
                    responseSha256, Array.Empty<IntegrationCarried>(), outcome, error);

                // 3. hand the new receipt to NFL at once (best effort: witnessing never blocks the integration)
                string nflNote;
                if (nflRoot == null) nflNote = "NFL not found on this computer";
                else if (nfl is { Available: false }) nflNote = "NFL not reached";
                else
                {
                    var line = File.ReadAllText(SoteriaReceiptPath).TrimEnd('\n').Split('\n')[^1];
                    var w = IntegrationReceipts.ParseNflWitness(await RunNflAsync(nflRoot, "witness-receipt", "-ReceiptLine", line), h);
                    nflNote = w switch { "WITNESSED" or "ALREADY_WITNESSED" => "witnessed by NFL", null => "NFL did not confirm", _ => "NFL " + w };
                }
                if (what.Length > 0) Status(what + ": " + outcome + " (integration receipt " + h.Substring(0, 12) + ", witnessed by the browser, " + nflNote + ")" + (note != null ? "; " + note : ""));
            }
            catch (Exception ex) { Status(what + ", but the integration receipt was NOT written: " + ex.Message); }
            finally { _receiptGate.Release(); }
        }
    }
}
