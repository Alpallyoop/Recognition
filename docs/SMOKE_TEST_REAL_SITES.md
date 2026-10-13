# Real-site smoke test and measurement sheet (v1.3.0)

Unit tests cannot cover the web engine. Run this once per release (about 20 minutes) and record results in the table at the bottom.
Use a normal tab unless stated. Shield level = Balanced, Tracking prevention = Balanced, unless stated.

## A. Does it break normal browsing? (item 2)
| # | Do this | Pass when |
|---|---|---|
| A1 | Open a news site, a video site, a webmail, a shopping checkout | Pages load, video plays, you can sign in and check out |
| A2 | Open the same pages with Shield level Strict | Still usable; note what breaks (expected: some canvas/audio sites) |
| A3 | Shield > site exception "off" for one site | Reload shows that site's trackers allowed, others still blocked |
| A4 | Ad-heavy page (a news site with banners) | Banners gone, no empty gaps larger than before, no missing article text |
| A5 | Shield > Test a URL: `https://doubleclick.net/x.js`, type Script | Blocked, rule shown |
| A6 | Click a link that opens a pop-up | Opens as a tab. A page that opens a window on load: blocked and Status says so |
| A7 | Private tab, click a pop-up link | Pop-up tab is also private |
| A8 | Click a `mailto:` link | Windows asks which mail app. A `ms-msdt:` link: blocked |

## B. Fingerprinting and ad blocking measured (item 3)
Record the same numbers for Recognition and for Brave (default shields) on the same machine.
| Site | What to record |
|---|---|
| coveryourtracks.eff.org | "Strong protection against tracking?" and fingerprint uniqueness (bits) |
| abrahamjuliot.github.io/creepjs | Trust score; whether canvas/audio/WebGL hashes change after you reload with a new session (restart the browser) |
| browserleaks.com/canvas, /webgl | Hash differs between two different sites in the same session (per-site noise) and is stable on reload of one site |
| d3ward.github.io/toolz/adblock.html | Percent of test domains blocked |
| adblock-tester.com | Score |
| Open any top-10 news site | Number of blocked requests (Shield page counter) and page load time versus Brave |
Expected honest result: lower than Brave on list breadth and cosmetic coverage until the lists are loaded; if the score is far lower after Update lists, file it as a bug with the site.

## C. Passkeys (item 1 of the original request)
| # | Do this | Pass when |
|---|---|---|
| C1 | Passkeys page > Run local test | Windows Hello prompt, then "signature verified" |
| C2 | passkeys.io or webauthn.io: register, sign out, sign in | Works with Windows Hello |
| C3 | Passkeys > block for one site, retry that site | The site reports passkeys not allowed; other sites unaffected |

## D. Full-page capture and PDF tools
| # | Do this | Pass when |
|---|---|---|
| D1 | Tools > Full-page PDF (A4) on a very long article | PDF opens, text readable, no cut line or missing strip at page joins |
| D2 | Full-page PDF (one long page) and Full-page PNG | Same content, correct width |
| D3 | A page with lazy-loaded images, scroll first and then capture | Note whether images below the fold appear (known limit if not) |
| D4 | Open a multi-page PDF, extract pages 2 to 3, save | New PDF has exactly those pages |

## E. Extensions (item 4)
| # | Do this | Pass when |
|---|---|---|
| E1 | Extensions > install `samples\extensions\hello-good` | Allowed; appears enabled after Restart |
| E2 | Install hello-review | Needs your approval; refused without it |
| E3 | Install a build with the `debugger` permission | Refused (deny) |
| E4 | Edit a file of an installed extension on disk, restart | Not loaded; Status says unapproved |
| E5 | Extensions > Test | Runs in a throw-away private tab; nothing persists |
| E6 | Try a real one: uBlock Origin Lite, Bitwarden, Dark Reader from the Chrome Web Store as unpacked or .crx | Record Works / Partly / Fails and why |

