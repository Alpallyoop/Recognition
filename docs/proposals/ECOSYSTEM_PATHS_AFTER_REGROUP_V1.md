# Proposal: update the ecosystem paths in CLAUDE.md after the folder regroup

Status: proposed (not applied; `CLAUDE.md` is a canonical file).

## What changed
Recognition now lives at `<dev root>\Constellation\_Venture-Systems\recognition`. The ecosystem authority is at
`<dev root>\Constellation\Constellation-Network` and SoteriaVault is at `<dev root>\Constellation\_Fides-Systems\soteria-vault`.

## Conflict
`CLAUDE.md` tells agents to read `../Constellation/ecosystem/SERVICE_MAP.md`, `../Constellation/registry/services.json`,
`../Constellation/ecosystem/AGENT_POLICY.md` and `../Constellation/ecosystem/SHARED_INVARIANTS.md`. From the new location those
paths do not exist. The files are at `..\..\Constellation-Network\ecosystem\...` and `..\..\Constellation-Network\registry\services.json`.

## Proposed change
Replace the four `../Constellation/...` lines with `../../Constellation-Network/...` (same file names). No role, boundary or
dependency change.

## Already adapted in implementation (not canonical)
- `scripts\UPDATE_ALL_V1.ps1` finds Constellation and SoteriaVault by walking up from the repository, so either layout works.
- `UPDATE_SITE.cmd` looks for `Constellation-Network` relative to itself.
