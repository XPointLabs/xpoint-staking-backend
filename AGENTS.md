# Agent Specification - Deep Staking Backend

Last updated: 2026-06-10.

## Mission

`xpoint-staking-backend` owns replay-safe indexing and HTTP projection of XPNT staking/reward state for Deep. It bridges contract events into registry/client/operator APIs while preserving Session reward/stake semantics at the projection layer.

## Source Of Truth

- Workspace entry point: `../prompts/00_Agent_Entry_Point.md`.
- Operations runbook: `docs/OPERATIONS_RUNBOOK.md`.
- Porting rules: `docs/SESSION_PORTING.md`.
- Contract semantics: `../xpoint-staking-contracts/AGENTS.md`.
- Registry consumer: `../deep-registry-api/AGENTS.md`.

## Ownership Boundaries

Owned here:

- event ingestion API,
- idempotent replay handling,
- staking/reward projection models,
- state snapshot persistence and corruption quarantine,
- runtime stats and degraded-mode diagnostics,
- backend API tests.

Not owned here:

- Solidity contract semantics,
- deployment scripts,
- registry storage,
- client UI.

## New Deep Solution Rules

Backend changes must preserve:

- idempotency on `(chainId, transactionHash, logIndex)`,
- deterministic event replay,
- explicit duplicate/stale/error counters,
- corrupted state quarantine,
- configuration-driven contract addresses and staking requirement,
- clean restart/recovery process.

Do not bake local devnet addresses or production addresses into code. Use configuration.

## Session Compatibility Rules

Preserve Session-visible staking/reward concepts:

- service node stake state,
- reward address/accounting projection,
- contribution state,
- unlock/exit semantics as emitted by contracts,
- staking requirement in atomic XPNT units.

If a semantic belongs to contract execution, implement it in contracts first, then project it here.

## Required Verification

```powershell
dotnet test XPoint.Staking.Backend.slnx
```

For production/recovery changes, also run DevOps readiness or recovery gates that consume `/api/events/stats`.

## Acceptance Gates

A backend change is complete only when:

- replay/idempotency tests cover changed event behavior,
- stats expose new recovery or failure modes,
- config docs are updated for new contract fields,
- registry/e2e consumers are updated for API changes,
- corrupted state recovery still works.

## Stop-The-Line Conditions

- Duplicate event replay changes projected balances/state.
- Stale or corrupted state is accepted silently.
- Contract semantics are reimplemented differently from Solidity.
- Registry can read a partial projection as healthy.
- Secrets/RPC credentials are logged or written into artifacts.

## Agent Workflow

1. Read this file and `docs/SESSION_PORTING.md`.
2. Check contract event definitions and backend tests.
3. Add replay/idempotency tests first.
4. Implement projection changes.
5. Run solution tests and relevant DevOps gates.
6. Update operations docs for recovery/config changes.
