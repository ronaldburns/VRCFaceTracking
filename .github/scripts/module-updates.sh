#!/usr/bin/env bash
# Run by module-updates.yml. For each module pinned in $PINS whose latest GitHub release isn't the pinned one, pushes a
# module-update/<repo> branch off $BASE_BRANCH that pins the new release, and opens or updates a PR for it.
# Everything read from the module's repo (release notes, file names) is untrusted and only ever goes in code blocks
set -euo pipefail

INSPECTOR=${INSPECTOR:-$RUNNER_TEMP/inspector/ModuleInspector.dll}

if [ "${1:-}" != one ]; then
  pins=$(jq -c '.[]' "$PINS")
  failed=0
  while read -r pin; do
    [ -n "$pin" ] || continue
    # Each module in its own shell, so one failing doesn't stop the rest and set -e still applies inside it
    if ! bash "$0" one "$pin"; then
      echo "::error::Checking $(jq -r .repo <<<"$pin") for updates failed"
      failed=1
    fi
  done <<<"$pins"
  exit "$failed"
fi

repo=$(jq -r .repo <<<"$2")
pinned_tag=$(jq -r .tag <<<"$2")
pinned_sha=$(jq -r .sha256 <<<"$2")

latest=$(gh api "repos/$repo/releases/latest" --jq .tag_name)
if [ "$latest" = "$pinned_tag" ]; then
  echo "$repo is up to date at $pinned_tag"
  exit 0
fi
echo "$repo has $latest, pinned to $pinned_tag"

branch="module-update/$(printf '%s' "$repo" | tr -c 'A-Za-z0-9._-' '-')"
marker="<!-- module-tag: $latest -->"
prs=$(gh pr list --base "$BASE_BRANCH" --head "$branch" --state all --limit 50 --json number,state,body)
if jq -e --arg m "$marker" 'any(.[]; .state == "OPEN" and (.body | contains($m)))' <<<"$prs" >/dev/null; then
  echo "There's already a PR for $latest"
  exit 0
fi
if jq -e --arg m "$marker" 'any(.[]; .state == "CLOSED" and (.body | contains($m)))' <<<"$prs" >/dev/null; then
  echo "The PR for $latest was closed without merging, so it isn't opened again"
  exit 0
fi
pr=$(jq -r 'map(select(.state == "OPEN")) | .[0].number // empty' <<<"$prs")

work=$(mktemp -d)
uri() { jq -rn --arg v "$1" '$v | @uri'; }
# The zip VRCFaceTracking installs: the release's first asset whose URL ends in .zip
zip_asset='[.assets[] | select(.browser_download_url | ascii_downcase | endswith(".zip"))][0]'
# Stops anything from closing the four-backtick code blocks the untrusted text goes in
fenced() { sed -E 's/`{4,}/```/g' | head -c "$1"; }

# Downloads release $1 into $work/$2.zip and its module.json into $work/$2-module.json, and extracts the zip into $work/$2
fetch() {
  gh api "repos/$repo/releases/tags/$(uri "$1")" >"$work/$2-release.json" || return 1
  local url
  url=$(jq -r "$zip_asset.browser_download_url // empty" "$work/$2-release.json")
  [ -n "$url" ] || return 1
  curl -fsSL "$url" -o "$work/$2.zip" || return 1
  curl -fsSL "https://raw.githubusercontent.com/$repo/$1/module.json" -o "$work/$2-module.json" || return 1
  sed -i '1s/^\xEF\xBB\xBF//' "$work/$2-module.json"
  jq -e . "$work/$2-module.json" >/dev/null || return 1
  mkdir "$work/$2"
  unzip -q "$work/$2.zip" -d "$work/$2"
}

if ! fetch "$latest" new; then
  echo "::warning::$repo $latest has no zip or no valid module.json at its tag, so VRCFaceTracking can't install it"
  exit 0
fi
new_sha=$(sha256sum "$work/new.zip" | cut -d' ' -f1)
digest=$(jq -r "$zip_asset.digest // empty" "$work/new-release.json")
if [ -n "$digest" ] && [ "$digest" != "sha256:$new_sha" ]; then
  echo "::error::The $repo $latest zip downloaded with SHA256 $new_sha, but GitHub lists $digest"
  exit 1
