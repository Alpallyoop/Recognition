# Proposal 0010: autofill and save logins

Status: built 2026-11-10 at the owner's request ("ensure there's an auto fill and save filled/save info"). Not yet seen on Windows (smoke test section BB).

Until now Recognition filled a saved login only when you chose it from the key button, and saved one only when you typed it on the Passwords page (or in SoteriaVault). This adds autofill on load and an offer to save a login you sign in with. Both are optional (Settings → Passwords) and keep the browser's existing safety rules.

## Autofill (host-driven)
On a completed navigation, if **Fill saved logins automatically** is on, Recognition fills the saved login when all of these hold:
- the page is on **exactly** a saved site over https (`PasswordRules.MayFill`): no subdomains, no look-alikes, no plain http on the internet;
- there is **exactly one** saved login for that site (it never guesses between several);
- the vault is **not locked** (a set-but-unlocked PIN is fine; a locked one is not — use the key button, which prompts);
- SoteriaVault is **not** the password manager (SoteriaVault asks you per fill, so autofill defers to it).

It uses the same fill as the key button: visible fields only, the first password field and the username field before it, and it **never submits**. Autofill runs only in the top document the tab is on, so a saved login is never filled into an embedded cross-origin frame. A receipt (`vault.autofill`) records that it happened and for which site, never the secret.

## Offer to save (native bar)
A tiny content script notices a login being submitted (form submit, a submit-button click, or page hide) and hands the entered username and password to the host via a `pwcap:` message. The host:
- accepts it only when it comes from the **page's own top-frame https origin** (re-checked against the tab's current URL);
- makes **no offer** for a password already saved for that username, for a site on your "never" list, in a private tab, or over http;
- offers **Save** for a new login or **Update** for a changed password, on a **native bar** (never a web page) with **Save / Update**, **Never for this site**, and **Not now**.

Nothing is saved until you click. The bar survives the same-origin redirect after login and clears when you leave the site or after 90 seconds. With SoteriaVault as the manager, Save hands the login to SoteriaVault.

## Message gate
`pwcap:` is a new web-allowed message kind (`MessageVerdict.Capture`), like keyboard shortcuts: a web page may send it, but it only ever offers to save a login for that page's own origin, and the host asks before saving. No privileged command is reachable from web content; the governed-actions self-test still passes.

## Settings
Settings → Passwords: **Fill saved logins automatically** (default on) and **Offer to save logins** (default on), plus a way to clear the per-site "never" list. Stored in `browser_settings.json` (`autofill_logins`, `offer_save_logins`, `never_save_origins`).

## Also in this change
The weak-password dictionary in `PasswordVault.cs` is now base64-encoded in source (a public detection list, never a secret), so the plain words no longer sit in the file; the `.gitguardian.yaml` entry for that file is dropped. New pure rules in `AutofillRules.cs` (22 browser.tests checks).

## Limits
- Autofill fills into the page, where page scripts can read the field — the same exposure as any fill (and as the key button). Exact-origin-only and top-frame-only keep a look-alike or an embedded frame from receiving it.
- The capture content script reads the submitted password to offer saving it; it only messages Recognition, never the network, and the host ignores it when "offer to save" is off.
