## Q. Extensions page and start-up steps (v1.3.9)

1. Start Recognition: the cover inside the window shows "Step n of 6" with plain messages and disappears when ready; no separate window and no product name.
2. Extensions, Get extensions: each store button opens the store in a new tab and leaves the Extensions tab in place.
3. Add by link: paste a Chrome Web Store page address, then an Edge Add-ons address, then a Firefox Add-ons page address (for example addons.mozilla.org/en-US/firefox/addon/dark-reader/). Each downloads and shows a review card. The Firefox one may be refused by policy or fail Test; that is expected for many add-ons.
4. Paste an ordinary web page address: the message explains it is not an extension package.
5. Try a demo: both demos open a review card; discard or install them.

## R. Everything stays in the project folder; update from the browser

1. Open a private window, then close Recognition: `runtime\tmp` is empty after the next start and nothing new appears in `%TEMP%`.
2. Download any file with no download folder chosen: it lands in `runtime\downloads_files`, and Downloads, Open folder opens that folder.
3. Run `BUILD_EXE.cmd` or `RUN_BROWSER.cmd` while an old copy exists in `%LOCALAPPDATA%\Recognition`: the cleanup step lists what it did, the old copy and its shortcuts are gone, its `runtime` and `packets` are in `.scratch\old_install_backup\<time>\`, and any extracted `dist\Recognition-win-x64` is deleted.
4. Installed copy (not a source folder): Settings, Updates, Check for updates. With no newer release it says "You are up to date". After a newer release is tagged, it asks, downloads, closes, shows a PowerShell window with the steps, reopens on the new version with bookmarks, history and passwords intact, and `.update_backup\<time>` holds the previous files.
5. Source folder: the same button explains that it runs from a source folder and points to Update everything.
6. Failure check (optional): edit the downloaded zip before the helper runs, or block github.com; the update refuses, nothing changes.

## S. Closing, start-up choice, weather radar, themes

1. Open one tab and close Recognition: it closes with no question. Open three tabs and close: a window lists "3 tabs", offers "Reopen the tabs I have open now" / "Start fresh with my home page", and Stay open. Pick "Start fresh", close, reopen: only the home page. Repeat with "Reopen": the three tabs come back (only the active one loads at once).
2. Start a large download and close: the window says the download is in progress; Stay open keeps it going.
3. Open a PDF tab and close: the window names the PDF and says viewer changes are lost unless saved. Open a page with a form (a long contact form), type into it, close: the page is named if it asks to be kept open (many do not; that is expected).
4. Settings, Tabs, startup: switch to "Ask me", close with two tabs (choose Reopen) and restart: it starts straight into the tabs, once. Close again with the same setting and restart: it asks "Reopen the tabs you had open last time?". Turn "When closing: Ask first" off: the several-tabs question stops; a download in progress still asks. Logging off Windows is not held up.
5. New tab (window wider than 1100 px): the Weather card sits at the right-hand side and shows a button only; nothing is requested until you press "Show the weather". Then it shows now, hour by hour (scrolls sideways) and 5 days. "Use my location": with Windows Location on it says "Your location, near <city>"; with it off it says location is not shared and shows the chosen place. Settings, Weather on the start page: °F/°C, region or coordinates, "Always show", "Off", Hide on the card. Radar link: shows the Windy/RainViewer map in the card and "Forecast" switches back. Narrow the window: the card moves below the sites.
6. Customize: pick several themes, then Top bar styles (ocean, rose, flat, frosted...) and check the toolbar, address field, tabs and bookmarks bar change but text stays readable; pick several backgrounds from the grid and open a new tab to see them.
7. Customize, Theme library, Browse the library: with network it says "Downloaded from GitHub" (the file must be pushed to `main` first); offline it shows the shipped list. "Add and use" applies a theme and it appears under My themes; Remove deletes it. "Add a theme file..." accepts a `.rtheme` made from `themes\README.md`.

## X. Extension developer area (proposal 0003) and the SoteriaVault integration receipt

1. Extensions, Developer tab: it is listed between "Add by link or file" and "My installed". "Add a build folder..." with `samples\extensions\hello-good`: a card appears with its name and version, "ready", an identity and no problems.
2. Run: a tab "Extension developer" opens (private), the build loads, and its page opens if it has one. The Extensions page shows "Running in the developer tab" with Show the tab, DevTools and Unload. DevTools opens a DevTools window for that tab. Your everyday profile is unchanged: My installed does not list it and it does not load after a restart.
3. Copy `hello-good` to a folder of your own, add that folder, turn on "Watch for changes", press Run, then edit a file (for example change the version in `manifest.json` to 1.0.1): within about a second the card shows a new build, "Since the previous build ... 1 changed", and the developer tab reloads. Add `"tabs"` to `permissions` and save: "access: + tabs" is shown in amber and a review warning appears.
4. Break the build (delete a file `manifest.json` points to, or set `"version": "1.0-beta"`): the card turns red with the problem named; Run says it was not run.
5. Package (.zip): save it, then upload the saved zip with "Upload a build...": it shows the same identity as the folder build. Package twice: the SHA-256 shown is the same both times.
6. Upload `ext-bad` (zipped) or any build asking for `<all_urls>`: it shows "the governance policy will refuse this build" and does not run.
7. Send to review on a good build: the normal review card appears at the top; installing it works as before and adds a ledger record. Remove the project: your folder is untouched; an uploaded build's copy under `runtime\extensions_dev` is deleted.
8. Close Recognition: `runtime\tmp\extdev-run` and the `rb-extdev-*` profile are gone.
9. SoteriaVault page: choose a folder and press Check. The status bar says "SoteriaVault link checked: <outcome> (integration receipt ..., witnessed)" and `proofs\integration_receipts\recognition__soteriavault\receipts.ndjson` gains one line per check (outcome `deferred` while SoteriaVault says the link is not on, `unavailable` for a folder without it). An older `constellation.integration.receipt.v1.ndjson` there is renamed to `receipts.ndjson` and anchored once (the status bar says so). Then close Recognition, open `receipts.ndjson` in Notepad, change the LAST line's outcome (for example `"deferred"` to `"unavailable"`), save, reopen and press Check: the status bar says the receipt was NOT written (RECEIPT_WITNESS_MISMATCH) and the file is unchanged. Put the file back as it was: Check works again.

## Y. Firefox themes and animated header pictures (proposal 0004)

1. Customize, Get a theme: paste `https://addons.mozilla.org/en-US/firefox/addon/nyan-cat-animated/` and press Get theme. The note says it applied "Nyan Cat (Animated)" with its header picture. The top of the window (toolbar and tab strip) shows the dark blue frame, the stars and rainbow, and the cat at the right; the stars and cat move. Buttons, tabs and the address field still work by click (the picture never takes clicks).
2. Repeat with `purple-starfield-animated` (stars move across the whole top), `praisethesun` (three pictures, one on the left), `saturn-plus` (still picture at the right), `laser-chamber-black-green` (a .jpg with spaces in its name), `terminal-deluxe` and `citroen-ds3-racing` ("orange and black": a faint grid pattern over the colour), `neapolitan-scoop` (colours only, no picture). Text in the toolbar stays readable on each.
3. Customize, Header picture: Keep still stops the animation (one frame stays), Animate starts it again; restart Recognition: the header and the still/animate choice are kept. Windows Settings, Accessibility, Visual effects, Animation effects off: the header stays still.
4. Minimise for a minute and restore: the animation continues; Task Manager shows no CPU use while minimised.
5. Pick any built-in look (for example Midnight): the header picture goes. Pick your own colours after a Firefox theme: the picture stays under them. Remove the picture: colours stay, picture goes; `runtime\theme_header` has no picture files left.
6. Extensions, Add by link: paste a Firefox theme link: the review card says it is a theme and offers Use this theme (never Install).
7. A large animated theme (if one is over the 96 MB frame budget) shows still, and Customize says so.
10. NFL (proposal 0005): with NFL in its usual place (`_Covenant-systems\nfl`), press Check on the SoteriaVault page: the status bar ends "..., witnessed by the browser, witnessed by NFL)". In NFL's folder, `pwsh -File scripts\nfl_cli_v1.ps1 receipt-head -Integration recognition>soteriavault` shows the same receipt prefix as HEAD. Rename NFL's folder for a moment: Check still writes the receipt and says "NFL not found on this computer"; rename it back. Then close Recognition, change the last receipt line in Notepad, reopen and press Check: it is refused and the status bar names RECEIPT_WITNESS_MISMATCH (or RECEIPT_NFL_MISMATCH). Put the file back.
11. Constellation: "1 - SYNC EVERYTHING", then "9 - Witness Receipts": the plan lists `recognition>soteriavault  emitting  N receipt line(s)`, NFL verifies N/N, and it is marked `witnessed`.

