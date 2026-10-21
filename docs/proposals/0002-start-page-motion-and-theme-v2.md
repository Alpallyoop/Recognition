# Proposal 0002 - Animated start page backgrounds, theme file v2, ThemeForge packages

| Field | Value |
|---|---|
| Status | Applied on the owner's instruction 2026-10-21 ("set all of this up now", ThemeForge integration). |
| Affects | Start page (recognition:start), Customize / Settings start page rows, `browser_settings.json`, theme files (`recognition.theme.v1` -> adds `recognition.theme.v2`), theme library entries, Chrome theme package import |
| Compatibility | Additive. v1 theme files, the library schema id `recognition.theme.library.v1` and existing settings files keep working unchanged; a v1 theme is still written byte-for-byte as before. |
| Not changed | Signed SoftwareID, release and packaging process, version number, trust boundaries of the start page (no network, no new hosts). |

## Why

ThemeForge (service `themeforge`) builds animated new-tab backgrounds. The owner asked for Recognition to show them on its own
start page and to accept ThemeForge's Recognition theme package.

## What is added

1. **Vendored runtime.** ThemeForge motion runtime 2.0.0 (`themeforge/outputs/constellation_night/newtab_extension/motion.js`) is
   copied byte-for-byte to `browser/shield/motion.js` and embedded as resource `motion.js`.
   SHA-256: `074198d978e3f01bb4729775c5643659dba2b06906838152591c1f992f2679ce`.
   The browser recomputes this hash before every use (`StartMotion.RuntimeOk`); a different file means no animation (fail closed).
   `browser.tests` checks the embedded bytes and the file on disk against the same hash. Updating the runtime means a new proposal that
   records the new hash.
2. **Setting `start_motion`** (in `runtime/browser_settings.json`): `{"effect","duration_ms","intensity_pct","seed"}`, validated by
   `browser/StartMotion.cs`: effect one of `none kenburns pan drift breathe parallax particles aurora rain snow shimmer fireflies`;
   `duration_ms` 4000..120000; `intensity_pct` 0..100; `seed` 0..2147483647; JSON integers only (no fraction, exponent or string); no other
   keys. Anything invalid becomes `none`. Written canonically (fixed key order). `start_motion_theme` (true/false) records that the
   animation came with the current theme, so a theme without an animation turns off only a theme's animation, never the user's own.
3. **Start page.** When the effect is not `none`, the start page gets a full-window `<canvas>` behind its content (z-index -1, no
   pointer events) and the runtime, inline like the page's existing script (the start page builds its own HTML and already uses inline
   script; nothing is fetched). The existing CSS background stays underneath: if the runtime is missing, the picture fails to decode or the
   script throws, the canvas is removed. `respect_reduced_motion` is always true (Windows "reduce animations" gives one still frame).
   - With the user's picture (`start_bg` = `image`), the runtime draws the picture with the same darkening as the CSS. The page script
     takes the picture from the page's own CSS background (only an embedded `data:image/jpeg;base64,` URL is accepted), so the picture is
     embedded once (WebView2 `NavigateToString` is limited to 2 MB).
   - Without a picture the runtime clears each frame with one colour, which would hide the background gradient. The canvas is therefore
     blended: black + `screen` on dark backgrounds and white + `multiply` on light ones, which leave the CSS background exactly as it is.
     Accent and text colours come from the current theme (text replaced by the start page's own when it would not stand out).
   - The private start page is never animated.
4. **Theme file v2.** `recognition.theme.v2` = v1 plus an optional `motion` object (same rules as the setting). A bad `motion` refuses
   the whole theme, as bad colours do. Motion `none` or absent is the same as v1, and such a theme is saved as v1. In a v1 file a `motion`
   key is ignored, as every unknown key always was. Applying a theme with motion sets the start page animation.
5. **Library entries.** Entries of `themes/library.v1.json` (schema id unchanged, `recognition.theme.library.v1`) may carry the same
   optional `motion` object; an entry with a bad one is dropped like any other invalid entry. ThemeForge's "publish to Recognition library"
   exchange relies on this.
6. **Chrome theme packages.** A `.crx`, `.zip` or folder theme whose root also holds `recognition.theme.json` (v1 or v2, at most 20 KB)
   uses that file's colours, names and motion; the Chrome manifest still supplies the start page picture (`theme_ntp_background`, same
   path checks). If that file is present but invalid or too large, the import is refused (no silent fall back to the Chrome colours).
   ThemeForge ships this file in `<id>.recognition-theme.zip`.

## Evidence

- `dotnet run -c Release --project browser.tests`: recipe validation (positive and 28 negative cases), canonical JSON, boot
  configuration, runtime hash (embedded and on disk), page injection on/off/tampered, v2 parse and round trip, v2 with bad motion refused,
  v1 unchanged byte-for-byte, library entries with good and bad motion, package import with/without/invalid `recognition.theme.json`,
  static checks of the browser glue.
- `node browser.tests/shield/motion.test.js` (after the C# tests, which write the two pages it runs): the injected page runs in a simulated
  browser, animates, is deterministic for a recipe, draws one still frame under reduced motion, makes no network request, loads only an
  embedded JPEG, and removes the canvas when the runtime or the picture fails. Wired into `_selftest_recognition_browser_governed_actions_v1.ps1`.
- The WPF project compiles (`-p:EnableWindowsTargeting=true` on Linux). The animation itself has not yet been seen in a real WebView2.

## Not done (owner or Constellation decisions)

- No `themeforge > recognition` integration is registered in Constellation and no integration receipt is written for theme imports
  (theme files are user-chosen data; applying a theme is already receipted in the action ledger as `theme.apply`, and changing the
  animation as `start.motion`). Registering the exchange is for Constellation's registry.
- Start page CSP: the start page carries no Content-Security-Policy meta of its own (unlike the other internal pages); this is unchanged.
  Adding one is a separate change because the weather card and radar on the start page would need to be checked against it.