## F. Stress (item 6)
| # | Do this | Pass when |
|---|---|---|
| F1 | Open 30 tabs of mixed sites | No crash; memory stays reasonable; switching tabs responsive |
| F2 | Shield > Update lists with all four lists | Finishes; browsing stays responsive during and after |
| F3 | Add a 100,000-line custom list | Loads or is refused with a clear message |
| F4 | Kill the web engine process in Task Manager | Browser recovers or shows an error, does not hang |
| F5 | Full-page PDF of a 50,000 px page | Completes or refuses with a clear message |

## G. Install and update (items 5 and 7)
| # | Do this | Pass when |
|---|---|---|
| G1 | Fresh Windows user or VM: run the installer | Installs, launches, no missing runtime message |
| G2 | Install v1.3.0 over an older version | Settings, vault, bookmarks, extension state survive |
| G3 | Uninstall | App removed; data removal is your choice and is stated |

| Date | Build | Tester | Failures (id: note) |
|---|---|---|---|

## H. Look and default browser (v1.3.1)
| # | Do this | Pass when |
|---|---|---|
| H1 | Launch Recognition | No separate loading window appears; the main window opens at once with a small Starting cover inside it that disappears when ready, with with a dark title bar and the new start page |
| H2 | Open a new tab, then an internal page (Settings) | No white box where the bookmark star was; the star appears only on real web pages |
| H3 | Click each start page tile | Bookmarks, History, Passwords, Shield, Tools and Settings open |
| H4 | Settings > Default browser > Open Windows Default apps, pick Recognition for HTTP and HTTPS | Windows lists Recognition (installed copy only) |
| H5 | Click a web link in another program | Recognition opens it in a new tab; no second window |
| H6 | Launch Recognition again from the Start menu while it is running | The running window comes forward; no second copy starts |
| H7 | Double-click a local .html file after choosing Recognition for .html | It opens in a tab |

## I. Home page, background, VPN hint (v1.3.3)

1. Settings, Home page: type `example.com`, Set. Open a new tab (Ctrl+T) and press Home: both load example.com. Use start page restores the start page.
2. Settings, Start page background: try Aurora, Light, a colour, and Choose a picture. Open a new tab with the start page home: the background shows, text stays readable, and nothing is requested from the web. Remove picture goes back to Default.
3. With a VPN app connected (any), the toolbar globe says "VPN" in green and the VPN page names the adapter. Disconnect it: both go back to "No VPN".
4. VPN page, Check my public address opens the address-check page in a new tab (only when pressed).

## J. Everyday browser features (v1.3.5)

1. Bookmark two pages with the star: they appear on the bar under the tabs. Middle-click one: it opens in a new tab. Right-click: Remove bookmark works. Ctrl+Shift+B hides and shows the bar.
2. Settings, Import bookmarks: choose an HTML bookmarks export from Chrome, Edge, Brave or Opera: the bar fills; importing the same file again says they were already saved.
3. Right-click a tab: Pin moves it to the left and removes its close button; Unpin returns it. Mute shows a muted mark; play a video and the tab shows a speaker. Duplicate, Close other tabs, Close tabs to the right all work, pinned tabs stay. Close a tab, then Ctrl+Shift+T brings it back.
4. Settings, Search engine: pick Brave Search, search from the address bar and from the start page: both use it. Pick DuckDuckGo to go back.
5. Visit three sites a few times, open a new tab: the most-visited tiles show with coloured letters, and the counters show the blocked totals. Restart: the counters keep their values.
6. The side bar buttons open Bookmarks, History, Downloads, Passwords, Shield, VPN, Tools and Settings. Settings, Side bar, Off hides it.

## K. Workspace features (v1.3.6)

1. Right-click a tab, Add to group, New group, name it. Add a second tab to it: both show the coloured group mark and sit together. Collapse hides the second tab; Close group closes both.
2. Right-click a tab, Split view with, pick another tab: both pages show side by side. Click each side: the address bar follows. Drag the bar to resize. Close split view returns to one page.
3. Settings, Continue where I left off. Open three sites, pin one, close Recognition, start it again: the tabs return, only the last active one loads at once, the others load when clicked. Private tabs are not restored. Open my home page turns it off.
4. Start page: hover a tile and press the cross; add a site; Settings, Reset tiles brings the defaults back.
5. Open a news article, Menu, Page tools, Reader mode: clean text; A+/A-, themes, Esc closes. On a page with no article it says so.
6. Play a video, Menu, Page tools, Picture-in-picture: a floating window opens; again closes it.
7. Translate this page: a question names the service; No does nothing; Yes opens a translated copy in a new tab.
8. Sync: on device A, Settings, Device sync, Turn on, pick a synced folder, note the code. On device B, Join with the same folder and code. A bookmark added on A appears on B after Sync now (and the reverse). Wrong code says so and changes nothing.

