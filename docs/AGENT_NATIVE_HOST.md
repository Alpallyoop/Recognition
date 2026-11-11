# Registering Recognition's native-messaging host (proposal 0012, Phase 4)

The native-messaging host lets a browser extension (or any native-messaging client) talk to Recognition's governed agent
over stdin/stdout instead of HTTP. It is a **thin bridge**: it forwards each framed JSON-RPC message to the already-running
Recognition instance's loopback MCP endpoint and frames the reply back. It holds no privilege of its own — the running
instance's AgentSession, per-site grants, kill switch and AgentRules still decide everything. Agent control must be **on**
in Recognition (Customize → Agent control) or every request gets a clean "agent control is off" error.

## How it finds the endpoint
While agent control is on, Recognition writes a DPAPI-protected file at `runtime/agent_native_endpoint.v1.enc` holding the
loopback MCP URL and the per-launch bearer token. The host reads and decrypts it (same Windows user only). No token is ever
placed in the manifest or on a command line, and the file is removed when agent control is turned off or the kill switch is used.

## Files
- `scripts/agent/recognition-agent-host.cmd` — the wrapper a browser launches. Edit the path inside it to point at your
  built/installed `recognition.exe`. (A wrapper is needed because browsers launch the manifest's `path` with their own
  arguments; the `.cmd` adds `--agent-native-host`, which tells Recognition to run as the host rather than open a window.)
- `scripts/agent/com.recognition.agent.template.json` — the native-messaging manifest. Set `path` to the absolute path of
  the `.cmd`, and set `allowed_origins` (Chrome/Edge) or `allowed_extensions` (Firefox) to your extension's id.

## Register (Windows, current user — no admin needed)
1. Fill in the `.cmd` path and the manifest (`path` + allowed origin/extension).
2. Copy the manifest somewhere stable and add the registry key pointing at it:
   - Chrome:  `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.recognition.agent` → (Default) = full path to the manifest JSON.
   - Edge:    `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.recognition.agent` → (Default) = manifest path.
   - Firefox: `HKCU\Software\Mozilla\NativeMessagingHosts\com.recognition.agent` → (Default) = manifest path.
3. Turn agent control on in Recognition. The extension can now `connectNative("com.recognition.agent")` and send the same
   JSON-RPC the MCP endpoint accepts (`initialize`, `tools/list`, `tools/call`). Each tool call is governed exactly as over HTTP.

## Notes
- The host re-reads the endpoint file per message, so turning agent control on/off (or hitting the kill switch) takes effect
  without restarting the host.
- This is a transport only; it adds no capability beyond what the MCP endpoint already exposes under governance.
