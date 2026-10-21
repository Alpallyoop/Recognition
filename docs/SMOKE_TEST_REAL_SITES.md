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
9. SoteriaVault page: choose a folder and press Check. The status bar says "SoteriaVault link checked: <outcome> (integration receipt ...)" and `proofs\integration_receipts\recognition__soteriavault\constellation.integration.receipt.v1.ndjson` gains one line per check (outcome `deferred` while SoteriaVault says the link is not on, `unavailable` for a folder without it).
