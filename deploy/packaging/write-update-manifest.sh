#!/usr/bin/env bash
# Write a latest.json auto-update manifest for staged release assets.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: write-update-manifest.sh --version <ver> --asset-dir <dir> --download-base <url> [--output <file>]

Creates latest.json describing MSI/deb/rpm/pkg assets for AssetBee Drone auto-update.
EOF
}

version=""
asset_dir=""
download_base=""
output=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      version="${2:-}"
      shift 2
      ;;
    --asset-dir)
      asset_dir="${2:-}"
      shift 2
      ;;
    --download-base)
      download_base="${2:-}"
      shift 2
      ;;
    --output)
      output="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ -z "$version" || -z "$asset_dir" || -z "$download_base" ]]; then
  usage >&2
  exit 1
fi

if [[ -z "$output" ]]; then
  output="$asset_dir/latest.json"
fi

download_base="${download_base%/}"

rids=(win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64)
packages_json=""
count=0

shopt -s nullglob
for asset in "$asset_dir"/*.msi "$asset_dir"/*.deb "$asset_dir"/*.rpm "$asset_dir"/*.pkg; do
  file_name="$(basename -- "$asset")"
  stem="${file_name%.*}"
  prefix="AssetBee.Drone-"
  if [[ "$stem" != "$prefix"* ]]; then
    echo "Skipping unrecognized asset name: $file_name" >&2
    continue
  fi

  rest="${stem#"$prefix"}"
  rid=""
  for candidate in "${rids[@]}"; do
    if [[ "$rest" == *"-${candidate}" ]]; then
      rid="$candidate"
      break
    fi
  done

  if [[ -z "$rid" ]]; then
    echo "Skipping asset with unknown RID: $file_name" >&2
    continue
  fi

  sha256="$(sha256sum -- "$asset" | awk '{print $1}')"
  url="${download_base}/${file_name}"

  if [[ $count -gt 0 ]]; then
    packages_json+=","
  fi
  packages_json+=$(printf '\n    {"rid":"%s","fileName":"%s","sha256":"%s","url":"%s"}' \
    "$rid" "$file_name" "$sha256" "$url")
  count=$((count + 1))
done

if [[ $count -eq 0 ]]; then
  echo "No package assets found in $asset_dir" >&2
  exit 1
fi

umask 022
cat >"$output" <<EOF
{
  "version": "${version}",
  "packages": [${packages_json}
  ]
}
EOF

echo "Wrote update manifest ($count packages) to $output"
