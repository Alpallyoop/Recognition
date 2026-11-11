# Proposal 0012: governed agent control (the Claude adapter + embedded assistant)

Status: **ACCEPTED** by the owner 2026-11-10 ("fix 0012"); implemented in phases 1–5 below. Opened 2026-11-10 at the owner's request ("make Recognition have the same Claude capabilities and adapter that Chrome has"). Scope chosen with the owner: the **external adapter** (an outside Claude drives Recognition, as the Claude-in-Chrome extension drives Chrome) **and** an **embedded assistant**, reachable over **three transports** (hosted MCP server, CDP, native-messaging), with **both** a default fully-governed mode and an explicit developer mode, exposing the **full** Chrome-equivalent surface.

Per `CLAUDE.md` and `.constellation/AGENT_POLICY.md`, agent control is a **new trust boundary** and required owner review before becoming canonical; the owner accepted it on 2026-11-10. This document is the design of record; the security core (`browser/AgentRules.cs`) and the governed decision at `AgentRules.Decide` are the foundation every phase routes through, and each phase below shipped with its own pure, tested core plus Windows-smoke-tested glue. **Still weakens no existing gate:** every frontend (MCP, CDP, native-messaging, the embedded assistant) is a thin caller of the one executor, and nothing is more privileged than the person.

## Why this is delicate
Recognition's identity is a *governed* browser: every privileged action is gated (`SafetyRules`/MessageGate), web content can reach only an allowlist of shortcuts, sensitive surfaces (the vault, private tabs, Recognition's own `recognition:` pages) are walled off, and actions write receipts. An agent adapter hands an **outside program** the ability to act *as the user* inside that browser. Done naively it is a total bypass of everything above. So the design rule is singular:

> **The agent is less privileged than the user, never more.** Every agent action flows through one governed executor, is subject to the same (or stricter) gates as a human action, touches nothing the user has walled off, and leaves a receipt. There is no path from a transport straight to WebView2.

## Architecture: one core, many frontends
```
 Claude app / Claude Code / embedded panel
        │        │         │
   MCP server  CDP br.  native host        ← transports (thin, untrusted frontends)
        └────────┼─────────┘
            AgentRules  (pure policy: may this capability run here, now?)   ← browser/AgentRules.cs
                 │
          MainWindow.Agent  (the ONE executor: runs a decided action on WebView2, writes the receipt)
                 │
            WebView2 / tab model / existing governance
```
A transport may only **propose** a `(capability, target, args)` request. `AgentRules.Decide(...)` returns `Allow | NeedConsent | Deny`. Only an `Allow` reaches the executor; `NeedConsent` surfaces a native consent prompt; `Deny` is refused and logged. Transports never hold a WebView2 handle, never see the vault, and cannot escalate by reconnecting.

## The capability surface (Chrome parity)
Mapping each Claude-in-Chrome tool to a Recognition capability. "Read" = observe only; "Act" = changes page/tab state.

| Chrome tool | Recognition capability | Class | Default gate |
|---|---|---|---|
| tabs_context | `TabsList` | read | agent-enabled |
| navigate | `Navigate` | act | per-site grant on the destination |
| read_page | `ReadPage` (DOM/a11y tree) | read | per-site grant |
| get_page_text | `GetText` | read | per-site grant |
| find | `Find` | read | per-site grant |
| computer (screenshot) | `Screenshot` | read | per-site grant |
| computer (click) | `Click` | act | per-site grant |
| computer (type) | `Type` | act | per-site grant |
| computer (scroll) | `Scroll` | act | per-site grant |
| form_input | `FormInput` | act | per-site grant |
| file_upload | `FileUpload` | act | **explicit per-call confirm**, never dev-mode-silent |
| javascript_tool | `RunScript` | act | **explicit per-call confirm**, never dev-mode-silent |
| read_console_messages | `ReadConsole` | read | per-site grant |
| read_network_requests | `ReadNetwork` | read | per-site grant (headers/bodies redacted by default) |
| tabs_create | `TabCreate` | act | agent-enabled |
| tabs_close | `TabClose` | act | agent-enabled |
| resize_window | `Resize` | act | agent-enabled |
| gif_creator | `Screenshot` (sequence) | read | per-site grant |

### Always refused (no mode, no grant, ever)
- Any `recognition:` internal page (settings, vault, passwords, setup, export, extensions…). The agent cannot drive Recognition's own governed UI — that is how it would grant itself permissions, read saved logins, or export credentials.
- Any **private tab** (`tab.Private`). Private tabs leave no trace by definition; the agent cannot see or act in them.
- The password **vault** and the autofill/save machinery. The agent never reads a saved login, never triggers autofill, never receives a `pwcap:` capture.
- `file://` and local-file navigation.
- Reading another site's cookies, or exporting a bundle/packet (proposals 0011/export) — those stay user-only.

