#!/bin/bash
# Docker health check without curl/wget in the image: ask Clearr's /ping over a plain TCP socket.
exec 3<>"/dev/tcp/127.0.0.1/${CLEARR_PORT:-8585}" || exit 1
printf 'GET /ping HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
timeout 5 cat <&3 | tail -n 1 | grep -qx 'ok'
