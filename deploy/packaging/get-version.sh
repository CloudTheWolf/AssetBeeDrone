#!/usr/bin/env sh
# Prints a SemVer-compatible version for packaging.
# Priority: VERSION env, git tag (v*), GITHUB_RUN_NUMBER, git commit count, fallback 1.0.0
set -eu

if [ -n "${VERSION:-}" ]; then
  printf '%s\n' "$VERSION"
  exit 0
fi

# GitHub Actions tag builds (refs/tags/v1.2.3)
ref="${GITHUB_REF:-}"
if [ -n "$ref" ] && [ "${ref#refs/tags/v}" != "$ref" ]; then
  printf '%s\n' "${ref#refs/tags/v}"
  exit 0
fi

if [ -n "${GITHUB_RUN_NUMBER:-}" ]; then
  printf '1.0.%s\n' "$GITHUB_RUN_NUMBER"
  exit 0
fi

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
repo_root="$(CDPATH= cd -- "$script_dir/../.." && pwd)"

if command -v git >/dev/null 2>&1 && git -C "$repo_root" rev-parse --git-dir >/dev/null 2>&1; then
  # Prefer an exact tag on HEAD when present.
  tag="$(git -C "$repo_root" describe --tags --exact-match 2>/dev/null || true)"
  case "$tag" in
    v*)
      printf '%s\n' "${tag#v}"
      exit 0
      ;;
  esac

  count="$(git -C "$repo_root" rev-list --count HEAD 2>/dev/null || true)"
  if [ -n "$count" ] && [ "$count" -gt 0 ]; then
    printf '1.0.%s\n' "$count"
    exit 0
  fi
fi

printf '1.0.0\n'
