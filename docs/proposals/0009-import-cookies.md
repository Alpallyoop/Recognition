# Proposal 0009: import your cookies from another browser

Status: built 2026-11-01 at the owner's request ("should be able to import cookies from other browsers as well"). Not yet seen on Windows (smoke test section AA).

## Why
Recognition already imports bookmarks. Switching browser also means losing every sign-in, because sign-ins live in cookies. This brings them across, so you stay signed in.

## Automatic, and sorted by what the cookies are for (2026-11-01)
No path to find or type: the Overview page looks for the other browsers on its own as soon as it opens and lists what it found. The cookies are sorted into **Sign-ins and sessions**, **Preferences and settings**, **Analytics**, **Advertising** and **Other**, worked out from each cookie's name (with the `__Secure-`/`__Host-` prefixes and the HttpOnly+Secure pair as hints). You tick the categories to bring over; by default sign-ins, preferences and other are on and the two tracker categories are off, so switching browser carries your logins without carrying trackers. Each source shows how many cookies it has in each category, and the Import button says how many the current choice will bring.

(The first version of this, earlier the same day, discovered the browsers but its Overview-page wiring was not saved, so the panel never appeared; this is the working version.)

## What
On the Overview page (and menu → Privacy and security → *Import cookies from another browser*), Recognition looks for other browsers installed under your Windows account and shows each one with how many cookies it can import and the sites they cover. You pick one, confirm, and the current cookies are added to your normal profile.

Sources read:
- **Chromium family** (Chrome, Edge, Brave, Chromium, Vivaldi, Opera): the `Cookies` SQLite database. Values are AES-256-GCM; the key is in `Local State`, wrapped by Windows DPAPI for your account, so only your own account can unwrap it. Newer app-bound (`v20`) values cannot be read and are skipped and counted.
- **Firefox**: `cookies.sqlite` (values stored in the clear for your account).
- A Netscape **cookies.txt** file is also supported by the parser as a browser-independent fallback.

## How it is kept safe
- A cookie is a credential, so this is treated like a sign-in, not like bookmarks: Recognition **asks before importing** each source, says how many sites it will sign you in to, writes an action receipt (`cookies.import`), and imports **only into your normal (non-private) profile**, never a private tab.
- Recognition only **reads** the other browser's files; it never changes them. The database is opened share-mode (the other browser may be running) and copied into memory; nothing is written back.
- Only your own Windows account's DPAPI can unwrap the key, so the import works only for the person who owns those profiles.
- Expired cookies are skipped. Cookies with control characters, no host, a leading-space name or over-long values are skipped (the engine would reject them), never imported blank or malformed.
- A value that cannot be decrypted is counted and skipped, never guessed.

## New code
- `browser/SqliteReader.cs` — a small, read-only SQLite reader (no dependency): one table through `sqlite_master`, leaf/interior table pages, record values, rowid aliases, and overflow pages. Bounded and defensive; anything it cannot parse is an error, never a wrong row.
- `browser/CookieImport.cs` — pure rules: the Local State key, AES-GCM / DPAPI value decryption, Chromium/Firefox time and same-site conversions, row → cookie mapping, Netscape parsing, and which cookies are importable.
- `browser/MainWindow.CookieImport.cs` — the glue: finding browsers, DPAPI unwrap, reading the locked database, the confirm step, and adding cookies through WebView2's cookie manager.
- `browser.tests`: 23 checks, including the SQLite reader run against real databases written by Python's sqlite3 (overflow pages included) and AES-GCM round trips.

## Limits
- WAL: a browser that has not checkpointed keeps its newest cookies in a `-wal` side file, which this does not merge, so a very recently set cookie might not come across. Re-running after the other browser closes picks them up.
- `v20` app-bound Chromium cookies (newest Chrome/Edge) cannot be decrypted; they are skipped and counted.
