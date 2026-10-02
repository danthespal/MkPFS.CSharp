#!/usr/bin/env bash
# Print Markdown release notes for <tag>: downloads, changelog since the previous v* tag,
# contributors, and GitHub's generated notes (merged PRs, new contributors, full diff link).
# Needs: git history with tags (fetch-depth 0), gh CLI with GH_TOKEN.
# Usage: tools/ci/release-notes.sh v2.0.0 owner/repo > notes.md
set -euo pipefail

tag="${1:?usage: release-notes.sh <tag> <owner/repo>}"
repo="${2:?usage: release-notes.sh <tag> <owner/repo>}"
version="${tag#v}"
prev="$(git describe --tags --abbrev=0 --match 'v*' "${tag}^" 2>/dev/null || true)"
range="${prev:+${prev}..}${tag}"

# Contributors: GitHub login when the commit email is linked to an account, else the git name.
jq_author='if .author then "@" + .author.login else .commit.author.name end'
if [ -n "$prev" ]; then
  contributors="$(gh api "repos/${repo}/compare/${prev}...${tag}" --jq ".commits[] | ${jq_author}")"
else
  contributors="$(gh api --paginate "repos/${repo}/commits?sha=${tag}" --jq ".[] | ${jq_author}")"
fi
contributors="$(printf '%s\n' "$contributors" | grep -v '\[bot\]$' | sort -fu | paste -sd ',' - | sed 's/,/, /g')"

# GitHub's own notes; omitted when the API refuses (for example an unknown tag in local tests).
generate_args=(-X POST "repos/${repo}/releases/generate-notes" -f "tag_name=${tag}")
if [ -n "$prev" ]; then
  generate_args+=(-f "previous_tag_name=${prev}")
fi
generated="$(gh api "${generate_args[@]}" --jq .body 2>/dev/null || true)"

cat <<EOF
# MkPFS.C# ${version}

## Downloads

| Platform | Command line | Desktop app |
|---|---|---|
| Windows x64 | \`mkpfs-${version}-win-x64.zip\` | \`mkpfs-gui-${version}-win-x64.zip\` |
| Linux x64 | \`mkpfs-${version}-linux-x64.tar.gz\` | \`mkpfs-gui-${version}-linux-x64.tar.gz\` |
| macOS Apple silicon | \`mkpfs-${version}-osx-arm64.tar.gz\` | \`mkpfs-gui-${version}-osx-arm64.tar.gz\` |

Keep each program with the libraries next to it (\`mkpfs_zlib\`, and Skia/HarfBuzz for the desktop
app). On macOS the desktop app is \`MkPFS.C#.app\`; it is not notarized, so open it the first time
with right-click > Open. Verify downloads with \`SHA256SUMS.txt\`.

## Changes${prev:+ since ${prev}}

EOF
git log --no-merges --pretty="format:- %s ([%h](https://github.com/${repo}/commit/%H))" "$range"
printf '\n\n## Contributors\n\n%s\n' "${contributors:-none}"
# GitHub's body brings its own headings ("What's Changed", "New Contributors") and the diff link.
if [ -n "$generated" ]; then
  printf '\n%s\n' "$generated"
fi
