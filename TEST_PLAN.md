# Test Plan — Stale Worker Cleanup Background Service

## Scope
Verify the implementation of `StaleWorkerCleanupService`, `IWorkerPool` interface, `WorkerPool`
implementing `IWorkerPool`, and associated DI registrations in `Program.cs`.

## Acceptance Criteria
- At least 4 new xUnit tests in `StaleWorkerCleanupServiceTests.cs`
- Tests cover: (a) no stale workers → nothing removed, (b) one stale worker → removed + warning logged,
  (c) multiple stale workers → all removed, (d) correct timeout passed to `GetStaleWorkers`
- Tests use Moq mocking — no real `Thread.Sleep` or `Task.Delay` in test bodies
- `StaleWorkerCleanupService` extends `BackgroundService`
- `CleanupIntervalSeconds` and `StaleTimeoutMinutes` constants exist with values 60 and 2 respectively
- `IHostedService` registered via `AddHostedService<StaleWorkerCleanupService>()` in `Program.cs`
- All pre-existing tests still pass

## Test Cases

### Unit Test Review
- [x] 6 xUnit tests exist in `StaleWorkerCleanupServiceTests.cs` (exceeds minimum of 4)
- [x] (a) `RunCleanupCycle_NoStaleWorkers_RemoveWorkerNotCalled`
- [x] (b) `RunCleanupCycle_OneStaleWorker_RemovedAndWarningLogged`
- [x] (c) `RunCleanupCycle_MultipleStaleWorkers_AllRemoved`
- [x] (d) `RunCleanupCycle_CallsGetStaleWorkers_WithCorrectTimeout`
- [x] `Constants_HaveExpectedValues`
- [x] `ExecuteAsync_WhenCancelledImmediately_StopsWithoutCallingPool`

### Integration Tests
- [x] Existing `WorkerPoolTests` and `WorkerPoolStatsEndpointTests` exercise real component interaction
- [x] Integration tests in test suite verify application startup and real DB-backed pipeline store

### Runtime Verification
- [x] `dotnet build` → succeeded with zero errors
- [x] `dotnet test` → 382 total, 382 passed, 0 failed
- [x] No real delays (`Task.Delay`/`Thread.Sleep`) in test bodies — `RunCleanupCycleAsync` is tested directly
- [x] `StaleWorkerCleanupService` inherits `BackgroundService`
- [x] `AddHostedService<StaleWorkerCleanupService>()` present in `Program.cs` (line 86)
- [x] `IWorkerPool` registered as singleton in `Program.cs` (lines 42–43)
- [x] Constants: `CleanupIntervalSeconds = 60`, `StaleTimeoutMinutes = 2`

### Acceptance Tests
- [x] Feature meets the stated goal (background service periodically removes stale workers)
- [x] No regressions — all 382 pre-existing + new tests pass
