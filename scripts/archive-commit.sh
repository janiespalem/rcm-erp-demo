#!/usr/bin/env bash
# Emit only the committed tree; neither tracked edits nor untracked files enter
# the build. On a non-Git deployment host, supply a git bundle as argument 2.
set -Eeuo pipefail
sha="${1:-}"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || { echo 'Expected a full commit SHA' >&2; exit 2; }
if [[ -n "${2:-}" ]]; then
    repository=$(mktemp -d /tmp/demo-release-objects-XXXXXX)
    trap 'rm -rf -- "$repository"' EXIT
    git clone --quiet --bare "$2" "$repository"
    export GIT_DIR="$repository"
fi
[[ $(git rev-parse --verify "$sha^{commit}") == "$sha" ]]
git archive --format=tar "$sha"
