#!/bin/sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: install.sh PUBLISH_DIRECTORY --endpoint URL (--bearer-token TOKEN | --api-key KEY)
       install.sh PUBLISH_DIRECTORY --settings-file PATH

Install AssetBee Drone from a published directory and configure authentication.
EOF
  exit 1
}

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

publish_dir=""
endpoint=""
bearer_token=""
api_key=""
settings_file=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --endpoint)
      endpoint="${2:?--endpoint requires a value}"
      shift 2
      ;;
    --bearer-token)
      bearer_token="${2:?--bearer-token requires a value}"
      shift 2
      ;;
    --api-key)
      api_key="${2:?--api-key requires a value}"
      shift 2
      ;;
    --settings-file)
      settings_file="${2:?--settings-file requires a value}"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    --*)
      echo "Unknown option: $1" >&2
      usage
      ;;
    *)
      if [ -n "$publish_dir" ]; then
        echo "Unexpected argument: $1" >&2
        usage
      fi
      publish_dir="$1"
      shift
      ;;
  esac
done

[ -n "$publish_dir" ] || usage

if [ -n "$settings_file" ]; then
  if [ -n "$endpoint" ] || [ -n "$bearer_token" ] || [ -n "$api_key" ]; then
    echo "Do not combine --settings-file with --endpoint/--bearer-token/--api-key." >&2
    exit 1
  fi
  if [ ! -f "$settings_file" ]; then
    echo "Settings file not found: $settings_file" >&2
    exit 1
  fi
else
  [ -n "$endpoint" ] || usage

  case "$endpoint" in
    https://*) ;;
    *)
      echo "The inventory endpoint must use HTTPS." >&2
      exit 1
      ;;
  esac

  if [ -n "$bearer_token" ] && [ -n "$api_key" ]; then
    echo "Provide exactly one of --bearer-token or --api-key." >&2
    exit 1
  fi

  if [ -z "$bearer_token" ] && [ -z "$api_key" ]; then
    echo "Provide exactly one of --bearer-token or --api-key." >&2
    exit 1
  fi
fi

if [ ! -d "$publish_dir" ]; then
  echo "Publish directory not found: $publish_dir" >&2
  exit 1
fi

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
install_dir="/Library/AssetBee/Drone"
plist="/Library/LaunchDaemons/com.assetbee.drone.plist"

launchctl bootout system/com.assetbee.drone 2>/dev/null || true
install -d -m 0700 "$install_dir"
cp -R "$publish_dir"/. "$install_dir"/
chmod 0700 "$install_dir/AssetBee.Drone"

if [ -n "$settings_file" ]; then
  install -m 0600 "$settings_file" "$install_dir/appsettings.json"
else
  tmp_settings="$(mktemp)"
  trap 'rm -f "$tmp_settings"' EXIT
  if [ -n "$bearer_token" ]; then
    auth_json="\"BearerToken\": \"$bearer_token\", \"ApiKey\": null"
  else
    auth_json="\"BearerToken\": null, \"ApiKey\": \"$api_key\""
  fi
  cat >"$tmp_settings" <<EOF
{
  "Drone": {
    "Endpoint": "$endpoint",
    "CollectionIntervalMinutes": 3600,
    "RequestTimeoutSeconds": 30,
    "MaxRetryAttempts": 3,
    $auth_json,
    "Type": null,
    "Debug": false,
    "DebugOutputPath": "/var/tmp/assetbee-drone-inventory-debug.json"
  }
}
EOF
  install -m 0600 "$tmp_settings" "$install_dir/appsettings.json"
fi

chown -R root:wheel "$install_dir"
install -o root -g wheel -m 0644 "$script_dir/com.assetbee.drone.plist" "$plist"
launchctl bootstrap system "$plist"
launchctl enable system/com.assetbee.drone
launchctl kickstart -k system/com.assetbee.drone

echo "AssetBee Drone installed at $install_dir and started."
