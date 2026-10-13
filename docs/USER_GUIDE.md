# Recognition user guide

- **Tabs:** Ctrl+T new, Ctrl+W close, Ctrl+Shift+N private tab (also the buttons beside "+", and the taskbar right-click menu).
- **Address bar:** type a word and it completes from sites you visit most, your bookmarks, then popular sites. Backspace removes the suggestion; Enter or right arrow accepts.
- **Bookmarks:** star to add; the Bookmarks page renames, moves to folders, opens privately. Right-click a bar chip for the same.
- **History:** timed and grouped by day; clear the last hour, day, week, four weeks, or all.
- **Downloads:** save folder and ask-each-time in Settings or the Downloads page; progress, pause, resume, cancel.
- **Overview:** blocked trackers, cookies by site, library counts, most visited, and Clear browsing data by range.
- **Customize:** looks, your own colours, backgrounds, home page.
- **Extensions:** paste a Chrome Web Store or Edge Add-ons link or an id on the Extensions page; manage there. Themes apply as looks.
- **Workspace:** tab groups, split view, session restore, start tiles, reader mode, picture-in-picture, translate link, folder sync.
- **Privacy:** no telemetry; form autofill of personal data is not built. Address completion uses only local data.

## Buttons (double-click in the project folder)

| File | What it does |
|---|---|
| `UPDATE_ALL.cmd` | Pull, test, package, install and reopen Recognition |
| `BUILD_EXE.cmd` | Build the new exe, run the tests, make `dist\Recognition-win-x64.zip` |
| `RUN_BROWSER.cmd` | Build and open it from the source folder (no install) |
| `UPDATE_SITE.cmd` | Update the website from the Constellation folder (validate, rebuild, commit, push) |

In PowerShell, run them with `.\UPDATE_ALL.cmd` (they are in the project folder, not in `scripts`).
