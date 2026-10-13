# Security

## Reporting a vulnerability
Please do not open a public issue. Use GitHub's private "Report a vulnerability" (Security tab) on ScrappyHub/Recognition, or contact the owner through the GitHub profile. Include the version (Settings, or `config/version.v1.json`), steps to reproduce, and impact.

## What is in scope
The signed launch check, encrypted storage (history, bookmarks, session, sync file), the extension ledger and downloader, the internal-page message gate, download handling, and the receipt/evidence chain.

## Design points
- No telemetry. The only network use is what you browse, the filter-list and extension downloads you start (https only, size-capped), optional translate links, and sync files you place in your own folder.
- Internal pages only accept messages from internal tabs.
- Extensions load only through the governance ledger.
- Private tabs do not write history; they use a separate profile that is discarded.

Only the latest release is supported.
