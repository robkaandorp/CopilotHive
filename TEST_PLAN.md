# Test Plan — Worker Pool Stats Endpoint

## Scope
Verify that the `GET /health` endpoint now includes `worker_pool` statistics (total, idle, busy, generic workers and per-worker details). Feature branch: `copilothive/add-worker-pool-stats-endpoint/coder-001`.

## Acceptance Criteria
- Build succeeds with zero errors.
- All existing tests (baseline 366) still pass — no regressions.
- At least 5 new tests were added (minimum 371 total).
- Health response JSON contains `worker_pool` with fields: `total_workers`, `idle_workers`, `busy_workers`, `generic_workers`, `workers`.
- At least 5 tests use `WebApplicationFactory<Program>` (via `HiveTestFactory`).

## Test Cases

### Build Verification
- [x] `dotnet build` completes with 0 errors, 0 warnings.

### Unit Test Review
- [x] `WorkerPoolTests` — 2 new unit tests: `GetDetailedStats_EmptyPool_ReturnsZeroCounts`, `GetDetailedStats_MixedWorkers_CorrectCountsAndEntries`.
- [x] `WorkerPoolStatsEndpointTests` — 8 new integration tests covering HTTP 200, stats fields, workers array, busy/idle/generic counts, individual worker entry fields.

### Runtime Verification
- [x] 376 tests total (366 baseline + 10 new) — all pass.
- [x] Line coverage: 37.15%, Branch: 28.53%, Method: 47.05%.

### Acceptance Tests
- [x] `worker_pool` nested object present in `/health` response with all required fields.
- [x] 8 tests in `WorkerPoolStatsEndpointTests` use `HiveTestFactory` (extends `WebApplicationFactory<Program>`).
- [x] No existing tests removed (376 ≥ 366).
