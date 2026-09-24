#!/bin/sh
set -u
umask 077
computer="${1-}"
case "$computer" in 1|2|3|4) ;; *) exit 64 ;; esac
config="$HOME/Library/Application Support/KvmBridge"
[ -d "$config" ] || exit 66
server=$(/bin/cat "$config/server-url.txt") || exit 66
case "$server" in https://*) ;; *) exit 64 ;; esac
log="$config/computer-$computer-launch.log"
if [ -f "$log" ] && [ "$(/usr/bin/stat -f %z "$log")" -gt 65536 ]; then
    /bin/mv -f "$log" "$log.previous"
fi
request=$(/usr/bin/uuidgen)
latest="$config/latest-request"
marker="$config/.request-$request"
printf '%s\n' "$request" > "$marker"
/bin/mv -f "$marker" "$latest"
errors="$config/.error-$request"
reply="$config/.reply-$request"
trap '/bin/rm -f "$errors" "$reply" "$marker"' EXIT
record() { printf '%s request=%s %s\n' "$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')" "$request" "$*" >> "$log"; }
record "start computer=$computer"
attempt=0
while :; do
    if [ "$(/bin/cat "$latest" 2>/dev/null)" != "$request" ]; then
        record 'cancelled: newer selection requested'
        exit 0
    fi
    attempt=$((attempt + 1))
    metrics=$(/usr/bin/curl --silent --show-error --fail --connect-timeout 2 --max-time 8 \
      --cacert "$config/kvmbridge-ca.pem" --config "$config/client.conf" \
      --request PUT --header 'Content-Type: application/json' \
      --data "{\"computer\":$computer}" "$server/api/kvm/selection" \
      --output "$reply" --write-out 'http=%{http_code} seconds=%{time_total}' 2> "$errors")
    result=$?
    record "attempt=$attempt curl=$result $metrics"
    /bin/cp "$errors" "$config/computer-$computer-error.log"
    if [ "$result" -eq 0 ]; then
        record "confirmed $(/bin/cat "$reply")"
        exit 0
    fi
    record "error $(/bin/cat "$errors")"
    # Curl 7 means no TCP connection was established: no selection was transmitted.
    # Never replay a request after an HTTP error or an ambiguous response timeout.
    if [ "$result" -ne 7 ] || [ "$attempt" -ge 3 ]; then exit "$result"; fi
    /bin/sleep 0.3
done
