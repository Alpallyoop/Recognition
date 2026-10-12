# Recognition compared with Opera GX and Brave (October 2026)

Based on side-by-side screenshots of the three browsers on the same machine. "Built" means written and covered by executed tests where there is logic to test; features that need the real browser window are on the manual smoke sheet (section J) and are unverified until you run them.

| Everyday feature | Opera GX | Brave | Recognition 1.3.5 |
|---|---|---|---|
| Bookmarks bar under the tabs, with import prompt | yes | yes | Built (Ctrl+Shift+B, right-click to remove, middle-click new tab) |
| Import bookmarks from another browser | yes | yes | Built (HTML file export, web addresses only) |
| Most-visited tiles on the new tab page | yes (speed dial) | yes (favourites) | Built (from your own history, nothing fetched) |
| Blocked-trackers counter on the new tab page | no | yes | Built (lifetime trackers/ads blocked, tracking links cleaned) |
| Side bar of quick buttons | yes | yes (side panel) | Built (home, bookmarks, history, downloads, passwords, shield, VPN, tools, settings) |
| Tab menu: pin, mute, duplicate, close others, reopen closed tab | yes | yes | Built (right-click a tab; Ctrl+Shift+T) |
| Tab shows when it is playing sound | yes | yes | Built |
| Choice of search engine | yes | yes | Built (DuckDuckGo, Brave, Startpage, Ecosia, Qwant, Bing, Google) |
| Start page background and home page | yes (wallpapers) | yes | Built earlier (presets, colour, your own picture) |
| Address-bar suggestions from history and bookmarks | yes | yes | Already had |
| Built-in VPN | yes (provider servers) | yes (paid) | Different by design: bring your own exit; detects a VPN app |
| Ad, tracker and fingerprint blocking | basic | strong | Strong (filter engine, shield); counters and per-site controls |
| Password manager, passkeys | basic | basic | Encrypted vault, passkeys, SoteriaVault link |
| PDF and image tools, full-page screenshots | no | no | Ahead of both |
| Evidence receipts, governed updates | no | no | Unique |

## Not built yet (honest list)

(Built in v1.3.6: tab groups, split view, session restore, editable tiles, reader mode, translate, picture-in-picture, and server-free device sync through a folder you choose.)

- Resource limiter and tab-sleep tuning panel (sleeping tabs exist).

## Left out on purpose

- News feeds, sponsored tiles, rewards, crypto wallet and AI sidebars: they either call home or serve ads, which conflicts with "no telemetry".
- Gaming overlays (GX Corner, game store tiles): not a goal.
