#!/usr/bin/env sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

publish_dir="${1:?Usage: install.sh PUBLISH_DIRECTORY}"
install_dir="/opt/assetbee-drone"

install -d -m 0755 "$install_dir" /etc/assetbee-drone
cp -R "$publish_dir"/. "$install_dir"/
chmod 0755 "$install_dir/AssetBee.Drone"
if [ ! -f /etc/assetbee-drone/environment ]; then
  install -m 0600 "$(dirname "$0")/environment.example" /etc/assetbee-drone/environment
fi
install -m 0644 "$(dirname "$0")/assetbee-drone.service" /etc/systemd/system/assetbee-drone.service
systemctl daemon-reload
systemctl enable --now assetbee-drone.service
