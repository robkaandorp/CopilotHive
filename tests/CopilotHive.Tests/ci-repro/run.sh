#!/bin/bash
# usage: run.sh <gitbin> <label> <input> [extra env assignments...]
GIT=$1; LABEL=$2; INPUT=$3; shift 3
OUT=/tmp/diag/out/$LABEL; rm -rf $OUT; mkdir -p $OUT
rm -rf /tmp/diag/work/repo; "$GIT" init -q /tmp/diag/work/repo
( set +u; env -i bash -c '
  source /tmp/diag/child-env.sh
  export PATH=/usr/bin:/bin HOME=/tmp/diag/work/home
  export GIT_ASKPASS=/tmp/diag/fwd.sh REAL_ASKPASS=/tmp/diag/production-askpass.sh DIAG_OUT='$OUT'
  for kv in "$@"; do export "$kv"; done
  cd /tmp/diag/work/repo
  printf "%b" "'"$INPUT"'" | '"$GIT"' credential fill > '$OUT'/stdout 2> '$OUT'/stderr; echo $? > '$OUT'/exit
' _ "$@" )
echo "== $LABEL ($($GIT --version)) exit=$(cat $OUT/exit)"
echo "-- stdout:"; sed 's/ghp_host_askpass_secret/<TOKEN>/g' $OUT/stdout
echo "-- stderr:"; cat $OUT/stderr
echo "-- prompts (od -c):"; cat $OUT/prompts.od 2>/dev/null || echo "(NO PROMPT LOG: helper never invoked)"
echo "-- helper rc:"; cat $OUT/helper-rc.txt 2>/dev/null
