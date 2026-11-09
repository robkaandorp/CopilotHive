#!/bin/sh
# token-free forwarder: record argv/prompt bytes/env/cwd, then exec real production helper
{ printf 'ARGC=%s\n' "$#"; printf 'CWD=%s\n' "$(pwd)"; printf 'PROMPT_BEGIN\n%s' "$1" ; printf '\nPROMPT_END\n'; } >> "$DIAG_OUT/prompts.raw"
printf '%s' "$1" | od -An -c >> "$DIAG_OUT/prompts.od"; echo ---- >> "$DIAG_OUT/prompts.od"
env | sed 's/ghp_host_askpass_secret/<TOKEN>/g' | grep -E '^(GIT_|GCM_|COPILOTHIVE|REAL)' | sort >> "$DIAG_OUT/helper-env.txt"; echo ---- >> "$DIAG_OUT/helper-env.txt"
out=$("$REAL_ASKPASS" "$@"); rc=$?
printf 'HELPER_EXIT=%s STDOUT_LEN=%s\n' "$rc" "${#out}" >> "$DIAG_OUT/helper-rc.txt"
printf '%s' "$out"; exit $rc
