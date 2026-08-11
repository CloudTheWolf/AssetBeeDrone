#!/usr/bin/env sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: build-rpm.sh --publish-dir DIR [--version VER] [--arch ARCH] [--rid RID] [--output-dir DIR]

Build an AssetBee Drone .rpm from a Native AOT publish directory.
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
[ -f "$publish_dir/AssetBee.Drone" ] || {
  echo "Published binary not found in $publish_dir" >&2
  exit 1
}

if [ -z "$version" ]; then
  version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$repo_root/AssetBeeDrone.csproj" | head -n 1)"
fi
[ -n "$version" ] || {
  echo "Unable to determine version." >&2
  exit 1
}

if [ -z "$arch" ]; then
  case "$rid" in
    linux-arm64) arch="aarch64" ;;
    linux-x64|"") arch="x86_64" ;;
    *)
      echo "Cannot map RID '$rid' to RPM arch; pass --arch." >&2
      exit 1
      ;;
  esac
fi

command -v rpmbuild >/dev/null 2>&1 || {
  echo "rpmbuild is required to build .rpm packages." >&2
  exit 1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/BUILD" "$work/RPMS" "$work/SOURCES" "$work/SPECS" "$work/SRPMS" "$work/BUILDROOT"

payload="$work/SOURCES/assetbee-drone-$version"
install -d "$payload/opt/assetbee-drone" "$payload/etc/assetbee-drone" "$payload/etc/systemd/system"
cp -R "$publish_dir"/. "$payload/opt/assetbee-drone/"
chmod 0755 "$payload/opt/assetbee-drone/AssetBee.Drone"
install -m 0644 "$linux_dir/assetbee-drone.service" "$payload/etc/systemd/system/assetbee-drone.service"
install -m 0644 "$linux_dir/environment.example" "$payload/etc/assetbee-drone/environment.example"
tar -C "$work/SOURCES" -czf "$work/SOURCES/assetbee-drone-$version.tar.gz" "assetbee-drone-$version"

cat >"$work/SPECS/assetbee-drone.spec" <<EOF
Name:           assetbee-drone
Version:        $version
Release:        1%{?dist}
Summary:        AssetBee Drone device inventory service
License:        Proprietary
URL:            https://assetbee.local
Source0:        assetbee-drone-$version.tar.gz
BuildArch:      $arch
Requires:       systemd

%description
Collects and securely reports device inventory over HTTPS.

%prep
%setup -q -n assetbee-drone-$version

%build

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a opt etc %{buildroot}/

%preun
if [ \$1 -eq 0 ]; then
  systemctl disable --now assetbee-drone.service 2>/dev/null || true
fi

%post
systemctl daemon-reload || true
if [ -f /etc/assetbee-drone/environment ] && grep -q '^Drone__Endpoint=https://' /etc/assetbee-drone/environment; then
  systemctl enable --now assetbee-drone.service || true
else
  echo "AssetBee Drone is installed but not started." >&2
  echo "Configure /etc/assetbee-drone/environment then: systemctl enable --now assetbee-drone.service" >&2
fi

%postun
if [ \$1 -eq 0 ]; then
  rm -f /etc/systemd/system/assetbee-drone.service
  systemctl daemon-reload 2>/dev/null || true
fi

%files
%dir /opt/assetbee-drone
/opt/assetbee-drone/*
%config(noreplace) /etc/assetbee-drone/environment.example
/etc/systemd/system/assetbee-drone.service
EOF

rpmbuild \
  --define "_topdir $work" \
  --define "_build_id_links none" \
  --target "$arch" \
  -bb "$work/SPECS/assetbee-drone.spec"

mkdir -p "$output_dir"
case "$arch" in
  x86_64) rid_label="linux-x64" ;;
  aarch64) rid_label="linux-arm64" ;;
  *) rid_label="linux-$arch" ;;
esac
built="$(find "$work/RPMS" -type f -name '*.rpm' | head -n 1)"
[ -n "$built" ] || {
  echo "rpmbuild did not produce an RPM." >&2
  exit 1
}
output="$output_dir/AssetBee.Drone-${version}-${rid_label}.rpm"
cp "$built" "$output"
echo "Created $output"
