#!/usr/bin/env sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

# Prefer the package manager so dpkg/rpm do not leave assetbee-drone in a
# reinst-required state that breaks unrelated apt/dnf installs.
if command -v dpkg >/dev/null 2>&1 && dpkg -l assetbee-drone >/dev/null 2>&1; then
  dpkg --remove --force-remove-reinstreq assetbee-drone || dpkg -r assetbee-drone || true
elif command -v rpm >/dev/null 2>&1 && rpm -q assetbee-drone >/dev/null 2>&1; then
  rpm -e assetbee-drone || true
else
  systemctl disable --now assetbee-drone.service 2>/dev/null || true
  rm -f /etc/systemd/system/assetbee-drone.service
  systemctl daemon-reload 2>/dev/null || true
  rm -rf /opt/assetbee-drone /etc/assetbee-drone
fi

# Portable installs and leftover state directory.
rm -rf /var/lib/assetbee-drone
systemctl daemon-reload 2>/dev/null || true

echo "AssetBee Drone uninstalled."