## Z. Kept library: nothing you bring in is lost by removing it (proposal 0008)
1. Customize, Get a theme: paste a Firefox theme link (for example Purple starfield). It is applied, and "Themes you added" lists it with its source and size. Pick a built-in look, then press Use on the kept theme: the header picture and colours come back without downloading anything.
2. Press Remove on that theme: it moves to "Removed themes". Add back puts it under "Themes you added" again. Remove it again and press Delete forever: it asks first, then `runtime\kept\theme\<id>` is gone.
3. Theme library: add a theme, then Remove it under My themes: it moves to "Removed themes" (`runtime\themes\<id>.json` is moved to `runtime\kept\pack\<id>\pack.json`). Add back: it is under My themes again.
4. Extensions: install the demo safe extension, then Remove it. The question says its files are kept. It appears under Removed with its size and the space it takes; `runtime\extensions\<id>` is gone and `runtime\kept\extension\<id>.zip` is there, and the ledger's newest record for it says `deny`, `user_removed`. Restart: it does not load.
5. Add back: it installs again without a download, the ledger gets a new `allow` record, and Removed is empty. For the review demo, Add back shows the review card again and needs the approval click.
6. Remove an extension, close Recognition, replace `runtime\kept\extension\<id>.zip` with a zip of slightly different files, reopen: Add back refuses ("changed on disk since it was removed"). Delete forever removes it.
8. A big theme: apply a Firefox theme with a large header picture (or a theme package that carries extra files): it is kept, and `runtime\kept\theme\<id>` holds only `manifest.json` and the pictures it uses.
7. Developer tab: upload a build, then Remove it: it moves to Removed (its uploaded copy stays). Add back: it is on the developer list again and is checked. Remove a folder build and Delete forever: your own folder is untouched.

