#!/usr/bin/env bash
# Fails when the packed tool is missing a file agents or NuGet read from the package root.
# Entry names are matched exactly. A csproj PackagePath mistake once produced
# skills//split-types/SKILL.md, which a substring match would have accepted.
# Usage: verify-package-contents.sh <directory holding exactly one .nupkg>
set -euo pipefail

shopt -s nullglob
packages=("${1:?usage: verify-package-contents.sh <artifacts directory>}"/*.nupkg)
if [ "${#packages[@]}" -ne 1 ]; then
  echo "::error::expected exactly one .nupkg in $1, found ${#packages[@]}"
  exit 1
fi

entries="$(unzip -Z1 "${packages[0]}")"
missing=0
for required in README.md AGENTS.md icon.png skills/split-types/SKILL.md; do
  if ! grep -qxF "$required" <<<"$entries"; then
    echo "::error::${packages[0]} has no entry named $required"
    missing=1
  fi
done

exit "$missing"
