#!/bin/sh
# Docker health check: Clearr answers "ok" on /ping (busybox wget, nothing extra in the image).
wget -q -T 5 -O - "http://127.0.0.1:${CLEARR_PORT:-8585}/ping" | grep -qx ok