## Governance modes
- **Default (fully governed).** Agent control is **off** until the user turns it on (Settings → Agent control, off by default) and remains off per launch unless they opt in. With it on, each *origin* must be granted before the agent may read or act there (a native "Allow Claude to act on example.com?" prompt, like a camera grant), and `RunScript`/`FileUpload` ask **per call** regardless. Every allowed action writes a receipt (`agent.<capability>` + origin + outcome, never the typed text, script body, or page content). A always-visible **kill switch** in the toolbar ends the session and revokes grants.
- **Developer mode (explicit opt-in).** For the user's own sites while building. It skips the *repeat* per-site prompt for origins already granted this session — it does **not** remove the first grant, and it **never** silences `RunScript`, `FileUpload`, the private/vault/internal refusals, or the receipts. This is the only concession to speed, and it is logged as `agent.devmode` when enabled. (AGENT_POLICY forbids silent fallbacks; dev mode is loud and bounded.)

Both modes share one `AgentRules.Decide`; the mode is just one input. There is no code path that acts without a decision.

## Transports (thin frontends, land in phases)
All three carry the same request shape and all call `AgentRules` + the executor. None is trusted.
1. **Recognition-hosted MCP server** *(reference transport, Phase 2).* Recognition runs a local MCP endpoint (loopback only, bearer token minted per launch and shown once in Settings) exposing `recognition_tabs`, `recognition_navigate`, `recognition_read_page`, `recognition_click`, … The user adds it to their Claude MCP config. Closest analog to the Chrome adapter and the most portable.
2. **CDP bridge** *(Phase 3).* A loopback Chrome-DevTools-Protocol surface backed by WebView2's `CallDevToolsProtocolMethodAsync`. Every CDP method is **wrapped**: an allowlisted method maps to a capability and runs through `AgentRules`; anything else is refused. Raw CDP is never exposed. Lets existing CDP tooling attach without bypassing governance.
3. **Native-messaging host** *(Phase 4).* Mirrors the Chrome extension's transport (a native host speaking the extension's framing) for clients that expect it.

## Embedded assistant (Phase 5)
A panel inside Recognition that reads and acts on the current page using the **same** `AgentRules` + executor, driven by the Claude API with the user's own key (stored in the vault, never in a bundle/packet). It is just a fourth caller of the core — it gets no extra privilege, obeys the same grants, and writes the same receipts. It is explicitly **not** required for the adapter and can trail the transports.

## Security properties (what review should check)
- One executor; no transport-to-WebView2 path. (`MainWindow.Agent` is the only caller of the WebView2 action APIs for agent requests.)
- `AgentRules.Decide` denies on: agent-off, internal page, private tab, vault/credential capability, `file://`, un-granted origin (→ NeedConsent, not Allow), `RunScript`/`FileUpload` without per-call confirm.
- Receipts carry capability + origin + outcome only. No typed text, script bodies, page text, cookies, or tokens are ever logged. (Enforced by the governed-actions self-test, extended for `agent.*`.)
- Loopback-only transports; per-launch bearer token; kill switch revokes everything; rate limit per origin.
- Grants are per-origin and per-launch (not persisted across restarts unless the user ticks "remember").
- The adapter cannot enable itself, grant its own origins, read the vault, touch a private tab, or export credentials — those are user-only and refused in the pure layer.

