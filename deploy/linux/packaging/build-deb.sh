#!/usr/bin/env sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: build-deb.sh --publish-dir DIR [--version VER] [--arch ARCH] [--output-dir DIR]

Build an AssetBee Drone .deb from a Native AOT publish directory.
ARCH defaults to amd64 (linux-x64) or arm64 (linux-arm64) based on --rid when provided.
EOF
  exit 1
}

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
linux_dir="$(CDPATH= cd -- "$script_dir/.." && pwd)"
repo_root="$(CDPATH= cd -- "$linux_dir/../.." && pwd)"

publish_dir=""
version=""
arch=""
rid=""
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
    --arch)
      arch="${2:?}"
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
[ -x "$publish_dir/AssetBee.Drone" ] || [ -f "$publish_dir/AssetBee.Drone" ] || {
  echo "Published binary not found in $publish_dir" >&2
  exit 1
}

if [ -z "$version" ]; then
  version="$(sh "$repo_root/deploy/packaging/get-version.sh")"
fi
[ -n "$version" ] || {
  echo "Unable to determine version." >&2
  exit 1
}

if [ -z "$arch" ]; then
  case "$rid" in
    linux-arm64) arch="arm64" ;;
    linux-x64|"") arch="amd64" ;;
    *)
      echo "Cannot map RID '$rid' to Debian arch; pass --arch." >&2
      exit 1
      ;;
  esac
fi

command -v dpkg-deb >/dev/null 2>&1 || {
  echo "dpkg-deb is required to build .deb packages." >&2
  exit 1
}

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

install -d -m 0755 \
  "$stage/DEBIAN" \
  "$stage/opt/assetbee-drone" \
  "$stage/etc/assetbee-drone" \
  "$stage/etc/systemd/system"

cp -R "$publish_dir"/. "$stage/opt/assetbee-drone/"
chmod 0755 "$stage/opt/assetbee-drone/AssetBee.Drone"
install -m 0644 "$linux_dir/assetbee-drone.service" "$stage/etc/systemd/system/assetbee-drone.service"
install -m 0644 "$linux_dir/environment.example" "$stage/etc/assetbee-drone/environment.example"

installed_size="$(du -sk "$stage/opt" "$stage/etc" | awk '{s+=$1} END {print s}')"

cat >"$stage/DEBIAN/control" <<EOF
Package: assetbee-drone
Version: $version
Section: utils
Priority: optional
Architecture: $arch
Maintainer: AssetBee <support@assetbee.local>
Installed-Size: $installed_size
Depends: systemd
Description: AssetBee Drone device inventory service
 Collects and securely reports device inventory over HTTPS.
EOF

install -m 0755 "$script_dir/deb/DEBIAN/postinst" "$stage/DEBIAN/postinst"
install -m 0755 "$script_dir/deb/DEBIAN/prerm" "$stage/DEBIAN/prerm"
install -m 0755 "$script_dir/deb/DEBIAN/postrm" "$stage/DEBIAN/postrm"

mkdir -p "$output_dir"
case "$arch" in
  amd64) rid_label="linux-x64" ;;
  arm64) rid_label="linux-arm64" ;;
  *) rid_label="linux-$arch" ;;
esac
output="$output_dir/AssetBee.Drone-${version}-${rid_label}.deb"
dpkg-deb --build --root-owner-group "$stage" "$output"
echo "Created $output"
