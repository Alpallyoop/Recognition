using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Recognition.Browser
{
    // Where the weather card gets its numbers. This is the only place the weather feature touches the network or the system:
    //  * the forecast: one https request to api.open-meteo.com carrying only a latitude and longitude (no key, no account, no cookies),
    //    made only when you press the button on the card (or chose "Always show");
    //  * your location: only when "Use my location" is on or you press that link, read from Windows Location through a short-lived
    //    PowerShell helper, so Windows' own privacy switch decides. The position is rounded to about 1 km, kept in memory for ten
    //    minutes and never written to disk.
    public partial class MainWindow
    {
        private static readonly HttpClient WxHttp = MakeWxHttp();
        private (double lat, double lon, DateTime at)? _wxFix;

        private static HttpClient MakeWxHttp()
        {
            var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Recognition-weather/1.0");
            return h;
        }

        private static async Task<string?> FetchForecastAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, WeatherForecast.Host, StringComparison.OrdinalIgnoreCase)) return null;
            using var r = await WxHttp.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
            if (!r.IsSuccessStatusCode || r.Content.Headers.ContentLength > 400_000) return null;
            var bytes = await r.Content.ReadAsByteArrayAsync();
            return bytes.Length > 400_000 ? null : Encoding.UTF8.GetString(bytes);
        }

        private const string LocateScript =
            "Add-Type -AssemblyName System.Device; $w=New-Object System.Device.Location.GeoCoordinateWatcher; $null=$w.Start(); $n=0; " +
            "while($w.Status -ne 'Ready' -and $w.Permission -ne 'Denied' -and $n -lt 120){Start-Sleep -Milliseconds 100; $n++}; " +
            "if($w.Status -eq 'Ready' -and -not $w.Position.Location.IsUnknown){$c=$w.Position.Location; " +
            "'OK ' + $c.Latitude.ToString('0.00',[cultureinfo]::InvariantCulture) + ' ' + $c.Longitude.ToString('0.00',[cultureinfo]::InvariantCulture)} else {'NO'}";

        private async Task<(double lat, double lon)?> LocateAsync()
        {
            if (_wxFix is { } f && DateTime.UtcNow - f.at < TimeSpan.FromMinutes(10)) return (f.lat, f.lon);
            if (DateTime.UtcNow - _wxFixFailed < TimeSpan.FromMinutes(5)) return null;   // location was off a moment ago: do not ask Windows again on every new tab
            var result = await LocateOnceAsync();
            if (result == null) _wxFixFailed = DateTime.UtcNow;
            return result;
        }

        private DateTime _wxFixFailed = DateTime.MinValue;

        private async Task<(double lat, double lon)?> LocateOnceAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
                psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(LocateScript);
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outTask = p.StandardOutput.ReadToEndAsync();
                var done = await Task.Run(() => p.WaitForExit(16000));
                if (!done) { try { p.Kill(true); } catch { } return null; }
                var parts = (await outTask).Trim().Split(' ');
                if (parts.Length == 3 && parts[0] == "OK" && WeatherRadar.TryCoord(parts[1], parts[2], "4", out var la, out var lo, out _))
                {
                    _wxFix = (la, lo, DateTime.UtcNow);
                    return (la, lo);
                }
            }
            catch { }
            return null;
        }

        // From the start page: "wxf-get" (the saved place, or my location when that is switched on) or "wxf-here" (my location now).
        private async void HandleForecastMessage(BrowserTab tab, string msg)
        {
            if (msg.StartsWith("wxf-min:", StringComparison.Ordinal))   // hide the card down to the small emoji, or bring it back
            {
                var want = msg.EndsWith("1", StringComparison.Ordinal);
                if (want != _wxMin) { _wxMin = want; SaveSettings(); }
                return;
            }
            string html; string place = ""; string emoji = "", tip = "", chip = "";
            try
            {
                double lat = _wxLat, lon = _wxLon; place = WxPlace(); var note = "";
                if (msg == "wxf-here" || (msg == "wxf-auto" && _wxHere))
                {
                    var fix = await LocateAsync();
                    if (fix is { } g) { lat = g.lat; lon = g.lon; place = "Your location"; _actions?.Append("weather.location", "shared"); }
                    else if (msg == "wxf-auto")
                    {
                        // automatic mode: location is off, so nothing is requested from the forecast service until you choose
                        var offHtml = "<div class='wxft'>Your location is not being shared with this app (Windows Settings, Privacy &amp; security, Location), so the weather was not loaded.</div>" +
                                      "<div style='margin-top:10px;display:flex;gap:10px;flex-wrap:wrap'><button class='wxbtn' type='button' onclick='wxGo(0)'>Show " + Esc(place) + "</button></div>";
                        if (_tabs.Contains(tab) && tab.Internal == "start" && tab.Web.CoreWebView2 != null)
                            tab.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "wxf", html = offHtml, place }));
                        return;
                    }
                    else note ="Your location is not being shared with this app (Windows Settings, Privacy & security, Location), so this is the weather for " + place + ".";
                }
                var fahrenheit = _wxUnit == "f";
                var json = await FetchForecastAsync(WeatherForecast.Url(lat, lon, fahrenheit));
                if (WeatherForecast.TryParse(json, out var fc) && fc != null)
                {
                    if (place == "Your location" && fc.City.Length > 0) place = "Your location, near " + fc.City;
                    emoji = WeatherForecast.Emoji(fc.Now.Code, fc.Now.Day); chip = WeatherForecast.ChipText(fc); tip =fc.Now.Temp + (fahrenheit ? "°F " : "°C ") + fc.Now.Text;
                    html = WeatherForecast.BodyHtml(fc, fahrenheit, "") +
                           (note.Length > 0 ? "<div class='wxft'>" + Esc(note) + "</div>" : "") +
                           "<div class='wxft'><a onclick='wxGo(1)'>Use my location</a><a onclick='wxGo(0)'>Refresh</a>Forecast: Open-Meteo</div>";
                    _actions?.Append("weather.forecast", "shown");
                }
                else html = "<div class='wxft'>The forecast could not be loaded right now.</div><div class='wxft'><a onclick='wxGo(0)'>Try again</a></div>";
            }
            catch { html = "<div class='wxft'>The forecast could not be loaded (no connection?).</div><div class='wxft'><a onclick='wxGo(0)'>Try again</a></div>"; }
            try
            {
                if (_tabs.Contains(tab) && tab.Internal == "start" && tab.Web.CoreWebView2 != null)
                    tab.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "wxf", html, place, emoji, chip, tip }));
            }
            catch { }
        }
    }
}
