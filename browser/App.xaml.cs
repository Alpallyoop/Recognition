using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Recognition.Browser
{
    // Start-up: a light splash while the engine and the locked-start checks run, one running copy per install (a second launch hands
    // its address to the first and exits), and the addresses Windows passes when Recognition is your default browser.
    public partial class App : Application
    {
        // Addresses to open once the first tab exists. Filled from the command line and from the second-launch messages.
        internal static readonly ConcurrentQueue<string> PendingUrls = new();
        internal static string? PendingSpecial;   // "--private" or "--new-tab" from the taskbar menu, until the first tab exists

        private Mutex? _mutex;
        private CancellationTokenSource? _cts;

        protected override void OnStartup(StartupEventArgs e)
        {
            var url = LaunchArgs.FromArgs(e.Args, File.Exists);
            // the taskbar menu starts the program with one of two fixed switches; nothing else is read from them
            string? special = Array.IndexOf(e.Args, "--private") >= 0 ? "--private" : Array.IndexOf(e.Args, "--new-tab") >= 0 ? "--new-tab" : null;
            var id = InstanceId();
            try
            {
                _mutex = new Mutex(true, "Local\\RecognitionBrowser_" + id, out bool first);
                // "Restart to apply" starts the new copy while the old one is still closing: wait for it instead of handing it the address
                if (!first && Array.IndexOf(e.Args, "--restart") >= 0)
                {
                    try { first = _mutex.WaitOne(12000); } catch (AbandonedMutexException) { first = true; }
                }
                if (!first) { SendToRunning(id, special ?? url ?? ""); Shutdown(); return; }
            }
            catch { /* no mutex: run anyway, never block the browser from starting */ }

            if (url != null) PendingUrls.Enqueue(url);
            if (special != null) PendingSpecial = special;
            ShowSplash();
            base.OnStartup(e);
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => PipeLoop(id, _cts.Token));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }

        // one id per install location, so a development build and the installed one do not hand addresses to each other
        private static string InstanceId()
        {
            var b = SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant()));
            return Convert.ToHexString(b, 0, 8).ToLowerInvariant();
        }

        private static void SendToRunning(string id, string text)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", "RecognitionBrowser_" + id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                c.Connect(4000);
                var bytes = Encoding.UTF8.GetBytes(text.Length > LaunchArgs.MaxLen ? "" : text);
                c.Write(bytes, 0, bytes.Length);
                c.Flush();
            }
            catch { }
        }

        private async Task PipeLoop(string id, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // only this Windows user can connect; whatever arrives is validated again by LaunchArgs.Parse
                    using var s = new NamedPipeServerStream("RecognitionBrowser_" + id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync(ct);
                    using var rd = CancellationTokenSource.CreateLinkedTokenSource(ct); rd.CancelAfter(TimeSpan.FromSeconds(3));
                    var buf = new byte[LaunchArgs.MaxLen * 3 + 8]; int total = 0;
                    while (total < buf.Length) { int n = await s.ReadAsync(buf.AsMemory(total), rd.Token); if (n == 0) break; total += n; }
                    var text = total >= buf.Length ? "" : Encoding.UTF8.GetString(buf, 0, total).Trim();
                    if (text == "--private" || text == "--new-tab") { var sp = text; _ = Dispatcher.BeginInvoke(new Action(() => { FindMain()?.OpenSpecialFromOutside(sp); })); continue; }
                    var url = text.Length == 0 ? "" : LaunchArgs.Parse(text, File.Exists);
                    if (url != null) _ = Dispatcher.BeginInvoke(new Action(() => { FindMain()?.OpenFromOutside(url); }));
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
                catch { try { await Task.Delay(300, ct); } catch { break; } }
            }
        }

        // The splash is the first window created, so Application.MainWindow points at it; look the real window up instead.
        private static global::Recognition.Browser.MainWindow? FindMain()
        {
            foreach (Window w in Current.Windows) if (w is global::Recognition.Browser.MainWindow m) return m;
            return null;
        }

        // ---- splash ------------------------------------------------------------------------------------------------------------
        // No separate loading window: the main window opens at once and shows a small "starting" cover inside itself,
        // which is removed the moment start-up finishes (MainWindow.HideLoading).
        private void ShowSplash() { }

        private void CloseSplash() { try { FindMain()?.HideLoading(); } catch { } }
        internal static void HideSplash() { try { (Current as App)?.CloseSplash(); } catch { } }
    }
}