## AA. Import cookies from another browser (proposal 0009)
1. With Chrome or Edge installed and signed in to a couple of sites, open the Overview page (menu -> Overview, or Import cookies from another browser). The "Import cookies from another browser" section fills in on its own with each browser/profile found, a per-category breakdown (sign-in, preferences, analytics, advertising, other) and a few site names. Nothing had to be located or typed.
2. Leave the default categories (sign-ins, preferences, other on; analytics and advertising off). Press Import on one, read the confirm box (it names the categories, count and sites), say Yes. The trackers are not brought over. Tick Analytics/Advertising and Import again to confirm those come only when chosen. The message says how many were imported. Open one of those sites: you are signed in without typing your password.
3. The other browser is unchanged (check it still has its cookies). The action ledger has a `cookies.import` entry; no passwords or cookie values are in it.
4. Firefox: the same, from `cookies.sqlite`. A profile with app-bound (very new Chrome) cookies shows some as "could not be read" and imports the rest.
5. Import into a private tab is never offered; with only a private tab open, Import says to open a normal tab first.

## BB. Autofill and save logins (proposal 0010)
1. Settings → Passwords shows "Fill saved logins automatically" and "Offer to save logins", both ON by default.
2. Save a login on the Passwords page for a site you can reach over https. Open that site in a new tab: the username and password fill on load, and the form is NOT submitted. The action ledger has a `vault.autofill` entry (no secret).
3. Two saved logins for the same site: autofill does nothing (the key button lets you choose). Set a PIN and lock it: autofill does nothing until you unlock via the key button.
4. On a site with no saved login, sign in: a bar appears under the toolbar, "Save this login for <site>?" with Save / Never for this site / Not now. Click Save; it appears on the Passwords page. Sign in again with a changed password: the bar offers "Update". Click "Never for this site": no bar there again, and Settings shows a "Clear" for the never list.
5. Turn both toggles OFF: no autofill, no save bar. A plain http page never autofills and never offers to save. In a private tab, no save bar appears.
6. With SoteriaVault as the manager: autofill defers to SoteriaVault (no silent fill); the save bar's Save hands the login to SoteriaVault.

