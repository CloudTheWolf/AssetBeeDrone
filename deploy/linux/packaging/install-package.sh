#!/usr/bin/env sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: install-package.sh PACKAGE --endpoint URL (--bearer-token TOKEN | --api-key KEY)

Write /etc/assetbee-drone/environment then install a .deb or .rpm package.
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
[ -n "$endpoint" ] || usage
[ -f "$package" ] || {
  echo "Package not found: $package" >&2
  exit 1
}

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

install -d -m 0755 /etc/assetbee-drone
tmp_env="$(mktemp)"
trap 'rm -f "$tmp_env"' EXIT
{
  printf 'Drone__Endpoint=%s\n' "$endpoint"
  printf 'Drone__CollectionIntervalMinutes=360\n'
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
install -m 0600 "$tmp_env" /etc/assetbee-drone/environment

case "$package" in
  *.deb)
    dpkg -i "$package"
    ;;
  *.rpm)
    if command -v rpm >/dev/null 2>&1; then
      rpm -Uvh "$package"
    else
      echo "rpm is required to install .rpm packages." >&2
      exit 1
    fi
    ;;
  *)
    echo "Unsupported package type: $package (expected .deb or .rpm)" >&2
    exit 1
    ;;
esac

systemctl enable --now assetbee-drone.service
echo "AssetBee Drone package installed and started."
