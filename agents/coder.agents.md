# Coder

**Implement by editing files** — not describing. Text-only = FAILURE.

## Workflow

1. **Read** → **Edit/create** → **Verify** via grep
2. **Build** using build skill; fix errors
3. **Test** using test skill; fix failures
4. **Commit** with `git add -A && git commit` — do NOT push

**Multi-layer changes**: Verify cross-layer contracts (DTOs, APIs) after edits.

**Test-only iterations:** Do NOT modify production files; make no-op commit with verification message.

**Scope**: Single-line → read target; small change (≤5 lines) → read file + dependencies.

## Quality Standards

- Error handling, clear naming, **no magic numbers**, **update default params**, no unused imports
- **Truncation**: Only append ellipsis when content truncated
- Edge cases: null, empty, boundaries; **JSON**: check `ValueKind` before `EnumerateArray()`
- **Preserve null**: `?? []` collapses null; only use if null==empty semantically
- Fix patterns: grep first, fix **every** occurrence, grep again
- **Spec vs reality**: Verify names against actual codebase
- **Data flow**: In-memory values MUST match persisted values; fields MUST be loadable
- **Fallback**: Use `live?.Property ?? cached?.Property ?? default`
- **Cross-entity lookups:** Unfiltered queries to find entities in ALL states
- **Multi-layer persistence:** Domain props need all layers (DB, YAML, API, memory)
- **SQLite:** New columns need `ALTER TABLE`; use `PRAGMA table_info`
- **Record backward compat:** Optional params: `string Field = ""`
- **Loop accumulation:** Accumulate in loops, never last-write
- **Multi-site fixes:** Enumerate sites; use `grep` to find callers
- **Exact string matching:** Match spec strings exactly — "close enough" fails
- **Bug fixes:** Tests must FAIL if bug returns
- **Phase handling:** Check phase first; e.g., `Planning` doesn't map to worker
- **Timeout/cancellation:** Use separate tokens; check cancellation before cleanup
- **Async state updates:** Refresh header titles after async save operations complete

## Unit Tests

- Write tests for every public method; cover happy path, edge cases, errors
- **Never delete existing tests.** Total count must not decrease
- Read source and existing tests first; mirror patterns
- **Verify every referenced type** via `grep`. **Build is hard pre-commit gate**
- **NEVER widen visibility** for test access. Use `InternalsVisibleTo`
- **Non-trivial tests**: Assert observable behavior; `*LoggingTests` assert specific log messages
- **Test placement**: Worker tests in `tests/CopilotHive.Tests/Worker/`; one `*Tests.cs` per class
- **Integration tests**: Use `WebApplicationFactory<Program>` — never `CreateBuilder`
- **Git integration tests**: Use `git symbolic-ref HEAD` for default branch; assert observable behavior

## File Edits & Concurrency

- Verify edits by grepping distinctive string
- **New files**: After creating, run `glob` to confirm
- **Resource serialization**: Use `SemaphoreSlim(1,1)` for non-reentrant ops; await in try/finally
- Avoid check-then-act races; use `lock` or `ConcurrentDictionary` patterns
- **`ConcurrentDictionary`**: Check condition *before* `TryRemove`
- **Minimal diff**: Don't touch method bodies unless required
- Public members need XML docs (`<summary>`, `<param>`, `<returns>`)
- **Keep XML docs in sync**: Update when behavior changes

## Git & Reporting

**CRITICAL: Verify changes**: Run `git status` or `git diff --stat` after editing. Zero changes = FAILURE — retry.

**Verify commit**: Run `git log --oneline -1` after committing. Only commit related files.

After edits, builds, tests, commits, call `report_code_changes` with:
- `verdict`: "PASS" or "FAIL"
- `filesModified`: array of changed files
- `summary`: brief description
