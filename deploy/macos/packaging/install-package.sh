#!/bin/sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: install-package.sh PACKAGE --endpoint URL (--bearer-token TOKEN | --api-key KEY)
       install-package.sh PACKAGE --settings-file PATH

Write configuration then install an AssetBee Drone .pkg with the macOS installer.
EOF
  exit 1
}

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root." >&2
  exit 1
fi

package=""
endpoint=""
bearer_token=""
api_key=""
settings_file=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --endpoint)
      endpoint="${2:?}"
      shift 2
      ;;
    --bearer-token)
      bearer_token="${2:?}"
      shift 2
      ;;
    --api-key)
      api_key="${2:?}"
      shift 2
      ;;
    --settings-file)
      settings_file="${2:?}"
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
      if [ -n "$package" ]; then
        echo "Unexpected argument: $1" >&2
        usage
      fi
      package="$1"
      shift
      ;;
  esac
done

[ -n "$package" ] || usage
[ -f "$package" ] || {
  echo "Package not found: $package" >&2
  exit 1
}

install_dir="/Library/AssetBee/Drone"
install -d -m 0700 "$install_dir"

if [ -n "$settings_file" ]; then
  if [ -n "$endpoint" ] || [ -n "$bearer_token" ] || [ -n "$api_key" ]; then
    echo "Do not combine --settings-file with --endpoint/--bearer-token/--api-key." >&2
    exit 1
  fi
  [ -f "$settings_file" ] || {
    echo "Settings file not found: $settings_file" >&2
    exit 1
  }
  install -m 0600 "$settings_file" "$install_dir/appsettings.json"
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
installer -pkg "$package" -target /
echo "AssetBee Drone package installed."
