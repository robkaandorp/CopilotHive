#!/bin/bash
# like run.sh but replaces config channels using sed on the sourced env
GIT=$1; LABEL=$2; CFGPARAMS=$3; CNT=$4
OUT=/tmp/diag/out/$LABEL; rm -rf $OUT; mkdir -p $OUT; rm -rf /tmp/diag/work/repo; "$GIT" init -q /tmp/diag/work/repo
env -i bash -c '
  source /tmp/diag/child-env.sh
  export PATH=/usr/bin:/bin HOME=/tmp/diag/work/home GIT_ASKPASS=/tmp/diag/fwd.sh REAL_ASKPASS=/tmp/diag/production-askpass.sh DIAG_OUT='$OUT'
  export GIT_CONFIG_PARAMETERS="$1"; export GIT_CONFIG_COUNT="$2"
  [ "$2" = 1 ] && unset GIT_CONFIG_KEY_1 GIT_CONFIG_VALUE_1
  [ "$2" = 0 ] && unset GIT_CONFIG_KEY_0 GIT_CONFIG_VALUE_0 GIT_CONFIG_KEY_1 GIT_CONFIG_VALUE_1
  cd /tmp/diag/work/repo
  printf "protocol=https\nhost=github.com\n\n" | '$GIT' credential fill > '$OUT'/stdout 2> '$OUT'/stderr; echo $? > '$OUT'/exit' _ "$CFGPARAMS" "$CNT"
echo "== $LABEL exit=$(cat $OUT/exit) stderr=$(cat $OUT/stderr) helper_invoked=$([ -s $OUT/prompts.od ] && echo yes || echo NO)"