## CC. Export for another device — portable bundle (proposal 0011)
1. Menu → Export session. The evidence packet is unchanged (code, QR, folder). Below it is a new section "Move this setup to another device" with **Export for another device…**; click it — it opens the Setup snapshot page (`recognition:setup`).
2. The create list now includes **Saved logins**, **Sign-in cookies** and **Kept extensions & themes**, all three OFF by default and labelled sensitive. With none of the credential ones ticked, create as before: a bundle and a code, any expiry up to 7 days.
3. Tick **Saved logins** and/or **Sign-in cookies**: the 7-day option is refused ("at most 24 hours"). With a PIN set and locked, creating prompts to unlock; cancelling exports nothing. Tick **Sign-in cookies** with no normal tab open: it asks you to open one first.
4. Create a bundle with logins + cookies on device A. On device B, paste the bundle and code → **Preview**: it lists "N saved login(s) — added to your vault, never overwriting a password you have", "N sign-in cookie(s) across M site(s)", and the kept list as an info line (never a checkbox). Credential rows are unticked by default.
5. Apply on B (unlock the vault if prompted; have a normal tab open). The logins appear on the Passwords page; a login you already had with the same password is reported as "already saved", not duplicated; reload a site whose cookie came over and you are signed in. The kept extensions/themes are **not** installed automatically — the preview told you to add them from the Extensions and Customize pages.
6. A bundle with logins/cookies that is older than 24 hours is refused on preview ("expired"). The action ledger has `setup.create` / `setup.apply` entries naming only the categories — no code, blob or password.

## DD. Agent control — hosted MCP server, Phase 2 (proposal 0012)
1. Customize → Agent control shows "Let Claude drive this browser" OFF by default. Turn it On: the status bar shows an endpoint like `http://127.0.0.1:8766/mcp`. "Reveal token" shows a 64-char bearer token. Restart Recognition with it left on: the endpoint comes back (new token).
2. Add the endpoint + token to a Claude MCP config (Authorization: Bearer <token>). `tools/list` returns the recognition_* tools. A request with no token or a wrong token is refused (401). A request to a non-loopback address does not reach it.
3. On a normal https page, ask Claude to read the page (recognition_get_text / recognition_read_page): the first action on that site shows "Allow Claude to act on <site>?" — Allow once, then reads/acts work on that origin without re-asking. Navigate, click (x,y), type, scroll, screenshot, find, form_input all act on the visible page and never submit unless asked.
4. recognition_run_script and recognition_file_upload ask you to confirm EVERY time, showing the script/path, even on a granted site and even in developer mode. Decline → the tool returns an error and nothing runs.
5. Try to act on: a private tab, a `recognition:` page (settings/vault/setup), or a `file://` page → refused every time, no prompt offered. Open the Passwords page and confirm no agent tool can read or fill a saved login.
6. Developer mode ON: a site you already granted this session no longer re-prompts for reads/acts; a site you have NOT granted still prompts; script/upload still confirm every time.
7. Kill switch (Settings → "Stop now"): agent control turns off, the endpoint closes, and every site grant is forgotten (the next enable + action re-prompts). The action ledger shows `agent.<capability>:<origin>:ok|fail` lines and `agent.server_start` / `agent.kill` — never typed text, a script body, or page content.
8. Rate limit: a rapid burst of actions on one site is throttled with a "rate limit" error, then resumes.

