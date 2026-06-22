# Deep Staking Backend

ASP.NET Core compatibility backend for Deep staking state. It preserves
Session reward/stake semantics at the API projection layer and uses XPoint
(`XPNT`, 9 decimals) as the configured token.

## Agent Specs

- Start with [`AGENTS.md`](AGENTS.md) before changing event ingestion, projections, persistence, or API behavior.
- Use [`docs/SESSION_PORTING.md`](docs/SESSION_PORTING.md) for Session staking/reward projection migration rules.
- Contract source of truth remains `xpoint-staking-contracts`; this service indexes and projects events.

## Endpoints

- `GET /info`
- `POST /api/events`
- `GET /api/events`
- `GET /api/events/stats`
- `GET /api/staking/nodes`
- `GET /api/staking/nodes/{nodeId}`
- `GET /api/staking/rewards/{address}`
- `GET /api/staking/state/{nodeId}`

`POST /api/events` is idempotent on `(chainId, transactionHash, logIndex)` so
event replay is safe.

State persistence behavior:

- The indexer stores replay-safe state at `Contracts:StatePath` (defaults to `artifacts/staking-state.json` under app base directory).
- On startup, an invalid/corrupted state file is automatically quarantined to `*.corrupt-<timestamp>.bak` and the service continues with empty in-memory state.
- `GET /api/events/stats` exposes ingestion counters including duplicate/stale ignores, `corruptedStateRecoveries`, and `statePersistenceFailures`.

## Recovery (devnet/non-production)

1. Stop the backend process.
2. Move or delete the current state file configured in `Contracts:StatePath`.
3. Start the backend.
4. Replay canonical events through `POST /api/events` from block 0 or from your trusted checkpoint.
5. Verify `GET /api/events/stats` and `GET /api/staking/nodes`/`GET /api/staking/rewards/{address}` snapshots.

## Configuration

```json
{
  "Contracts": {
    "TokenAddress": "0x...",
    "ServiceNodeRewardsAddress": "0x...",
    "ServiceNodeContributionFactoryAddress": "0x...",
    "RewardRatePoolAddress": "0x...",
    "StakingRequirementAtomic": 25000000000000
  }
}
```

Run locally:

```bash
Contracts__TokenAddress=0x... \
Contracts__ServiceNodeRewardsAddress=0x... \
Contracts__ServiceNodeContributionFactoryAddress=0x... \
Contracts__RewardRatePoolAddress=0x... \
dotnet run --project src/XPoint.Staking.Backend
dotnet test
```

For local devnet, copy these addresses from
`xpoint-staking-contracts/deployments/localhost.latest.json`.

See `docs/OPERATIONS_RUNBOOK.md` for production-oriented recovery procedures,
degraded-mode handling, and CI-reproducible validation steps.
