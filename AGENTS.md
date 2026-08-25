# XPoint Staking Backend agent rules

The workspace rules in `../AGENTS.md` apply. This file contains only projection-backend deltas.

## Owns

- Replay-safe chain event ingestion and deterministic staking/reward projections.
- HTTP projection APIs consumed by registry, portal and operators.
- Snapshot persistence, corruption quarantine, runtime counters and degraded readiness.

Solidity economics belong in `xpoint-staking-contracts`; registry storage belongs in
`deep-registry-api`; chain/service deployment belongs in `deep-devops`.

## Repository rules

- Event identity is `(chainId, transactionHash, logIndex)` and replay is idempotent.
- Project only contract-emitted semantics; never reimplement or reinterpret economics here.
- Contract addresses, chain ID, staking requirement and RPC endpoints come from a validated
  deployment manifest/configuration, never source constants.
- Stale, partial or corrupt state cannot report healthy; quarantine with bounded diagnostics.
- Reorg/replay handling must be deterministic and expose duplicate, stale and failure counters.
- Never log RPC credentials, raw signer material or sensitive provider responses.
- Update registry/portal consumers and `docs/OPERATIONS_RUNBOOK.md` for API, manifest or recovery
  changes.

## Verify

```powershell
dotnet test XPoint.Staking.Backend.slnx
```

Add focused replay/idempotency and recovery tests for changed event behavior, then run the DevOps
gate that consumes `/api/events/stats` for release-path changes.