## EE. Agent control — governed CDP bridge, Phase 3 (proposal 0012)
1. With agent control ON (section DD), Customize → Agent control → "Reveal token" now also shows a CDP endpoint `ws://127.0.0.1:<port>/cdp?token=...` and discovery at `http://127.0.0.1:<port>/json/version?token=...`. The action ledger shows `agent.cdp_start`.
2. Discovery: `GET /json/version?token=<token>` returns JSON with `webSocketDebuggerUrl`; the same without the token (or a wrong token) returns 401. `GET /json` lists one page target.
3. Connect a CDP/WebSocket client to the `ws` URL (token in the query). `Browser.getVersion` returns product "Recognition/agent" with no prompt. `Page.navigate {url:https://…}` navigates the active tab (first use on a site prompts for the grant, shared with the MCP side). `Page.captureScreenshot` returns `data` (base64 PNG). `Input.dispatchMouseEvent` release → a click; `mouseWheel` → a scroll; `Input.insertText` → typing.
4. `Runtime.evaluate {expression}` runs — and asks you to confirm EVERY time (it is the governed run-a-script), even on a granted site. Decline → a CDP error, nothing runs.
5. A refused method (`DOM.getDocument`, `Network.getResponseBody`, `Input.dispatchKeyEvent`, `Fetch.enable`, …) returns a CDP error "method not permitted by Recognition's governed CDP bridge" and never touches the page.
6. Try the CDP bridge against a private tab / a `recognition:` page as the active tab → every mapped action is refused (same rules as the MCP side). Non-loopback address does not reach it.
7. Kill switch (section DD step 7) closes the CDP socket too; the `ws`/discovery URLs stop answering and grants are revoked.

## FF. Agent control — native-messaging host, Phase 4 (proposal 0012)
1. Edit `scripts/agent/recognition-agent-host.cmd` to point at your built recognition.exe. Fill in `scripts/agent/com.recognition.agent.template.json` (`path` → the .cmd, allowed origin/extension) and register it per `docs/AGENT_NATIVE_HOST.md` (HKCU NativeMessagingHosts key).
2. Turn agent control ON in Recognition. Confirm `runtime/agent_native_endpoint.v1.enc` appears; turn it OFF (or use the kill switch) and confirm the file is removed.
3. From a test extension, `connectNative("com.recognition.agent")` and send `{"jsonrpc":"2.0","id":1,"method":"tools/list"}` framed (4-byte LE length + JSON). The reply lists the recognition_* tools. `tools/call` runs a governed action (first use on a site prompts, shared with the HTTP/CDP grants).
4. With agent control OFF, the same call returns a clean "agent control is off" error (no crash, no window opens).
5. The host opens no window and holds no privilege of its own — stopping agent control mid-session makes the next call error without restarting the host.

## GG. Agent control — embedded assistant, Phase 5 (proposal 0012)
1. Menu → "Claude assistant" opens `recognition:assistant`. With no key, it asks for your Anthropic API key; save it. Confirm `runtime/assistant_key.v1.enc` appears (encrypted). "Remove key" deletes it.
2. Ask it to do something on the current page ("summarize this page", "click the login button"). It reads/acts via the same tools: the first action on a site prompts for the grant; running a page script or uploading a file asks every time; a screenshot comes back to the model as an image. Tool activity shows as "·" lines.
3. On a private tab or a `recognition:` page, the assistant's tools are refused exactly as the external adapter's are. It can never read a saved password.
4. "New conversation" clears the thread; the kill switch / turning agent control off does not remove the key but stops the shared executor (tools error until re-enabled). Set a different model in the Model box.
5. The key never appears in any receipt, the export packet, or a portable bundle. The assistant's API calls go directly from Recognition to the Anthropic API, not through any page.

## HH. Home page, link defaults, and blocking your own sites
1. Settings → "Home page & links": the Home page field shows the current home. Type `example.com`, Set → the Home button and new windows open it. "Use start page" resets to the Recognition start page. (The same control is also under Customize.)
2. The note points to Shield (how links open / pop-ups) and Web engine → Default browser ("Open Windows Default apps"). Confirm both links work.
3. Settings → "Blocked sites": add `example.com`. Try to visit it (and `www.example.com`, `sub.example.com`): each shows the "Site blocked" page instead of loading; a look-alike like `notexample.com` or `example.com.evil.com` still loads.
4. On the blocked page, "Unblock example.com" removes it and the site loads next time. In Settings the list shows each blocked host with Remove.
5. On a normal site, Settings shows a one-click "Block <current host>" button; clicking it blocks that site. The action ledger shows `site.block_add` / `site.block_remove` / `site.blocked` (host only).
6. Blocks persist across restart (stored in browser_settings.json), apply in normal and private tabs, and never block Recognition's own pages.
