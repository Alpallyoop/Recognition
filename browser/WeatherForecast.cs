using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // Pure rules for the forecast on the start page weather card (no WPF, files or network; executed by browser.tests).
    // The forecast comes from Open-Meteo (no key, no account) and is requested only after you press the button
    // (or choose "Always show"); the request carries only a latitude and longitude.
    internal sealed record WxNow(int Temp, string Text, int Wind, bool Day, int Code);
    internal sealed record WxHour(string Label, int Temp, int Rain, string Text, int Code, bool Day);
    internal sealed record WxDay(string Name, int Hi, int Lo, int Rain, string Text, int Code);
    internal sealed record Forecast(WxNow Now, WxHour[] Hours, WxDay[] Days, string City);

    internal static class WeatherForecast
    {
        public const string Host = "api.open-meteo.com";

        public static string Url(double lat, double lon, bool fahrenheit)
        {
            string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
            return "https://" + Host + "/v1/forecast?latitude=" + N(lat) + "&longitude=" + N(lon) +
                   "&current=temperature_2m,weather_code,is_day,wind_speed_10m" +
                   "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
                   "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                   "&forecast_days=5&timezone=auto&temperature_unit=" + (fahrenheit ? "fahrenheit" : "celsius") +
                   "&wind_speed_unit=" + (fahrenheit ? "mph" : "kmh");
        }

        public static string Describe(int code) => code switch
        {
            0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 or 56 or 57 => "Drizzle",
            61 or 63 or 65 or 66 or 67 => "Rain",
            71 or 73 or 75 or 77 => "Snow",
            80 or 81 or 82 => "Showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm", 96 or 99 => "Thunder and hail",
            _ => "Mixed"
        };

        // The emoji shown on the small chip when the card is hidden.
        public static string Emoji(int code, bool day) => code switch
        {
            0 => day ? "☀️" : "\U0001F319",
            1 => day ? "\U0001F324️" : "\U0001F319",
            2 => day ? "⛅" : "☁️",
            3 => "☁️",
            45 or 48 => "\U0001F32B️",
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 => "\U0001F327️",
            80 or 81 or 82 => "\U0001F326️",
            71 or 73 or 75 or 77 or 85 or 86 => "\U0001F328️",
            95 or 96 or 99 => "⛈️",
            _ => "\U0001F321️"
        };

        // The text of the small chip: weather emoji, temperature, and the chance of rain in the next hour.
        public static string ChipText(Forecast f)
        {
            var next = f.Hours.Length > 1 ? f.Hours[1] : f.Hours[0];
            return Emoji(f.Now.Code, f.Now.Day) + " " + f.Now.Temp + "°  \U0001F4A7" + next.Rain + "%";
        }

        // A small drawing for the condition (inline SVG, drawn in the current text colour).
        public static string Icon(int code, bool day)
        {
            const string cloud = "<path d='M7 18h10a4 4 0 000-8 5.5 5.5 0 00-10.6 1.2A3.5 3.5 0 007 18z'/>";
            string body = code switch
            {
                0 or 1 => day ? "<circle cx='12' cy='12' r='4'/><path d='M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4'/>" : "<path d='M20 14.5A8 8 0 019.5 4 8 8 0 1020 14.5z'/>",
                2 => "<circle cx='8' cy='8' r='2.6'/><path d='M8 3v1.2M3 8h1.2M4.5 4.5l.9.9M11.5 4.5l-.9.9'/><path d='M9 19h8a3.4 3.4 0 000-6.8 4.6 4.6 0 00-8.8 1A3 3 0 009 19z'/>",
                3 => cloud,
                45 or 48 => "<path d='M4 9h16M6 13h12M4 17h16'/>",
                51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => cloud + "<path d='M9 20l1-2M13 20l1-2M17 20l1-2'/>",
                71 or 73 or 75 or 77 or 85 or 86 => cloud + "<path d='M9 20h.01M13 21h.01M17 20h.01'/>",
                95 or 96 or 99 => cloud + "<path d='M12 14l-2 3h3l-2 3'/>",
                _ => cloud
            };
            return "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'>" + body + "</svg>";
        }

        // "America/Chicago" -> "Chicago"; anything odd -> "".
        public static string CityFromZone(string? zone)
        {
            var z = (zone ?? "").Trim();
            var i = z.LastIndexOf('/');
            var c = (i >= 0 ? z.Substring(i + 1) : z).Replace('_', ' ');
            return c.Length > 0 && c.Length <= 40 && c.All(ch => char.IsLetter(ch) || ch == ' ' || ch == '-' || ch == '\'') ? c : "";
        }

        private static int R(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : 0;

        public static bool TryParse(string? json, out Forecast? f)
        {
            f = null;
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > 400_000) return false;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var cur = root.GetProperty("current");
                var now = new WxNow(R(cur.GetProperty("temperature_2m")), Describe(R(cur.GetProperty("weather_code"))), R(cur.GetProperty("wind_speed_10m")),
                                    R(cur.GetProperty("is_day")) == 1, R(cur.GetProperty("weather_code")));
                var curTime = cur.GetProperty("time").GetString() ?? "";
                var hourStart = curTime.Length >= 13 ? curTime.Substring(0, 13) : "";   // yyyy-MM-ddTHH

                var h = root.GetProperty("hourly");
                var ht = h.GetProperty("time"); var hT = h.GetProperty("temperature_2m"); var hC = h.GetProperty("weather_code");
                var hP = h.GetProperty("precipitation_probability"); var hD = h.GetProperty("is_day");
                var hours = new System.Collections.Generic.List<WxHour>();
                for (int i = 0; i < ht.GetArrayLength() && hours.Count < 24; i++)
                {
                    var t = ht[i].GetString() ?? "";
                    if (string.CompareOrdinal(t.Length >= 13 ? t.Substring(0, 13) : t, hourStart) < 0) continue;
                    if (!DateTime.TryParseExact(t, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) continue;
                    var code = R(hC[i]);
                    hours.Add(new WxHour(hours.Count == 0 ? "Now" : dt.ToString("h tt", CultureInfo.InvariantCulture), R(hT[i]), R(hP[i]), Describe(code), code, R(hD[i]) == 1));
                }

                var d = root.GetProperty("daily");
                var dtm = d.GetProperty("time"); var dC = d.GetProperty("weather_code"); var dH = d.GetProperty("temperature_2m_max");
                var dL = d.GetProperty("temperature_2m_min"); var dP = d.GetProperty("precipitation_probability_max");
                var days = new System.Collections.Generic.List<WxDay>();
                for (int i = 0; i < dtm.GetArrayLength() && days.Count < 5; i++)
                {
                    if (!DateTime.TryParseExact(dtm[i].GetString() ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                    var code = R(dC[i]);
                    days.Add(new WxDay(days.Count == 0 ? "Today" : day.ToString("dddd", CultureInfo.InvariantCulture), R(dH[i]), R(dL[i]), R(dP[i]), Describe(code), code));
                }
                if (hours.Count == 0 || days.Count == 0) return false;
                var zone = root.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String ? tz.GetString() : "";
                f = new Forecast(now, hours.ToArray(), days.ToArray(), CityFromZone(zone));
                return true;
            }
            catch { f = null; return false; }
        }

        private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        // The inside of the card: now, the next 24 hours, the next 5 days. Everything text-shaped is encoded.
        public static string BodyHtml(Forecast f, bool fahrenheit, string place)
        {
            var u = fahrenheit ? "°F" : "°C";
            var w = fahrenheit ? "mph" : "km/h";
            var sb = new StringBuilder();
            sb.Append("<div class='wxn'><span class='wxi'>" + Icon(f.Now.Code, f.Now.Day) + "</span><div><b>" + f.Now.Temp + u + "</b><span>" + E(f.Now.Text) + " &middot; wind " + f.Now.Wind + " " + w + "</span></div></div>");
            if (place.Length > 0) sb.Append("<div class='wxp'>" + E(place) + "</div>");
            sb.Append("<div class='wxt'>Hour by hour</div><div class='wxhs'>");
            foreach (var h in f.Hours)
                sb.Append("<div class='wxh1' title='" + E(h.Text) + "'><span>" + E(h.Label) + "</span><i>" + Icon(h.Code, h.Day) + "</i><b>" + h.Temp + "°</b><em>" + (h.Rain >= 10 ? h.Rain + "%" : "&nbsp;") + "</em></div>");
            sb.Append("</div><div class='wxt'>Next 5 days</div><div class='wxds'>");
            foreach (var d in f.Days)
                sb.Append("<div class='wxd1'><span>" + E(d.Name) + "</span><i>" + Icon(d.Code, true) + "</i><span class='wxx'>" + E(d.Text) + (d.Rain >= 10 ? " &middot; " + d.Rain + "%" : "") + "</span><b>" + d.Hi + "°</b><em>" + d.Lo + "°</em></div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        public const string CardCss =
            ".wxn{display:flex;align-items:center;gap:14px;padding:6px 4px 2px}.wxn b{display:block;font-size:34px;font-weight:600;line-height:1.05}.wxn span{color:var(--mut);font-size:12.5px}" +
            ".wxi svg,.wxh1 i svg,.wxd1 i svg{width:100%;height:100%;stroke:var(--acc)}.wxi{width:46px;height:46px;display:block}" +
            ".wxp{color:var(--mut);font-size:11.5px;padding:0 4px 6px}.wxt{font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:var(--mut);padding:8px 4px 4px}" +
            // thin scroll bars in the card's own colours (the plain Windows bars were white)
            ".wxhs,.wxb.wxf{scrollbar-width:thin;scrollbar-color:var(--line) transparent}.wxhs::-webkit-scrollbar,.wxb.wxf::-webkit-scrollbar{height:6px;width:6px;background:transparent}" +
            ".wxhs::-webkit-scrollbar-track,.wxb.wxf::-webkit-scrollbar-track{background:transparent}.wxhs::-webkit-scrollbar-thumb,.wxb.wxf::-webkit-scrollbar-thumb{background:var(--line);border-radius:6px}" +
            ".wxhs::-webkit-scrollbar-thumb:hover,.wxb.wxf::-webkit-scrollbar-thumb:hover{background:var(--mut)}.wxhs::-webkit-scrollbar-button,.wxb.wxf::-webkit-scrollbar-button{display:none}" +
            ".wxhs{display:flex;gap:4px;overflow-x:auto;padding-bottom:6px}.wxh1{flex:none;width:54px;display:flex;flex-direction:column;align-items:center;gap:3px;font-size:12px;padding:6px 0;border-radius:10px;background:rgba(127,127,127,.08)}" +
            ".wxh1 span{color:var(--mut);font-size:11px}.wxh1 i{display:block;width:22px;height:22px}.wxh1 em,.wxd1 em{font-style:normal;color:var(--mut);font-size:11px}" +
            ".wxds{display:flex;flex-direction:column;gap:2px}.wxd1{display:grid;grid-template-columns:78px 24px 1fr 34px 34px;align-items:center;gap:8px;font-size:12.5px;padding:5px 4px;border-radius:8px}" +
            ".wxd1:hover{background:rgba(127,127,127,.08)}.wxd1 i{display:block;width:22px;height:22px}.wxd1 .wxx{color:var(--mut);font-size:11.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.wxd1 b{text-align:right;font-weight:600}.wxd1 em{text-align:right}";
    }
}
