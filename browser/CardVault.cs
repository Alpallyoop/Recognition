using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // Payment cards and the unlock PIN. Pure logic (no WPF/WebView2): browser.tests executes every rule here.
    //
    //  - The CVC / security code is NEVER stored, so it is never filled. The person types it.
    //  - Cards live in their own encrypted file (same DPAPI protection as the passwords). Honest limit: DPAPI protects the file
    //    at rest and from other Windows users, not from other programs running as you.
    //  - A card is filled only on an explicit click, only on an https page, only after the unlock PIN, only into the top page.
    //  - The list sent to the page holds the brand, last four digits and expiry only; the full number goes to the clipboard
    //    (auto-cleared) or the page only on an explicit click after the PIN.

    internal sealed class CardEntry
    {
        public string Id = "", Label = "", Holder = "", Number = "", CreatedUtc = "", UpdatedUtc = "";
        public int Month, Year;   // Year is four digits
        public string Brand => CardRules.Brand(Number);
        public string Last4 => Number.Length >= 4 ? Number.Substring(Number.Length - 4) : "";
    }

    internal static class CardRules
    {
        // digits only, spaces and hyphens removed; 12 to 19 digits
        public static string? Digits(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 40) return null;
            var d = new string(raw.Where(c => c != ' ' && c != '-').ToArray());
            return d.Length >= 12 && d.Length <= 19 && d.All(c => c >= '0' && c <= '9') ? d : null;
        }

        public static bool Luhn(string? digits)
        {
            if (string.IsNullOrEmpty(digits) || digits.Any(c => c < '0' || c > '9')) return false;
            int sum = 0; bool dbl = false;
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                int n = digits[i] - '0';
                if (dbl) { n *= 2; if (n > 9) n -= 9; }
                sum += n; dbl = !dbl;
            }
            return sum % 10 == 0;
        }

        public static string Brand(string? digits)
        {
            var d = digits ?? "";
            if (d.Length < 2 || d.Any(c => c < '0' || c > '9')) return "Card";
            if (d[0] == '4') return "Visa";
            int two = int.Parse(d.Substring(0, 2));
            if (two >= 51 && two <= 55) return "Mastercard";
            if (d.Length >= 4) { int four = int.Parse(d.Substring(0, 4)); if (four >= 2221 && four <= 2720) return "Mastercard"; }
            if (two == 34 || two == 37) return "American Express";
            if (d.StartsWith("6011") || two == 65 || (d.Length >= 3 && int.Parse(d.Substring(0, 3)) >= 644 && int.Parse(d.Substring(0, 3)) <= 649)) return "Discover";
            return "Card";
        }

        public static string Mask(string? digits)
        {
            var d = digits ?? "";
            return d.Length >= 4 ? "•••• " + d.Substring(d.Length - 4) : "••••";
        }

        // two-digit years become 20yy; returns 0 when the year is not usable
        public static int NormalizeYear(int year) => year >= 0 && year <= 99 ? 2000 + year : (year >= 2000 && year <= 2100 ? year : 0);

        // valid month, usable year, and not past the end of the expiry month
        public static bool ExpiryOk(int month, int year, DateTime nowUtc)
        {
            var y = NormalizeYear(year);
            if (month < 1 || month > 12 || y == 0) return false;
            var end = new DateTime(y, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            return nowUtc.ToUniversalTime() < end;
        }

        public static string Expiry(int month, int year) => month.ToString("00") + "/" + (NormalizeYear(year) % 100).ToString("00");
    }

    internal sealed class CardVault
    {
        private readonly string _path; private readonly Func<string, string> _read; private readonly Action<string, string> _write;
        private readonly List<CardEntry> _items = new();
        public CardVault(string path, Func<string, string>? read = null, Action<string, string>? write = null)
        { _path = path; _read = read ?? SecureStore.ReadSecure; _write = write ?? SecureStore.WriteSecure; }

        public IReadOnlyList<CardEntry> Entries => _items;
        public const int MaxCards = 30;

        public int Load()
        {
            _items.Clear(); int skipped = 0;
            var text = _read(_path);
            if (string.IsNullOrWhiteSpace(text)) return 0;
            try
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.ValueKind != JsonValueKind.Object || !d.RootElement.TryGetProperty("cards", out var arr) || arr.ValueKind != JsonValueKind.Array) return 1;
                foreach (var e in arr.EnumerateArray())
                {
                    string S(string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    int N(string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
                    var c = new CardEntry { Id = S("id"), Label = S("label"), Holder = S("holder"), Number = S("number"), Month = N("month"), Year = CardRules.NormalizeYear(N("year")), CreatedUtc = S("created_utc"), UpdatedUtc = S("updated_utc") };
                    if (c.Id.Length < 8 || CardRules.Digits(c.Number) != c.Number || c.Month < 1 || c.Month > 12 || c.Year == 0 || _items.Any(x => x.Id == c.Id) || _items.Count >= MaxCards) { skipped++; continue; }
                    _items.Add(c);
                }
            }
            catch { _items.Clear(); return 1; }
            return skipped;
        }

        private void Save()
        {
            _write(_path, JsonSerializer.Serialize(new
            {
                schema = "recognition.cards.v1",
                cards = _items.Select(c => new { id = c.Id, label = c.Label, holder = c.Holder, number = c.Number, month = c.Month, year = c.Year, created_utc = c.CreatedUtc, updated_utc = c.UpdatedUtc })
            }));
        }

        // Returns null on success, else a plain-words error. The number must pass the Luhn check and must not be a repeat.
        public string? Add(string? label, string? holder, string? number, int month, int year, DateTime nowUtc, out CardEntry? added)
        {
            added = null;
            var digits = CardRules.Digits(number);
            if (digits == null) return "the card number must be 12 to 19 digits";
            if (!CardRules.Luhn(digits)) return "that card number does not pass the check digit test; check it for a typing mistake";
            if (!CardRules.ExpiryOk(month, year, nowUtc)) return "the expiry date is not valid or the card has expired";
            if (!PasswordRules.CleanField(label, 40)) return "the label is too long or has control characters";
            if (string.IsNullOrWhiteSpace(holder) || !PasswordRules.CleanField(holder, 80)) return "enter the name on the card (up to 80 characters)";
            if (_items.Any(x => x.Number == digits)) return "this card is already saved";
            if (_items.Count >= MaxCards) return "the card list is full (" + MaxCards + ")";
            var ts = nowUtc.ToUniversalTime().ToString("o");
            added = new CardEntry
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
                Label = (label ?? "").Trim(), Holder = holder!.Trim(), Number = digits, Month = month, Year = CardRules.NormalizeYear(year), CreatedUtc = ts, UpdatedUtc = ts
            };
            _items.Add(added); Save(); return null;
        }

        public bool Remove(string id) { var n = _items.RemoveAll(x => x.Id == id); if (n > 0) Save(); return n > 0; }
        public CardEntry? Get(string id) => _items.FirstOrDefault(x => x.Id == id);
    }

    internal static class CardFillScript
    {
        // JS run in the top page on an explicit Fill. Values are JSON string literals. Looks for visible fields marked with the
        // standard autocomplete names (cc-number, cc-name, cc-exp, cc-exp-month, cc-exp-year), then falls back to common
        // name/id words. Never touches security-code fields. Fires input/change events. Returns "filled:N".
        public static string Build(string holder, string number, int month, int year)
        {
            var y4 = CardRules.NormalizeYear(year);
            var mm = month.ToString("00"); var yy = (y4 % 100).ToString("00");
            return "(function(h,n,mm,y4,yy){function vis(e){var r=e.getBoundingClientRect();var s=getComputedStyle(e);return r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none'&&!e.disabled&&!e.readOnly}" +
                   "function setv(e,v){if(e.tagName==='SELECT'){var o=[].slice.call(e.options).filter(function(x){return x.value===v||x.text.trim()===v||x.value===String(parseInt(v,10))});if(!o.length)return false;e.value=o[0].value}else{var d=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value');d.set.call(e,v)}" +
                   "e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));return true}" +
                   "function find(ac,rx){var all=[].slice.call(document.querySelectorAll('input,select')).filter(vis);for(var i=0;i<all.length;i++){var a=(all[i].getAttribute('autocomplete')||'').toLowerCase().split(/\\s+/);if(a.indexOf(ac)>=0)return all[i]}" +
                   "for(var j=0;j<all.length;j++){var k=((all[j].name||'')+' '+(all[j].id||'')).toLowerCase();if(rx.test(k)&&!/cvc|cvv|csc|security|zip|postal/.test(k))return all[j]}return null}" +
                   "var c=0,f;" +
                   "f=find('cc-number',/card.?n|cc.?num|ccnum/);if(f&&setv(f,n))c++;" +
                   "f=find('cc-name',/card.?name|name.?on|cc.?name|holder/);if(f&&setv(f,h))c++;" +
                   "f=find('cc-exp-month',/exp.*month|month|exp.?m\\b/);if(f&&setv(f,mm))c++;" +
                   "f=find('cc-exp-year',/exp.*year|year|exp.?y\\b/);if(f&&(setv(f,y4)||setv(f,yy)))c++;" +
                   "f=find('cc-exp',/^(?!.*(month|year)).*exp/);if(f&&setv(f,mm+'/'+yy))c++;" +
                   "return 'filled:'+c})(" +
                   JsonSerializer.Serialize(holder) + "," + JsonSerializer.Serialize(number) + "," + JsonSerializer.Serialize(mm) + "," + JsonSerializer.Serialize(y4.ToString()) + "," + JsonSerializer.Serialize(yy) + ")";
        }
    }

    internal enum UnlockResult { Ok, Wrong, LockedOut, NotSet }

    // The unlock PIN for the vault. Only a salted PBKDF2 hash is kept. Wrong guesses lock the gate for longer and longer.
    // The unlocked state is only in memory (every launch starts locked) and ends after five idle minutes.
    internal sealed class PinGate
    {
        public const int MinLen = 4, MaxLen = 12, Iterations = 200_000, FreeTries = 5;
        public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(5);
        private string _salt = "", _hash = "";
        private int _fails;
        private DateTime _lockedUntil = DateTime.MinValue, _unlockedUntil = DateTime.MinValue;

        public bool IsSet => _hash.Length > 0;
        public int Failures => _fails;

        public static string? ValidatePin(string? pin)
        {
            if (string.IsNullOrEmpty(pin) || pin.Length < MinLen || pin.Length > MaxLen) return "the PIN must be " + MinLen + " to " + MaxLen + " digits";
            if (pin.Any(c => c < '0' || c > '9')) return "the PIN may only contain digits";
            if (pin.Distinct().Count() == 1) return "choose a PIN that is not one digit repeated";
            return null;
        }

        private static string HashOf(string pin, byte[] salt) =>
            Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32));

        // Sets the PIN when none is set. Changing or removing goes through Change / Remove, which check the old PIN first.
        public string? SetPin(string? pin)
        {
            if (IsSet) return "a PIN is already set; change it instead";
            var err = ValidatePin(pin); if (err != null) return err;
            var salt = RandomNumberGenerator.GetBytes(16);
            _salt = Convert.ToBase64String(salt); _hash = HashOf(pin!, salt); _fails = 0; _lockedUntil = DateTime.MinValue;
            return null;
        }

        public int RetryAfterSeconds(DateTime now) => now < _lockedUntil ? (int)Math.Ceiling((_lockedUntil - now).TotalSeconds) : 0;

        private UnlockResult Check(string? pin, DateTime now)
        {
            if (!IsSet) return UnlockResult.NotSet;
            if (now < _lockedUntil) return UnlockResult.LockedOut;
            bool ok = false;
            try { ok = !string.IsNullOrEmpty(pin) && pin.Length <= 64 && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(HashOf(pin, Convert.FromBase64String(_salt))), Convert.FromBase64String(_hash)); } catch { }
            if (ok) { _fails = 0; _lockedUntil = DateTime.MinValue; return UnlockResult.Ok; }
            _fails++;
            if (_fails >= FreeTries)
            {
                var secs = Math.Min(900, 30 * Math.Pow(2, Math.Min(10, _fails - FreeTries)));
                _lockedUntil = now.AddSeconds(secs);
                return UnlockResult.LockedOut;
            }
            return UnlockResult.Wrong;
        }

        public UnlockResult TryUnlock(string? pin, DateTime now)
        {
            var r = Check(pin, now);
            if (r == UnlockResult.Ok) _unlockedUntil = now + IdleWindow;
            return r;
        }

        public UnlockResult Change(string? oldPin, string? newPin, DateTime now, out string? error)
        {
            error = null;
            var err = ValidatePin(newPin); if (err != null) { error = err; return UnlockResult.Wrong; }
            var r = Check(oldPin, now); if (r != UnlockResult.Ok) return r;
            var salt = RandomNumberGenerator.GetBytes(16);
            _salt = Convert.ToBase64String(salt); _hash = HashOf(newPin!, salt); _unlockedUntil = now + IdleWindow;
            return UnlockResult.Ok;
        }

        public UnlockResult Remove(string? pin, DateTime now)
        {
            var r = Check(pin, now); if (r != UnlockResult.Ok) return r;
            _salt = ""; _hash = ""; _fails = 0; _lockedUntil = DateTime.MinValue; _unlockedUntil = DateTime.MinValue;
            return UnlockResult.Ok;
        }

        public bool IsUnlocked(DateTime now) => IsSet && now < _unlockedUntil;
        public void Touch(DateTime now) { if (IsUnlocked(now)) _unlockedUntil = now + IdleWindow; }
        public void Lock() { _unlockedUntil = DateTime.MinValue; }

        public string ToJson() => JsonSerializer.Serialize(new { schema = "recognition.pin.v1", salt = _salt, hash = _hash, fails = _fails, locked_until = _lockedUntil.ToUniversalTime().ToString("o") });

        // null when the text is not a valid PIN record (the caller must then fail closed, not treat it as "no PIN")
        public static PinGate? Parse(string? json)
        {
            var g = new PinGate();
            if (string.IsNullOrWhiteSpace(json)) return g;
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var sc) || sc.GetString() != "recognition.pin.v1") return null;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var salt = S("salt"); var hash = S("hash");
                if (hash.Length > 0) { if (Convert.FromBase64String(salt).Length != 16 || Convert.FromBase64String(hash).Length != 32) return null; }
                g._salt = salt; g._hash = hash;
                g._fails = r.TryGetProperty("fails", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var fi) && fi >= 0 && fi < 1000 ? fi : 0;
                if (DateTime.TryParse(S("locked_until"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lu)) g._lockedUntil = lu.ToUniversalTime();
                return g;
            }
            catch { return null; }
        }
    }
}
