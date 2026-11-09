#!/bin/sh
printf '%s' "$1" | od -An -c >> /tmp/diag/urlprompts.od; echo --- >> /tmp/diag/urlprompts.od; exit 1
