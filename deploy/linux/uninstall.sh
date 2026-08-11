#!/usr/bin/env sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

systemctl disable --now assetbee-drone.service 2>/dev/null || true
rm -f /etc/systemd/system/assetbee-drone.service
systemctl daemon-reload
rm -rf /opt/assetbee-drone /etc/assetbee-drone

echo "AssetBee Drone uninstalled."
