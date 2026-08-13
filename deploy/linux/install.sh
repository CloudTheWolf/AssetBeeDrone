#!/usr/bin/env sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: install.sh PUBLISH_DIRECTORY --endpoint URL (--bearer-token TOKEN | --api-key KEY)

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

if [ ! -d "$publish_dir" ]; then
  echo "Publish directory not found: $publish_dir" >&2
  exit 1
fi

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
install_dir="/opt/assetbee-drone"
env_file="/etc/assetbee-drone/environment"

install -d -m 0755 "$install_dir" /etc/assetbee-drone
cp -R "$publish_dir"/. "$install_dir"/
chmod 0755 "$install_dir/AssetBee.Drone"

tmp_env="$(mktemp)"
trap 'rm -f "$tmp_env"' EXIT
{
  printf 'Drone__Endpoint=%s\n' "$endpoint"
  printf 'Drone__CollectionIntervalMinutes=3600\n'
  printf 'Drone__RequestTimeoutSeconds=30\n'
  printf 'Drone__MaxRetryAttempts=3\n'
  printf 'Drone__Type=\n'
  if [ -n "$bearer_token" ]; then
    printf 'Drone__BearerToken=%s\n' "$bearer_token"
  else
    printf 'Drone__ApiKey=%s\n' "$api_key"
  fi
  printf 'Drone__Debug=false\n'
  printf 'Drone__DebugOutputPath=/var/lib/assetbee-drone/inventory-debug.json\n'
} >"$tmp_env"
install -m 0600 "$tmp_env" "$env_file"

install -m 0644 "$script_dir/assetbee-drone.service" /etc/systemd/system/assetbee-drone.service
systemctl daemon-reload
systemctl enable --now assetbee-drone.service

echo "AssetBee Drone installed at $install_dir and started."
