#!/usr/bin/env sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: build-archive.sh --publish-dir DIR --rid RID [--version VER] [--output-dir DIR]

Create a portable archive containing the published binary and install scripts.
EOF
  exit 1
}

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
repo_root="$(CDPATH= cd -- "$script_dir/../.." && pwd)"

publish_dir=""
rid=""
version=""
output_dir="$repo_root/dist"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --publish-dir)
      publish_dir="${2:?}"
      shift 2
      ;;
    --rid)
      rid="${2:?}"
      shift 2
      ;;
    --version)
      version="${2:?}"
      shift 2
      ;;
    --output-dir)
      output_dir="${2:?}"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      ;;
  esac
done

[ -n "$publish_dir" ] || usage
[ -n "$rid" ] || usage
[ -d "$publish_dir" ] || {
  echo "Publish directory not found: $publish_dir" >&2
  exit 1
}

if [ -z "$version" ]; then
  version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$repo_root/AssetBeeDrone.csproj" | head -n 1)"
fi
[ -n "$version" ] || {
  echo "Unable to determine version." >&2
  exit 1
}

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
payload="$stage/AssetBee.Drone-$version-$rid"
mkdir -p "$payload/bin" "$payload/deploy"

cp -R "$publish_dir"/. "$payload/bin/"
# Avoid shipping secrets from a developer appsettings.json.
if [ -f "$payload/bin/appsettings.json" ]; then
  cat >"$payload/bin/appsettings.json" <<'EOF'
{
  "Drone": {
    "Endpoint": "https://inventory.example.com/api/v1/inventory",
    "CollectionIntervalMinutes": 360,
    "RequestTimeoutSeconds": 30,
    "MaxRetryAttempts": 3,
    "BearerToken": null,
    "ApiKey": null,
    "Type": null,
    "Debug": false,
    "DebugOutputPath": "inventory-debug.json"
  }
}
EOF
fi

case "$rid" in
  win-*)
    mkdir -p "$payload/deploy/windows"
    cp "$repo_root/deploy/windows/install.ps1" "$payload/deploy/windows/"
    cp "$repo_root/deploy/windows/uninstall.ps1" "$payload/deploy/windows/"
    cat >"$payload/INSTALL.txt" <<EOF
AssetBee Drone $version ($rid)

From an elevated PowerShell session:

  .\\deploy\\windows\\install.ps1 \`
    -PublishDirectory .\\bin \`
    -Endpoint https://inventory.example.com/api/v1/inventory \`
    -BearerToken 'secret'

Uninstall:

  .\\deploy\\windows\\uninstall.ps1
EOF
    ;;
  linux-*)
    mkdir -p "$payload/deploy/linux"
    cp "$repo_root/deploy/linux/install.sh" "$payload/deploy/linux/"
    cp "$repo_root/deploy/linux/uninstall.sh" "$payload/deploy/linux/"
    cp "$repo_root/deploy/linux/assetbee-drone.service" "$payload/deploy/linux/"
    cp "$repo_root/deploy/linux/environment.example" "$payload/deploy/linux/"
    chmod 0755 "$payload/deploy/linux/install.sh" "$payload/deploy/linux/uninstall.sh"
    cat >"$payload/INSTALL.txt" <<EOF
AssetBee Drone $version ($rid)

  sudo ./deploy/linux/install.sh ./bin \\
    --endpoint https://inventory.example.com/api/v1/inventory \\
    --bearer-token secret

Uninstall:

  sudo ./deploy/linux/uninstall.sh
EOF
    ;;
  osx-*)
    mkdir -p "$payload/deploy/macos"
    cp "$repo_root/deploy/macos/install.sh" "$payload/deploy/macos/"
    cp "$repo_root/deploy/macos/uninstall.sh" "$payload/deploy/macos/"
    cp "$repo_root/deploy/macos/com.assetbee.drone.plist" "$payload/deploy/macos/"
    chmod 0755 "$payload/deploy/macos/install.sh" "$payload/deploy/macos/uninstall.sh"
    cat >"$payload/INSTALL.txt" <<EOF
AssetBee Drone $version ($rid)

  sudo ./deploy/macos/install.sh ./bin \\
    --endpoint https://inventory.example.com/api/v1/inventory \\
    --bearer-token secret

Uninstall:

  sudo ./deploy/macos/uninstall.sh
EOF
    ;;
  *)
    echo "Unsupported RID for archive: $rid" >&2
    exit 1
    ;;
esac

mkdir -p "$output_dir"
case "$rid" in
  win-*)
    output="$output_dir/AssetBee.Drone-${version}-${rid}.zip"
    if command -v zip >/dev/null 2>&1; then
      (CDPATH= cd -- "$stage" && zip -qr "$output" "AssetBee.Drone-$version-$rid")
    else
      echo "zip is required to build Windows portable archives." >&2
      exit 1
    fi
    ;;
  *)
    output="$output_dir/AssetBee.Drone-${version}-${rid}.tar.gz"
    tar -C "$stage" -czf "$output" "AssetBee.Drone-$version-$rid"
    ;;
esac

echo "Created $output"
