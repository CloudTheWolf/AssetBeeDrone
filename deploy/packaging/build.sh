#!/usr/bin/env sh
set -eu

usage() {
  cat >&2 <<'EOF'
Usage: build.sh --rid RID --publish-dir DIR [--version VER] [--output-dir DIR]

Build portable archive and native package(s) for a single RID on the host OS.
Native AOT publish must already be completed for the RID.
EOF
  exit 1
}

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
repo_root="$(CDPATH= cd -- "$script_dir/../.." && pwd)"

rid=""
publish_dir=""
version=""
output_dir="$repo_root/dist"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --rid)
      rid="${2:?}"
      shift 2
      ;;
    --publish-dir)
      publish_dir="${2:?}"
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

[ -n "$rid" ] || usage
[ -n "$publish_dir" ] || usage

if [ -z "$version" ]; then
  version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$repo_root/AssetBeeDrone.csproj" | head -n 1)"
fi
[ -n "$version" ] || {
  echo "Unable to determine version." >&2
  exit 1
}

mkdir -p "$output_dir"

case "$rid" in
  win-*)
    if command -v pwsh >/dev/null 2>&1; then
      pwsh -NoProfile -File "$script_dir/build-archive.ps1" \
        -PublishDirectory "$publish_dir" -Rid "$rid" -Version "$version" -OutputDirectory "$output_dir"
      pwsh -NoProfile -File "$repo_root/deploy/windows/build-msi.ps1" \
        -PublishDirectory "$publish_dir" -Version "$version" -OutputDirectory "$output_dir"
    elif command -v powershell.exe >/dev/null 2>&1; then
      powershell.exe -NoProfile -File "$script_dir/build-archive.ps1" \
        -PublishDirectory "$publish_dir" -Rid "$rid" -Version "$version" -OutputDirectory "$output_dir"
      powershell.exe -NoProfile -File "$repo_root/deploy/windows/build-msi.ps1" \
        -PublishDirectory "$publish_dir" -Version "$version" -OutputDirectory "$output_dir"
    else
      # Non-Windows hosts can still produce the zip when zip is available.
      sh "$script_dir/build-archive.sh" \
        --publish-dir "$publish_dir" --rid "$rid" --version "$version" --output-dir "$output_dir"
      echo "Skipping MSI build (PowerShell/WiX host required)." >&2
    fi
    ;;
  linux-*)
    sh "$script_dir/build-archive.sh" \
      --publish-dir "$publish_dir" --rid "$rid" --version "$version" --output-dir "$output_dir"
    sh "$repo_root/deploy/linux/packaging/build-deb.sh" \
      --publish-dir "$publish_dir" --rid "$rid" --version "$version" --output-dir "$output_dir"
    if command -v rpmbuild >/dev/null 2>&1; then
      sh "$repo_root/deploy/linux/packaging/build-rpm.sh" \
        --publish-dir "$publish_dir" --rid "$rid" --version "$version" --output-dir "$output_dir"
    else
      echo "Skipping RPM build (rpmbuild not installed)." >&2
    fi
    ;;
  osx-*)
    sh "$script_dir/build-archive.sh" \
      --publish-dir "$publish_dir" --rid "$rid" --version "$version" --output-dir "$output_dir"
    sh "$repo_root/deploy/macos/packaging/build-pkg.sh" \
      --publish-dir "$publish_dir" --rid "$rid" --version "$version" --output-dir "$output_dir"
    ;;
  *)
    echo "Unsupported RID: $rid" >&2
    exit 1
    ;;
esac

echo "Packaging complete for $rid -> $output_dir"