## Phases & evidence
- **Phase 1 (built 2026-11-10):** `browser/AgentRules.cs` (pure decision core) + browser.tests; this proposal. Verified in the cloud.
- **Phase 2 (built 2026-11-10):** the hosted MCP server and the executor. Pure + tested: `browser/AgentProtocol.cs` (the MCP tool catalogue + per-call validation + JSON-RPC shaping), `browser/AgentSession.cs` (per-launch bearer token, per-origin grants, kill switch, rate limiter), and `browser/AgentServer.cs`'s JSON-RPC router (initialize / tools.list / tools.call over loopback HTTP, bearer-gated) — 59 browser.tests checks. Glue (compiles; validated on the Windows build, smoke test section DD): `browser/MainWindow.Agent.cs` — the one executor (navigate, read text, DOM/role outline, find, screenshot, click, type, scroll, form input, file upload, run script, console, network, tabs, resize via WebView2/CDP), the consent prompts, server start/stop, and the Settings → Customize "Agent control" section with the enable / developer-mode toggles, the endpoint + one-time token, and the **kill switch** (Phase 2 lives in Settings; a toolbar button is a small follow-up). Every action still routes through `AgentRules.Decide` and writes a content-free receipt.
- **Phase 3 (built 2026-11-10):** the wrapped CDP bridge. Pure + tested: `browser/WsHandshake.cs` (the RFC 6455 accept key) and `browser/CdpMap.cs` (the method allowlist — local / mapped / refused — plus param→capability translation and CDP result/error shaping); `browser/CdpServer.cs`'s router is tested too (23 new checks). Glue (compiles; Windows smoke test section EE): `CdpServer.cs`'s loopback discovery (`/json/version`, `/json`) + WebSocket upgrade + token auth, over `TcpListener`, with frame handling left to the BCL's `WebSocket.CreateFromStream`. It shares the **same `AgentSession` and executor** as the MCP server, so one grant, one token, one kill switch, one rate limit cover both. **Deliberately a subset, not a faithful puppeteer target:** `Browser.getVersion`/`Target.*`/`*.enable` answered locally; `Page.navigate`, `Page.captureScreenshot`, `Runtime.evaluate` (→ the governed RunScript, so it asks every time), `Input.insertText`, `Input.dispatchMouseEvent` (release → click, wheel → scroll) mapped through `AgentRules`; every other method (`DOM.*`, `Network.getResponseBody`, `Fetch.*`, `Storage.*`, `Input.dispatchKeyEvent`, …) refused with a CDP error. Flat session (no Target/session routing); a `sessionId` is echoed if sent. Transport: `ws://127.0.0.1:<port>/cdp?token=…` (first free port from 9222), discovery and socket both token-gated so a page cannot reach it.
- **Phase 3 limits closed (built 2026-11-10):** the CDP bridge was widened toward broad CDP-client use. `Input.dispatchKeyEvent` now maps to the governed Type (printable → type, Enter → submit, key-up → no-op); a curated **read-only passthrough** forwards structure methods (`DOM.*` reads, `CSS.get*`, `Page.getLayoutMetrics`/`getFrameTree`, `Accessibility.get*`, `DOMSnapshot.captureSnapshot`, `Runtime.getProperties`) to WebView2's **real** CDP engine after an origin grant — faithful DOM/layout/a11y answers, still governed, and nothing that returns a resource body, cookies, storage or network traffic is on the list; and `Target.setDiscoverTargets`/`attachToTarget`/`setAutoAttach` now emit the `Target.targetCreated`/`attachedToTarget` lifecycle events a session client needs. It remains a governed subset (mutations still mapped/confirmed; bodies/cookies/storage refused), so it is "broad CDP-client support", not a promise of unmodified puppeteer page automation — that would mean exposing the refused surface, which the owner chose not to do.
- **Phase 4 (built 2026-11-10):** the native-messaging host. Pure + tested: `browser/NativeMessaging.cs` (the 4-byte-length framing). Glue: `browser/NativeHost.cs` — launched as `recognition.exe --agent-native-host` (via a `.cmd` wrapper a browser's native-messaging manifest names), it runs a stdin/stdout bridge to the **already-running** instance's loopback MCP endpoint. The running instance publishes its URL + per-launch token to a **DPAPI-protected** file under `runtime/` (removed on stop/kill); the host re-reads it per message, so no token is ever in the manifest or on a command line. Templates in `scripts/agent/`, registration in `docs/AGENT_NATIVE_HOST.md`. It is a transport only — no capability beyond the governed MCP surface.
- **Phase 5 (built 2026-11-10):** the embedded assistant (`recognition:assistant`, menu "Claude assistant"). Pure + tested: `browser/AssistantProtocol.cs` (the governed tools → Anthropic `tools` array, tool-use parsing, tool-result shaping). Glue: `browser/MainWindow.Assistant.cs` — a chat panel that calls the Anthropic Messages API with the person's own key (**DPAPI-protected** under `runtime/`, never in a bundle/packet/receipt/page) and runs a tool loop over the **same** `AgentExecuteAsync`. It is a fourth caller of the core: every tool it runs goes through `AgentRules` (grants, confirmations, the private/vault/internal refusals, receipts all apply), the person watches it and can stop it. The model is settable in the panel.

Also closed this round (Phase 2 follow-ups): a **kill switch in the ⋯ menu** (shown whenever agent control is on, beside the Settings one), and **console/network capture attached on each navigation** and cleared per page, so a read reflects the current page.

Each phase: positive + negative tests, governed-actions self-test still green, no weakening of existing gates.

### Transport (Phase 2, as built)
JSON-RPC 2.0 over HTTP on `http://127.0.0.1:<port>/mcp` (loopback only; first free port from 8766). Every request carries `Authorization: Bearer <token>` — the per-launch token shown once in Settings — and is rejected 401 otherwise, before routing. Methods: `initialize`, `tools/list`, `tools/call`, `ping`, and the `notifications/*` no-ops. `TcpListener` (not `HttpListener`) so no Windows urlacl reservation is needed for a non-admin launch. The server holds no WebView2 handle; it reaches a page only by awaiting the executor, which goes through `AgentRules`.

## Non-goals
- No agent access to the vault, private tabs, internal pages, cookie export, or evidence packets — ever.
- No silent/always-on control; no mode that removes receipts or the `RunScript`/`FileUpload`/credential refusals.
- Not a headless/server browser; it drives the user's visible session, with the user able to watch and stop it.
