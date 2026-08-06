#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

publish_dir="${1:?Usage: install.sh PUBLISH_DIRECTORY APPSETTINGS_FILE}"
settings_file="${2:?Usage: install.sh PUBLISH_DIRECTORY APPSETTINGS_FILE}"
install_dir="/Library/AssetBee/Drone"
plist="/Library/LaunchDaemons/com.assetbee.drone.plist"

launchctl bootout system/com.assetbee.drone 2>/dev/null || true
install -d -m 0700 "$install_dir"
cp -R "$publish_dir"/. "$install_dir"/
install -m 0600 "$settings_file" "$install_dir/appsettings.json"
chmod 0700 "$install_dir/AssetBee.Drone"
chown -R root:wheel "$install_dir"
install -o root -g wheel -m 0644 "$(dirname "$0")/com.assetbee.drone.plist" "$plist"
launchctl bootstrap system "$plist"
launchctl enable system/com.assetbee.drone
launchctl kickstart -k system/com.assetbee.drone
