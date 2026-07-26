# Staking Backend Operations Runbook

## Scope
This runbook covers replay-safe event ingestion, failure handling, and CI-reproducible validation for `xpoint-staking-backend`.

## Service Guarantees
- Event ingest is idempotent by `(chainId, transactionHash, logIndex)`.
- Out-of-order/delayed events do not regress node projection status or node metadata.
- Corrupted persisted state is quarantined at startup and does not block service availability.
- Deployment-bound snapshots fail closed on a fingerprint mismatch outside explicit LocalDev/localhost. Local development quarantines a mismatched snapshot and exposes `staleStateQuarantines`.
- Persist-write failures are non-fatal; ingestion continues and the failure is visible via metrics.

## Key Runtime Endpoints
- `GET /health/live`
- `GET /health/ready`
- `POST /api/events`
- `GET /api/events`
- `GET /api/events/stats`
- `GET /api/staking/nodes`
- `GET /api/staking/rewards/{address}`

## Metrics to Monitor (`/api/events/stats`)
- `attempted`
- `inserted`
- `duplicates`
- `staleNodeProjectionIgnored`
- `staleStatusIgnored`
- `corruptedStateRecoveries`
- `staleStateQuarantines`
- `statePersistenceFailures`
- `totalEvents`

Alerting guidance:
- `corruptedStateRecoveries > 0`: investigate filesystem integrity and recent deployments.
- `statePersistenceFailures > 0`: investigate permissions, disk saturation, and path correctness.
- Sustained growth in `stale*Ignored`: review event source ordering guarantees.
- `staleStateQuarantines > 0`: replay from a trusted checkpoint; do not copy a state file between deployments.

## Recovery Procedures

### A. Corrupted State Recovery
1. Confirm startup behavior via `GET /api/events/stats` and `corruptedStateRecoveries`.
2. Locate backup file matching `<state-path>.corrupt-<timestamp>.bak`.
3. Validate backup offline before any restore attempt.
4. Replay canonical events through `POST /api/events` from trusted checkpoint.
5. Verify snapshots using:
   - `GET /api/staking/nodes`
   - `GET /api/staking/rewards/{address}`
   - `GET /api/events/stats`

### B. Persist Path Degraded Mode
1. Check `statePersistenceFailures` in `/api/events/stats`.
2. Validate `Contracts:StatePath` points to writable storage.
3. Fix path/permissions and restart service.
4. Replay from trusted checkpoint to rehydrate durable state.

### C. Deployment Manifest or Readiness Failure
1. Keep `/health/live` separate from `/health/ready`; do not route traffic on liveness alone.
2. Verify `Contracts__DeploymentManifestPath` is readable and that `Contracts__ExpectedDeploymentNetwork` matches the manifest `network` exactly (case-insensitive). If configured, `Contracts__ExpectedDeploymentChainId` must also match.
3. The manifest must have `schemaVersion: 1`, numeric `chainId`, a required 64-hex `lifecycleId`, required four contract addresses, `parameters.stakingRequirement`, and `parameters.maxContributors`. Manifest values are authoritative; only explicit expected network/chain pins fail startup on conflict.
4. Verify the RPC's `eth_chainId` and bytecode at all four addresses. Do not log or paste the RPC URL if it contains credentials.
5. For a production fingerprint mismatch, retain the old snapshot for forensics and start only after selecting the correct deployment/state or replaying canonical events. Only explicit LocalDev/localhost may auto-quarantine stale state.

### D. Event Source Ordering Incidents
1. Inspect `staleNodeProjectionIgnored` and `staleStatusIgnored` trends.
2. Validate producer ordering at source.
3. If needed, replay from checkpoint to rebuild consistent projections.

## Migration Notes (State Path)
When changing `Contracts:StatePath`:
1. Stop service.
2. Move existing state snapshot to new location (or start empty intentionally).
3. Start service with updated configuration.
4. Verify baseline stats and projection endpoints.
5. Run replay from checkpoint when baseline state is intentionally empty.

## CI-Reproducible Validation
From repository root:

```powershell
Set-Location .\xpoint-staking-backend

dotnet test .\XPoint.Staking.Backend.slnx
```

Validation includes:
- replay idempotency
- delayed/ordering edge-cases
- corrupted state quarantine and recovery counter
- non-fatal persistence failure behavior
- API-level integration checks for stats and projections

## Incident Checklist
- Capture current `/api/events/stats` payload.
- Capture current `Contracts:StatePath` and filesystem health.
- Save recent `/api/events` source window for replay.
- Record replay checkpoint used for restoration.
