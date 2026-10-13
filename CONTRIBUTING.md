# Contributing

1. Read `CLAUDE.md` and `docs/canonical/` first. The canonical documents define the role, trust boundaries and schemas; do not change them directly. Put proposed canonical changes under `docs/proposals/`.
2. Build and prove before opening a pull request (Windows, PowerShell 7, .NET 8 SDK):

```powershell
dotnet build browser -c Release
dotnet run --project browser.tests -c Release
$env:RECOGNITION_PASSPHRASE = "any-value-for-selftests"
pwsh -File scripts\_selftest_recognition_browser_governed_actions_v1.ps1
pwsh -File scripts\RUN_RELEASE_GATE_V1.ps1 -RepoRoot . -RequireBrowserBuild
```

3. Put rules in pure, testable code (`Workspace.cs`, `Customize.cs`, `BrowserFeel.cs`) and add executed checks in `browser.tests/Program.cs`.
4. No telemetry, no news feeds, sponsored tiles, rewards, wallets or AI sidebars.
5. Update `docs/canonical/CURRENT_STATE.md`, `CHANGELOG.md` and the smoke tests with your change.
