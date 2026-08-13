#!/bin/sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: build-pkg.sh --publish-dir DIR [--version VER] [--rid RID] [--output-dir DIR]

Build an unsigned AssetBee Drone .pkg from a Native AOT publish directory.
Must run on macOS with pkgbuild available.
EOF
  exit 1
}

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
macos_dir="$(CDPATH= cd -- "$script_dir/.." && pwd)"
repo_root="$(CDPATH= cd -- "$macos_dir/../.." && pwd)"

publish_dir=""
version=""
rid="osx-arm64"
output_dir="$repo_root/dist"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --publish-dir)
      publish_dir="${2:?}"
      shift 2
      ;;
    --version)
      version="${2:?}"
      shift 2
      ;;
    --rid)
      rid="${2:?}"
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
[ -d "$publish_dir" ] || {
  echo "Publish directory not found: $publish_dir" >&2
  exit 1
}
[ -f "$publish_dir/AssetBee.Drone" ] || {
  echo "Published binary not found in $publish_dir" >&2
  exit 1
}

command -v pkgbuild >/dev/null 2>&1 || {
  echo "pkgbuild is required (run on macOS)." >&2
  exit 1
}

if [ -z "$version" ]; then
  version="$(sh "$repo_root/deploy/packaging/get-version.sh")"
fi
[ -n "$version" ] || {
  echo "Unable to determine version." >&2
  exit 1
}

case "$rid" in
  osx-x64|osx-arm64) ;;
  *)
    echo "Unsupported macOS RID: $rid" >&2
    exit 1
    ;;
esac

stage="$(mktemp -d)"
scripts="$(mktemp -d)"
trap 'rm -rf "$stage" "$scripts"' EXIT

install -d "$stage/Library/AssetBee/Drone"
cp -R "$publish_dir"/. "$stage/Library/AssetBee/Drone/"
chmod 0700 "$stage/Library/AssetBee/Drone/AssetBee.Drone"
# Ship plist next to the binary so postinstall can copy it into LaunchDaemons.
install -m 0644 "$macos_dir/com.assetbee.drone.plist" "$stage/Library/AssetBee/Drone/com.assetbee.drone.plist"
# Do not ship a secrets-bearing appsettings from publish output.
rm -f "$stage/Library/AssetBee/Drone/appsettings.json"

install -m 0755 "$script_dir/scripts/postinstall" "$scripts/postinstall"

mkdir -p "$output_dir"
output="$output_dir/AssetBee.Drone-${version}-${rid}.pkg"

pkgbuild \
  --root "$stage" \
  --scripts "$scripts" \
  --identifier com.assetbee.drone \
  --version "$version" \
  --install-location / \
  "$output"

echo "Created $output"