## L. Library, downloads, overview (v1.3.7)

1. Bookmarks page: Rename changes the name on the page and the bar; Private tab opens it in a private tab; Copy puts the address on the clipboard. Right-click a bar bookmark: same actions.
2. The private-tab button beside "+", the side bar button and the start page button each open a private tab.
3. Downloads page: Change... a folder, download a file: it lands there. Turn on Ask each time: a Save As window appears. A large file shows a progress bar; Pause and Resume work; Show in folder selects the file; Open is missing for .exe files.
4. History: entries have times and day headings. Clear the last hour: only recent entries go. Restart: remaining history loads (the chain still verifies).
5. Overview: numbers rise as you browse a news site; cookies list sites; Delete removes one site's cookies; Clear browsing data with a range works for each box.
6. Shield, Update all: each list shows a bar, then a result line with time and rules.
7. Bookmarks: press Folder on a bookmark, New folder, name it: the bookmark moves under a folder heading and the bar shows a folder chip that lists it. Rename and Delete folder work (delete keeps the bookmarks).
8. Start a large download with the Downloads page open: the bar and size update smoothly with no page reload or flicker.
9. Open a site in a private tab, then Overview: a card shows cookies in private tabs with a count and no site names.

## M. Extensions, themes, taskbar (v1.3.8)

1. Extensions page: paste a Chrome Web Store link (try a simple content-script extension). It downloads, shows what it asks for, you install it, restart, and it appears under Installed. Test, Disable, Enable and Remove work.
2. Paste a store link for a theme: the page says it is a theme; Use this theme changes the bar, tabs and start page.
3. Customize: click each look; the toolbar, tabs, side bar, menus and title bar change at once, and restarting keeps it. Light looks show readable text. Reset brings back the original.
4. Right-click the taskbar icon: New private tab and New tab work with Recognition running and when it is closed; the Recent pages list shows your last four pages; private-tab pages never appear; switching "recent pages" off in Settings clears it.


## N. Address autofill (v1.3.9)

1. Visit a site you have not been to (for example `example.org`), then open a new tab, click the address bar and type `exam`. Expect `example.org` with `ple.org` selected. Enter opens it.
2. Type `yout`. Expect `youtube.com` (popular list) even if never visited.
3. Type `git`, press Backspace. Expect the suggestion to vanish, leaving `gi`, and not to come back until you type another letter.
4. Type `git` and press the right arrow. Expect the caret at the end of the full host.
5. Visit one site three times and another once with the same first letters; the three-time site wins.
6. Private tabs do not add to history, so their sites are not offered.

## O. Window size and place (v1.3.9)

1. Resize and move the window, close it, open it again: same size and place.
2. Maximize, close, open: maximized. Restore, close, open: the earlier windowed size.
3. With two monitors, close on the second one, open again: it returns there. Unplug it and open: the window appears on the main screen.

## P. PIN and cards (v1.3.9)

1. Passwords, Security tab: Set a PIN (a Windows window with masked boxes appears). Wrong second entry sets nothing.
2. With a PIN set, click Fill, Copy password or Reveal on a login: the PIN window appears once, then not again for 5 idle minutes. Lock now makes it ask again.
3. Five wrong PINs: it refuses for 30 seconds even with the right PIN.
4. Cards tab: save a test card (4242 4242 4242 4242, a future date). A mistyped number is refused. The list shows only the brand, last four and expiry.
5. On a real https checkout page, click the key button in the address bar and choose the card: number, name and expiry fill; the security code stays empty; nothing is submitted. On an http page the card entries do not appear.
6. Delete the card: it asks first.
