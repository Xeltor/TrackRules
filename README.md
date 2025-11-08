# TrackRules

## Debug release helper

Use `scripts/release-debug.sh` to package the plugin, push a debug release to GitHub, and append the new version entry to `manifest.json`.

```bash
scripts/release-debug.sh <version> [release-notes-file]
```

- Builds `TrackRules.Plugin` in Debug mode and zips the published output into `artifacts/TrackRules.Debug.<version>.zip`.
- Creates or updates the GitHub release tagged `v<version>-debug`, uploads the artifact, and sets the release notes (defaults to `TrackRules.Plugin/meta.json`'s `changelog`).
- Updates `manifest.json` with the download URL, SHA-256 checksum (required by Jellyfin manifest consumers), target ABI, and timestamp for the newly published version.

Prereqs: `dotnet`, `gh`, `jq`, `zip`, and a valid `GITHUB_TOKEN` configured for the GitHub CLI.