fi
dotnet "$INSPECTOR" "$work/new" >"$work/new.txt"

warnings=()
: >"$work/old.txt"
if fetch "$pinned_tag" old; then
  if [ "$(sha256sum "$work/old.zip" | cut -d' ' -f1)" != "$pinned_sha" ]; then
    warnings+=("The pinned $pinned_tag zip no longer matches its pinned SHA256, so it was replaced after it was reviewed.")
  fi
  dotnet "$INSPECTOR" "$work/old" >"$work/old.txt"
  old_id=$(jq -r '.ModuleId // empty' "$work/old-module.json")
  new_id=$(jq -r '.ModuleId // empty' "$work/new-module.json")
  if [ "$old_id" != "$new_id" ]; then
    warnings+=("module.json's ModuleId changed, so VRCFaceTracking treats it as a different module and won't update installed copies.")
  fi
else
  warnings+=("Couldn't download the pinned $pinned_tag, so there's nothing to compare the new version with.")
fi
dll=$(jq -r '.DllFileName // empty' "$work/new-module.json")
if [ -z "$dll" ] || [ ! -f "$work/new/$dll" ]; then
  warnings+=("module.json's DllFileName isn't a file at the root of the zip, so the module won't load.")
fi

name=$(jq -r '.ModuleName // empty' "$work/new-module.json" | tr -d '\r\n@' | head -c 60)
name=${name:-$repo}
changes=$(diff --unchanged-line-format= --old-line-format='- %L' --new-line-format='+ %L' "$work/old.txt" "$work/new.txt" || true)
notable=$(grep '^! ' "$work/new.txt" | sed 's/^! //' || true)

{
  echo "$marker"
  echo "Pins [$repo](https://github.com/$repo) to \`$latest\` (was \`$pinned_tag\`): [release](https://github.com/$repo/releases/tag/$(uri "$latest")), [code changes](https://github.com/$repo/compare/$(uri "$pinned_tag")...$(uri "$latest"))."
  echo
  echo "Merging releases it: VRCFaceTracking then lists the new version and updates installed copies of the module when it starts. Close the PR to skip this version."
  echo
  echo "New zip SHA256: \`$new_sha\`"
  for warning in "${warnings[@]}"; do
    echo
    echo "> [!WARNING]"
    echo "> $warning"
  done
  echo
  echo "### What changed in the files"
  echo
  echo "Every file in the zip, and for .NET DLLs the outside types and methods they use, compared with \`$pinned_tag\`. \`!\` marks networking, other processes, the registry, the file system, native code and loading code by name."
  echo
  echo '````diff'
  printf '%s\n' "${changes:-No changes}" | fenced 20000
  echo '````'
  echo
  echo "### Worth a look in \`$latest\`"
  echo
  echo '````'
  printf '%s\n' "${notable:-Nothing}" | fenced 10000
  echo '````'
  echo
  echo "### Release notes"
  echo
  echo '````'
  jq -r '.body // "" | if . == "" then "None" else . end' "$work/new-release.json" | tr -d '\r' | fenced 6000
  echo '````'
  echo
  echo "<details><summary>Everything in $latest</summary>"
  echo
  echo '````'
  fenced 20000 <"$work/new.txt"
  echo '````'
  echo
  echo "</details>"
} >"$work/body.md"

git switch -q -C "$branch" "origin/$BASE_BRANCH"
jq --arg repo "$repo" --arg tag "$latest" --arg sha "$new_sha" \
  'map(if .repo == $repo then .tag = $tag | .sha256 = $sha else . end)' "$PINS" >"$work/pins.json"
cp "$work/pins.json" "$PINS"
git add "$PINS"
git -c user.name='github-actions[bot]' -c user.email='41898282+github-actions[bot]@users.noreply.github.com' \
  commit -q -m "Pin $repo to $latest"
git push -q --force origin "$branch"

title="Update $name to $latest"
if [ -n "$pr" ]; then
  gh pr edit "$pr" --title "$title" --body-file "$work/body.md"
  echo "Updated PR $pr"
else
  gh pr create --base "$BASE_BRANCH" --head "$branch" --title "$title" --body-file "$work/body.md"
fi
