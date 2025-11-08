#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/release-debug.sh <version> [release-notes-file]

Builds the plugin in Debug configuration, zips the output, uploads it to a GitHub
release, and appends the release metadata to manifest.json.

Arguments:
  version              Version string recorded inside manifest.json (e.g. 0.2.0-debug.1)
  release-notes-file   Optional file containing release notes; defaults to TrackRules.Plugin/meta.json changelog.

Environment:
  GITHUB_TOKEN must be configured for the GitHub CLI to create/upload releases.
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

if [[ $# -lt 1 || $# -gt 2 ]]; then
  usage >&2
  exit 1
fi

for cmd in dotnet gh jq zip md5sum; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "error: Required command '$cmd' not found in PATH." >&2
    exit 1
  fi
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

VERSION="$1"
MANIFEST_VERSION="${VERSION//-debug/}"

if [[ "$MANIFEST_VERSION" != "$VERSION" ]]; then
  echo "Manifest version sanitized: $VERSION -> $MANIFEST_VERSION"
fi
NOTES_FILE="${2:-}"
META_JSON="$REPO_ROOT/TrackRules.Plugin/meta.json"
MANIFEST_JSON="$REPO_ROOT/manifest.json"

if [[ ! -f "$META_JSON" ]]; then
  echo "error: Missing $META_JSON" >&2
  exit 1
fi

if [[ ! -f "$MANIFEST_JSON" ]]; then
  echo "error: Missing $MANIFEST_JSON" >&2
  exit 1
fi

if [[ -n "$NOTES_FILE" ]]; then
  if [[ ! -f "$NOTES_FILE" ]]; then
    echo "error: release notes file '$NOTES_FILE' not found." >&2
    exit 1
  fi
  RELEASE_NOTES="$(cat "$NOTES_FILE")"
else
  RELEASE_NOTES="$(jq -r '.changelog // ""' "$META_JSON")"
fi

if [[ -z "$RELEASE_NOTES" ]]; then
  RELEASE_NOTES="_No release notes provided._"
fi

PLUGIN_GUID="$(jq -r '.guid' "$META_JSON")"
TARGET_ABI="$(jq -r '.targetAbi' "$META_JSON")"
PLUGIN_NAME="$(jq -r '.name' "$META_JSON")"

if [[ -z "$PLUGIN_GUID" || "$PLUGIN_GUID" == "null" ]]; then
  echo "error: guid missing from $META_JSON" >&2
  exit 1
fi

if ! jq --arg guid "$PLUGIN_GUID" 'map(select(.guid == $guid)) | length > 0' "$MANIFEST_JSON" | grep -q true; then
  echo "error: manifest.json does not contain plugin guid $PLUGIN_GUID" >&2
  exit 1
fi

ARTIFACT_DIR="$REPO_ROOT/artifacts"
PUBLISH_DIR="$ARTIFACT_DIR/publish"
ARTIFACT_NAME="TrackRules.Debug.${VERSION}.zip"
ARTIFACT_PATH="$ARTIFACT_DIR/$ARTIFACT_NAME"

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR" "$ARTIFACT_DIR"

echo "Building TrackRules.Plugin in Debug configuration..."
dotnet publish "$REPO_ROOT/TrackRules.Plugin/TrackRules.Plugin.csproj" -c Debug -o "$PUBLISH_DIR" >/dev/null

echo "Packing artifact $ARTIFACT_NAME..."
rm -f "$ARTIFACT_PATH"
(cd "$PUBLISH_DIR" && zip -qr "$ARTIFACT_PATH" .)

CHECKSUM="$(md5sum "$ARTIFACT_PATH" | awk '{print $1}')"

detect_repo_slug() {
  local slug remote
  if slug="$(gh repo view --json nameWithOwner -q '.nameWithOwner' 2>/dev/null)"; then
    printf '%s\n' "$slug"
    return 0
  fi

  remote="$(git config --get remote.origin.url || true)"
  if [[ -n "$remote" ]]; then
    if [[ "$remote" =~ github.com[:/](.+)\.git$ ]]; then
      printf '%s\n' "${BASH_REMATCH[1]}"
      return 0
    fi
  fi
  return 1
}

REPO_SLUG="$(detect_repo_slug)" || {
  echo "error: Unable to determine GitHub repository slug." >&2
  exit 1
}

TAG="v${VERSION}-debug"
TITLE="${PLUGIN_NAME} Debug ${VERSION}"
DOWNLOAD_URL="https://github.com/${REPO_SLUG}/releases/download/${TAG}/${ARTIFACT_NAME}"

echo "Creating or updating GitHub release ${TAG}..."
if gh release view "$TAG" >/dev/null 2>&1; then
  gh release upload "$TAG" "$ARTIFACT_PATH" --clobber >/dev/null
  gh release edit "$TAG" --title "$TITLE" --notes "$RELEASE_NOTES" --prerelease >/dev/null
else
  gh release create "$TAG" "$ARTIFACT_PATH" --title "$TITLE" --notes "$RELEASE_NOTES" --prerelease >/dev/null
fi

TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

echo "Updating manifest.json..."
jq --arg guid "$PLUGIN_GUID" \
   --arg version "$MANIFEST_VERSION" \
   --arg changelog "$RELEASE_NOTES" \
   --arg targetAbi "$TARGET_ABI" \
   --arg sourceUrl "$DOWNLOAD_URL" \
   --arg checksum "$CHECKSUM" \
   --arg timestamp "$TIMESTAMP" \
   '
   map(
     if .guid == $guid then
       .versions = (
         [{
           version: $version,
           changelog: $changelog,
           targetAbi: $targetAbi,
           sourceUrl: $sourceUrl,
           checksum: $checksum,
           timestamp: $timestamp
         }] + (.versions // [] | map(select(.version != $version)))
       )
     else
       .
     end
   )
   ' "$MANIFEST_JSON" > "$MANIFEST_JSON.tmp"

mv "$MANIFEST_JSON.tmp" "$MANIFEST_JSON"

echo "All done!"
echo "  Artifact: $ARTIFACT_PATH"
echo "  Release : https://github.com/${REPO_SLUG}/releases/tag/${TAG}"
echo "  Manifest updated with version $VERSION"
