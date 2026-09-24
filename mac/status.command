#!/bin/sh
set -eu
config="$HOME/Library/Application Support/KvmBridge"
server=$(/bin/cat "$config/server-url.txt")
exec /usr/bin/curl --silent --show-error --fail --connect-timeout 3 --max-time 10 \
  --cacert "$config/kvmbridge-ca.pem" --config "$config/client.conf" "$server/api/kvm/status"
