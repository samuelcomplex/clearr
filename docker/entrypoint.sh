#!/bin/sh
# Starts Clearr as PUID:PGID (default 1000:1000, the linuxserver.io convention),
# so files in /config belong to you instead of root.
set -eu

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
umask "${UMASK:-022}"

# Already started as a normal user (compose "user:" or rootless Docker): nothing to switch.
if [ "$(id -u)" != "0" ]; then
    exec dotnet /app/Clearr.dll "$@"
fi

case "$PUID$PGID" in
    *[!0-9]*) echo "PUID and PGID must be numbers (got PUID=$PUID PGID=$PGID)" >&2; exit 1 ;;
esac

mkdir -p /config
# Only touch ownership when it's wrong, so large /config folders don't slow every start.
if [ "$(stat -c %u:%g /config)" != "$PUID:$PGID" ] || find /config -mindepth 1 \( ! -uid "$PUID" -o ! -gid "$PGID" \) -print -quit | grep -q .; then
    echo "Setting owner of /config to $PUID:$PGID"
    chown -R "$PUID:$PGID" /config
fi

echo "Starting Clearr as $PUID:$PGID"
exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups --inh-caps=-all -- dotnet /app/Clearr.dll "$@"
