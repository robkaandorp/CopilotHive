# Askpass CI repro (diagnostic only, not part of the test run)

1. `HIVE_ASKPASS_DIAG_DIR=/tmp/diag dotnet test --project tests/CopilotHive.Tests -- --filter-class "*AskpassCiReproDiagnostics"`
   exports the production askpass script and `BuildChildEnvironment("ghp_host_askpass_secret")` (synthetic token).
2. Copy `fwd.sh run.sh run2.sh ask.sh srv.py` into `/tmp/diag` and run
   `./run.sh <path-to-git> <label> 'protocol=https\nhost=github.com\n\n'` (real `git credential fill`; records argv,
   raw prompt bytes via `od -c`, helper exit, env) or `./run2.sh <git> <label> "<GIT_CONFIG_PARAMETERS>" <GIT_CONFIG_COUNT>`
   to vary the credential.interactive channels.
Finding: git >= 2.47.0 never invokes askpass when `credential.interactive=false` (git credential_getpass()).
