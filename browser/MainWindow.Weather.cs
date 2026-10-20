using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // The weather card on the start page (new tab). The rules are in WeatherForecast.cs and WeatherRadar.cs (executed by tests);
    // the forecast request and the location lookup are in MainWindow.WeatherData.cs.
    //  * The card shows the weather now, hour by hour for a day, and the next 5 days, for the place chosen in Settings or, if you share it, your location.
    //  * Default ("Automatic", the owner's choice): a small chip (emoji, temperature, chance of rain next hour) filled for your location with each new
    //    tab; if Windows location is off, nothing is requested until you click. Settings: "Show when I click" (nothing requested before a click),
    //    "Always show" (your location or the chosen place), "Off".
    //  * Radar: a map from Windy, RainViewer, or an https address of your own, one click away inside the same card.
    //  * On wide windows the card is a panel at the right-hand side of the start page; on narrow ones it sits below the sites.
    // Messages come from the Settings / Customize pages; the start page may only hide the card or ask for the forecast (wxf-).
    public partial class MainWindow
    {
        private string _wxMode = "auto", _wxProvider = "windy", _wxRegion = "us", _wxCustom = "";   // persisted: radar_mode, radar_provider, radar_region, radar_custom
        private double _wxLat = 39.5, _wxLon = -98.35; private int _wxZoom = 4;                      // persisted: radar_lat, radar_lon, radar_zoom
        private string _wxUnit = RegionInfoIsUs() ? "f" : "c";                                         // persisted: radar_unit (f or c)
        private bool _wxHere = true;                                                                  // persisted: weather_here (look up my location when the weather is shown; on unless you turn it off)

        private bool _wxMin = true;                                                                   // persisted: weather_min2 (true = just the small chip: emoji, temperature, chance of rain next hour; false = the full card)

        private static bool RegionInfoIsUs() { try { return RegionInfo.CurrentRegion.TwoLetterISORegionName == "US"; } catch { return true; } }

        private void LoadWeatherSettings(JsonElement r)
        {
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            _wxMode = WeatherRadar.NormalizeMode(S("weather_mode2"));   // new key (the older radar_mode and weather_mode held an "off" saved by the old Hide button, which now only shrinks the card)
            _wxProvider = WeatherRadar.NormalizeProvider(S("radar_provider"));
            var reg = WeatherRadar.FindRegion(S("radar_region"));
            _wxRegion = reg?.Id ?? "";
            if (WeatherRadar.TryCoord(S("radar_lat"), S("radar_lon"), S("radar_zoom"), out var la, out var lo, out var z)) { _wxLat = la; _wxLon = lo; _wxZoom = z; }
            else if (reg != null) { _wxLat = reg.Lat; _wxLon = reg.Lon; _wxZoom = reg.Zoom; _wxRegion = reg.Id; }
            else { var us = WeatherRadar.Regions[0]; _wxRegion = us.Id; _wxLat = us.Lat; _wxLon = us.Lon; _wxZoom = us.Zoom; }
            _wxCustom = WeatherRadar.CustomUrl(S("radar_custom")) ?? "";
            var un = S("radar_unit"); if (un == "f" || un == "c") _wxUnit = un;
            _wxHere = S("weather_here") != "0";
            _wxMin = S("weather_min2") != "0";
        }

        private string WeatherSettingsJson() =>
            J("weather_mode2") + ":" + J(_wxMode) + "," + J("radar_provider") + ":" + J(_wxProvider) + "," + J("radar_region") + ":" + J(_wxRegion) + "," +
            J("radar_lat") + ":" + J(_wxLat.ToString("0.###", CultureInfo.InvariantCulture)) + "," + J("radar_lon") + ":" + J(_wxLon.ToString("0.###", CultureInfo.InvariantCulture)) + "," +
            J("radar_zoom") + ":" + J(_wxZoom.ToString(CultureInfo.InvariantCulture)) + "," + J("radar_custom") + ":" + J(_wxCustom) + "," +
            J("radar_unit") + ":" + J(_wxUnit) + "," + J("weather_here") + ":" + J(_wxHere ? "1" : "0") + "," + J("weather_min2") + ":" + J(_wxMin ? "1" : "0") + ",";

        private string WxPlace() => WeatherRadar.FindRegion(_wxRegion)?.Name ?? _wxLat.ToString("0.##", CultureInfo.InvariantCulture) + ", " + _wxLon.ToString("0.##", CultureInfo.InvariantCulture);

        // ---- the card on the start page -------------------------------------------------------------------------------------
        private string WeatherCardHtml()
        {
            if (_wxMode == "off") return "";
            var radar = WeatherRadar.EmbedUrl(_wxProvider, _wxLat, _wxLon, _wxZoom, _wxCustom);
            var open = WeatherRadar.OpenUrl(_wxProvider, _wxLat, _wxLon, _wxZoom, _wxCustom);
            var sb = new StringBuilder();
            sb.Append("<style>.wx{flex:none;margin-top:30px;width:min(660px,86vw);background:var(--panel);border:1px solid var(--line);border-radius:16px;overflow:hidden}" +
                      ".wxh{display:flex;align-items:center;gap:10px;padding:10px 14px;font-size:12.5px;border-bottom:1px solid var(--line)}.wxh b{font-weight:600}.wxh span{color:var(--mut)}.wxh .wxs{margin-left:auto}" +
                      ".wxh a{color:var(--acc);text-decoration:none;margin-left:10px;cursor:pointer}" +
                      ".wxb{flex:none;min-height:190px;position:relative;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:10px;color:var(--mut);font-size:12.5px;text-align:center;padding:14px}" +
                      ".wxb.wxf{display:block;text-align:left;color:var(--txt);padding:10px 14px 12px;max-height:min(540px,72vh);overflow-y:auto}" +
                      ".wxb.wxr{height:min(440px,62vh);padding:0}.wxb iframe{position:absolute;inset:0;width:100%;height:100%;border:0}" +
                      ".wxb button.wxbtn{position:static;transform:none;width:auto;height:auto;border-radius:999px;padding:9px 18px;font-size:13px}" +
                      ".wxb svg.wxdial{width:56px;height:56px;stroke:var(--acc);fill:none;stroke-width:1.5;opacity:.8}.wxft{margin-top:8px;font-size:11px;color:var(--mut)}.wxft a{color:var(--acc);cursor:pointer;margin-right:12px}" +
                      WeatherForecast.CardCss +
                      ".wxc{flex:none;margin-top:30px;height:42px;padding:0 16px;border-radius:999px;white-space:pre;background:var(--panel);border:1px solid var(--line);align-items:center;justify-content:center;font-size:15px;color:var(--txt);cursor:pointer;user-select:none;transition:transform .12s,border-color .12s}.wxc:hover{transform:translateY(-2px);border-color:var(--acc)}" +
                      "@media(min-width:1100px){.wxc{position:fixed;top:84px;right:18px;margin:0;z-index:5}.wx{position:fixed;top:84px;right:18px;margin:0!important;width:min(400px,29vw)!important;z-index:5;box-shadow:0 8px 28px rgba(0,0,0,.28)}.wxb.wxf{max-height:calc(100vh - 190px)}.wxb.wxr{height:min(460px,calc(100vh - 190px))}}</style>");
            sb.Append("<div class='wx' id='wx'" + (_wxMin ? " style='display:none'" : "") + "><div class='wxh'><b>Weather</b><span id='wxplace'>" + Esc(WxPlace()) + "</span><span class='wxs'>" +
                      (radar != null ? "<a id='wxrb' onclick='wxRadar()'>Radar</a>" : "") + "<a onclick=\"wxMin(1)\">Hide</a></span></div>");
            sb.Append("<div class='wxb' id='wxb' data-radar='" + Attr(radar ?? "") + "' data-open='" + Attr(open ?? "") + "'>" +
                      "<svg class='wxdial' viewBox='0 0 64 64'><circle cx='32' cy='32' r='28'/><path d='M20 40h24a9 9 0 000-18 13 13 0 00-25 3A8 8 0 0020 40z'/></svg>" +
                      "<button class='wxbtn' type='button' onclick='wxGo(" + (_wxHere ? "1" : "0") + ")'>Show the weather</button>" +
                      "<div>" + (_wxMode == "click" ? "Nothing is requested until you click. " : "") + "Only a latitude and longitude is sent, to Open-Meteo." +
                      (_wxHere ? "" : " <a style='color:var(--acc);cursor:pointer' onclick='wxGo(1)'>Use my location</a>") + "</div></div></div>");
            // hidden: the card shrinks to a small chip showing the weather as an emoji; clicking the chip brings the card back
            sb.Append("<div class='wxc' id='wxc' title='Weather' onclick='wxMin(0)' style='display:" + (_wxMin ? "flex" : "none") + "'>\U0001F324️</div>");
            // mode: 2 = automatic (my location, ask first if it is off), 1/0 = always show (location / chosen place), -1 = only after a click
            sb.Append("<script>(function(){var mode=" + (_wxMode == "auto" ? "2" : _wxMode == "live" ? (_wxHere ? "1" : "0") : "-1") + ",here=" + (_wxHere ? "1" : "0") + ";var showing='f';" +
                      "window.wxGo=function(h){var b=document.getElementById('wxb');b.className='wxb';b.innerHTML='<div>Loading the weather&hellip;</div>';showing='f';var r=document.getElementById('wxrb');if(r)r.textContent='Radar';window.chrome.webview.postMessage(h===2?'wxf-auto':h?'wxf-here':'wxf-get');};" +
                      "window.wxRadar=function(){var b=document.getElementById('wxb'),r=document.getElementById('wxrb');if(showing==='r'){window.wxGo(here);return;}" +
                      "showing='r';b.className='wxb wxr';b.innerHTML='';var f=document.createElement('iframe');f.src=b.dataset.radar;f.setAttribute('sandbox','allow-scripts allow-same-origin allow-popups');f.setAttribute('referrerpolicy','no-referrer');f.title='Weather radar';b.appendChild(f);if(r)r.textContent='Forecast';};" +
                      "window.wxMin=function(m){document.getElementById('wx').style.display=m?'none':'';document.getElementById('wxc').style.display=m?'flex':'none';window.chrome.webview.postMessage('wxf-min:'+m);};" +
                      "window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m||m.type!=='wxf'||showing!=='f')return;var b=document.getElementById('wxb');b.className='wxb wxf';b.innerHTML=m.html;var p=document.getElementById('wxplace');if(p&&m.place)p.textContent=m.place;var c=document.getElementById('wxc');if(c&&(m.chip||m.emoji)){c.textContent=m.chip||m.emoji;c.title=(m.tip?m.tip+' - ':'')+'click for the full weather';}});" +
                      "if(mode>=0)window.wxGo(mode);})();</script>");
            return sb.ToString();
        }

        // ---- settings -----------------------------------------------------------------------------------------------------------
        private string WeatherSettingsHtml()
        {
            string Btn(string msg, bool on, string label) => "<a class='btn" + (on ? "" : " ghost") + "' onclick=\"send('" + msg + "')\">" + label + "</a> ";
            var sb = new StringBuilder();
            sb.Append("<div class='row'><div><div class='t'>Weather on the start page</div><div class='u'>Shows the weather now, hour by hour, and the next 5 days beside the sites on new tabs. \"Automatic\" (the default) shows it with every new tab for where you are, and if your location is off it shows a button first instead. \"Always show\" shows it with every new tab for your location, or the place below if location is off. \"Show when I click\" loads nothing until you press the button. Whenever it loads, Open-Meteo sees your IP address and a latitude and longitude.</div></div><div class='ts'>" +
                      Btn("wx-mode:auto", _wxMode == "auto", "Automatic") + Btn("wx-mode:click", _wxMode == "click", "Show when I click") + Btn("wx-mode:live", _wxMode == "live", "Always show") + Btn("wx-mode:off", _wxMode == "off", "Off (remove it)") + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>Use my location</div><div class='u'>When on, the weather is for where you are, if you share your location with Windows (Settings, Privacy &amp; security, Location, and let desktop apps use it). The position is asked for only when the weather is shown, rounded to about 1 km, kept in memory only and never saved. If location is not shared, the place below is used.</div></div><div class='ts'>" +
                      Btn("wx-here:1", _wxHere, "On") + Btn("wx-here:0", !_wxHere, "Off") + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>Temperature</div><div class='u'>Degrees Fahrenheit with miles per hour, or Celsius with kilometres per hour.</div></div><div class='ts'>" +
                      Btn("wx-unit:f", _wxUnit == "f", "&deg;F") + Btn("wx-unit:c", _wxUnit == "c", "&deg;C") + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>Place</div><div class='u'>Now: " + Esc(WxPlace()) + " (latitude " + _wxLat.ToString("0.###", CultureInfo.InvariantCulture) + ", longitude " + _wxLon.ToString("0.###", CultureInfo.InvariantCulture) + ", radar zoom " + _wxZoom + ").</div></div><div class='ts'>" +
                      "<select onchange=\"send('wx-region:'+this.value)\" style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'><option value=''>Choose a region...</option>" +
                      string.Concat(WeatherRadar.Regions.Select(g => "<option value='" + g.Id + "'" + (g.Id == _wxRegion ? " selected" : "") + ">" + Esc(g.Name) + "</option>")) + "</select> " +
                      "<input id='wxla' placeholder='lat' value='" + _wxLat.ToString("0.###", CultureInfo.InvariantCulture) + "' style='width:70px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'> " +
                      "<input id='wxlo' placeholder='lon' value='" + _wxLon.ToString("0.###", CultureInfo.InvariantCulture) + "' style='width:70px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'> " +
                      "<input id='wxzm' placeholder='zoom' value='" + _wxZoom + "' style='width:50px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'> " +
                      "<a class='btn ghost' onclick=\"send('wx-loc:'+encodeURIComponent(document.getElementById('wxla').value)+'|'+encodeURIComponent(document.getElementById('wxlo').value)+'|'+encodeURIComponent(document.getElementById('wxzm').value))\">Set</a></div></div>");
            sb.Append("<div class='row'><div><div class='t'>Radar map</div><div class='u'>The Radar link in the card shows a live rain map from Windy or RainViewer, or an https page of your own. Nothing is loaded from it until you press Radar.</div></div><div class='ts'>" +
                      Btn("wx-provider:windy", _wxProvider == "windy", "Windy") + Btn("wx-provider:rainviewer", _wxProvider == "rainviewer", "RainViewer") + Btn("wx-provider:custom", _wxProvider == "custom", "My own address") + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>My own radar address</div><div class='u'>" + (_wxCustom.Length > 0 ? "Now: " + Esc(_wxCustom) : "Not set.") + " Only https addresses are accepted.</div></div><div class='ts'>" +
                      "<input id='wxcu' placeholder='https://...' style='width:230px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'> " +
                      "<a class='btn' onclick=\"send('wx-custom:'+encodeURIComponent(document.getElementById('wxcu').value))\">Use this</a></div></div>");
            return sb.ToString();
        }

        private void HandleWeatherMessage(BrowserTab from, string msg)
        {
            try
            {
                if (msg.StartsWith("wx-mode:", StringComparison.Ordinal)) _wxMode = WeatherRadar.NormalizeMode(msg.Substring("wx-mode:".Length));
                else if (from.Internal == "start") return;   // the start page may only hide the card
                else if (msg.StartsWith("wx-here:", StringComparison.Ordinal)) _wxHere = msg.EndsWith("1", StringComparison.Ordinal);
                else if (msg.StartsWith("wx-unit:", StringComparison.Ordinal)) _wxUnit = msg.EndsWith("c", StringComparison.Ordinal) ? "c" : "f";
                else if (msg.StartsWith("wx-provider:", StringComparison.Ordinal))
                {
                    _wxProvider = WeatherRadar.NormalizeProvider(msg.Substring("wx-provider:".Length));
                    if (_wxProvider == "custom" && _wxCustom.Length == 0) Status("add your radar address below, then it will show");
                }
                else if (msg.StartsWith("wx-region:", StringComparison.Ordinal))
                {
                    var g = WeatherRadar.FindRegion(msg.Substring("wx-region:".Length)); if (g == null) return;
                    _wxRegion = g.Id; _wxLat = g.Lat; _wxLon = g.Lon; _wxZoom = g.Zoom;
                }
                else if (msg.StartsWith("wx-loc:", StringComparison.Ordinal))
                {
                    var p = msg.Substring("wx-loc:".Length).Split('|').Select(Dec).ToArray();
                    if (p.Length != 3 || !WeatherRadar.TryCoord(p[0], p[1], p[2], out var la, out var lo, out var z)) { Status("latitude -85 to 85, longitude -180 to 180, zoom 2 to 11"); return; }
                    _wxRegion = ""; _wxLat = la; _wxLon = lo; _wxZoom = z;
                }
                else if (msg.StartsWith("wx-custom:", StringComparison.Ordinal))
                {
                    var u = WeatherRadar.CustomUrl(Dec(msg.Substring("wx-custom:".Length)));
                    if (u == null) { Status("that is not an https address I can show"); return; }
                    _wxCustom = u; _wxProvider = "custom";
                }
                else return;
                SaveSettings(); _actions?.Append("radar.setting", msg.Substring(0, Math.Min(msg.IndexOf(':'), 20)));
                ReloadStartTabs();
                if (from.Internal is "settings" or "customize") LoadInternal(from, from.Internal);
            }
            catch (Exception ex) { Status("weather setting failed: " + ex.Message); }
        }
    }
}
