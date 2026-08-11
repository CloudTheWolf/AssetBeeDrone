#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

launchctl bootout system/com.assetbee.drone 2>/dev/null || true
rm -f /Library/LaunchDaemons/com.assetbee.drone.plist
rm -rf /Library/AssetBee/Drone

echo "AssetBee Drone uninstalled."
