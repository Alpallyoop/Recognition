# .constellation (read-only)

The part of the Constellation ecosystem's authority documents this repository needs to be read and worked on by itself:
this service and its direct neighbours only. Constellation writes this folder on every `constellation.ps1 publish -Apply`;
the authority is Constellation's own registry and ecosystem documents. Do not edit these files: a change made here is
overwritten and is not a change to the ecosystem. Propose changes in Constellation (`docs/proposals`).

| File | What it is |
|---|---|
| SERVICE_MAP.md | This service and its direct neighbours: role, family, ownership boundaries and dependencies |
| registry.json | The same services from the registry (identity, role, owns / does not own, upstream / downstream, contracts) |
| AGENT_POLICY.md | How agents work in any repository of the ecosystem |
| SHARED_INVARIANTS.md | Rules every service keeps |
| REMOTE_WORK.md | Working on this repository from a cloud or phone session, and setting it up for that |
| INTEGRATION_WORK.md | This service's open integration work |
| integrations.json | This service's registered integrations and the receipt each writes |
| constellation.integration.receipt.v1.json | Schema of the receipt every integration writes |
