# Proposal 0007: a redesigned window and internal pages (owner picks one of three)

Status: waiting for the owner's choice (2026-10-31). Nothing in the browser has changed yet beyond the fixes in `docs/AUDIT_2026-10-31.md`.

The problems this fixes are listed in the audit's "UI and UX review" (duplicated controls, "Export Session" as the loudest button, tabs under the toolbar, a 30-entry menu, a fixed slogan in the status bar, paragraphs before controls, three palettes, no focus ring).

Three working prototypes (open the HTML files in any browser; they use only made-up `.example` sites and change nothing):

| | Layout | Where page facts live | Menu | Internal pages |
|---|---|---|---|---|
| A Familiar (`0007-ui-redesign/A_familiar.html`) | tabs in the title bar, one toolbar (like Edge or Chrome) | site icon / Shield button opens one site panel | 18 entries in 5 groups, zoom inline | left menu |
| B Focus (`B_focus.html`) | tabs, address and pinned pages in a left sidebar (collapsible); the page in an inset card | Shield pill above the page opens the site panel | command bar (Ctrl+K): commands, pages, open tabs, addresses | no menu (pinned buttons) |
| C Instrument (`C_instrument.html`) | tabs and toolbar on top, an Inspector on the right (Ctrl+I) | Inspector: connection, Shield by kind, cookies, login, ledger receipts; an evidence strip replaces the slogan | grouped (Tabs / This page / Recognition) | page tabs across the top |

Common to all three: the lock only on https and a visible warning on http; "Export session" moved into the menu; one palette for the window and the internal pages, in light and dark; explanations folded under the controls; visible keyboard focus; compact density.

Building the chosen one touches `MainWindow.xaml`, `App.xaml` (brushes and styles), `MainWindow.Shell.cs` (internal-page frame and CSS), `Customize.cs` (theme keys), and the internal pages' markup (headings as real elements instead of `h1 style='font-size:16px'`). No change to trust boundaries, receipts, schemas or governed behaviour.
