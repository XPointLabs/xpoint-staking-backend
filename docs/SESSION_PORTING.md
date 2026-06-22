# Session Porting Spec - Staking Backend

Last updated: 2026-06-10.

## Scope

This document governs migration of Session staking and reward projection semantics into the Deep staking backend.

## Porting Model

Deep uses XPNT contracts as the chain-side source of truth. The backend must only ingest, replay, and project contract events. It must not invent alternate stake/reward rules.

## Reference Sources

- `../xpoint-staking-contracts/contracts/*`
- `../xpoint-staking-contracts/test/unit-js/*`
- `../xpoint-staking-contracts/test/cpp/*`
- upstream Session/Oxen staking references when available under `../source`.

## Required Semantics

- Event replay is idempotent by `(chainId, transactionHash, logIndex)`.
- Stale duplicates do not mutate projection state.
- Stake requirement is configured in XPNT atomic units and aligned with deployment manifests.
- Projection APIs expose node state, reward state, and stats required by registry/devops.
- Corrupted state files are quarantined and counted.

## Accepted Deviations

- Token is XPNT with 9 decimals, not OXEN.
- Backend stores deterministic local JSON snapshots for current devnet/CI profile.
- Chain polling may be external to this service; event ingestion API remains replay-safe.

## Evidence Checklist

- backend unit test for event/projection behavior,
- contract test or fixture when chain semantics change,
- registry/e2e update if API shape changes,
- DevOps runtime/recovery evidence for release-impacting changes,
- operations docs update for new recovery/config steps.

## Stop-The-Line Conditions

- Backend accepts events with ambiguous identity.
- Replay changes balances or node state.
- Projection diverges from contract tests.
- Corruption recovery loses diagnostics.
- Registry readiness can pass while backend stats show failures.
