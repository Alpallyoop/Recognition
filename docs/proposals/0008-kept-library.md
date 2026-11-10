# Proposal 0008: a kept library (Remove is undoable; Delete forever is explicit)

Status: built 2026-11-01 at the owner's request ("it should retain the things that it scans in and they can delete it or easily add it back on, same with extensions themes etc"). Not yet seen on Windows (smoke test section Z).

## Before
- Removing an extension deleted its files: to use it again you had to find and download it again.
- A Chrome or Firefox theme brought in from a link, file or folder was applied, but no copy was kept: picking another look lost it.
- Removing a theme under My themes deleted its file; removing an uploaded developer build deleted its copy.

## Now
- Everything you bring in is kept until you choose **Delete forever** (which asks first).
- Themes from packages are kept with their pictures under Customize, **Themes you added** (Use, Remove).
- **Remove** moves themes, library themes, extensions and developer builds to **Removed** (Customize for themes, Extensions for extensions and builds): **Add back** or **Delete forever**.
- Files live under `runtime\kept\<kind>\<id>\`; the list is `runtime\kept\kept.v1.json` (`recognition.kept.v1`, validated on load: a hand-edited list can only lose entries, never point outside `runtime\kept`, except an uploaded developer build inside `runtime\extensions_dev`).

## Governance is unchanged
- Removing an extension still stops it, removes its engine data and records `deny` / `user_removed` in the governance ledger, so the load gate refuses it.
- **Add back** re-hashes the kept files; they must be exactly the removed copy (identity hash), otherwise it is refused.
- It then goes through the normal review: the current policy decides, `review` needs your approval click again, `deny` is refused, and the install writes a new ledger record. There is no path that writes a ledger record from the kept library.
- A theme kept from a package is re-hashed before it is used again.
- Every keep, remove, add back and delete is in the action ledger (`kept.*`).
- A developer's own build folder is never deleted.

## No size or count limits (2026-11-01, at the owner's request)
- **Every theme you apply is kept.** Only the files a theme actually uses are kept: its manifest, `recognition.theme.json`, its start page picture (up to 40 MB, the most the browser reads) and its header pictures (up to 16 MB each, at most 6). Anything a package carries beyond that is something the browser never uses, so there is no size at which a theme is applied but not kept.
- **Nothing is dropped to make room.** An entry leaves the list only when you press Delete forever. (The list reader stops at a million entries, so a damaged file cannot be read without end; that is not a limit anyone reaches.) If the list file ever cannot be read, it is left untouched instead of being replaced, so nothing in it is lost.
- **Removed extensions are kept compressed** (`runtime\kept\extension\<id>.zip`). Before the folder is deleted, the archive is unpacked once, with the same hostile-package checks as any download, and must give back exactly the same identity hash. If it does not (or the archive would be over 100 MB), the folder itself is kept, so nothing is ever lost to the compression. The Removed list shows each one's size and what it takes on disk, plus the total.
