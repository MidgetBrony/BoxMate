# BoxMate

BoxMate is a Windows and Linux/Steam Deck mod manager for BOXROOM. The official [`MidgetBrony/BoxMate-Mods`](https://github.com/MidgetBrony/BoxMate-Mods) catalogue is built in, so generally available mods appear on first launch without setup. Add `OWNER/REPOSITORY` or a repository link for anything outside the catalogue, choose the BOXROOM folder, refresh, and install. BoxMate finds `manifest.json` at the repository root on `main` or `master`; required repositories are discovered and installed automatically. Existing raw manifest links remain supported for compatibility.

When a manifest requires MelonLoader and it is missing, BoxMate downloads the latest [official x64 archive from LavaGang](https://github.com/LavaGang/MelonLoader/releases/latest), verifies GitHub's SHA-256 digest, and installs it before the mod. This is also the correct archive for the Windows build of BOXROOM running through Wine/Proton. The setup panel has a separate **Install / update MelonLoader** button. BOXROOM must be closed during this operation.

## Linux and Steam Deck

Download `BoxMate-linux-x64.tar.gz`, extract it, and run `BoxMate`. If necessary, make it executable first:

```bash
chmod +x BoxMate
./BoxMate
```

Choose BOXROOM's installation directory—the folder containing `BOXROOM.exe`. BoxMate installs MelonLoader and mods into that Windows game directory even though BoxMate itself runs natively on Linux.

MelonLoader's proxy DLL must be enabled in BOXROOM's Steam launch options. BoxMate displays a Linux setup card, copies the required value after installing MelonLoader, and writes `BoxMate-Linux-Setup.txt` beside `BOXROOM.exe`:

```text
WINEDLLOVERRIDES="version=n,b" %command%
```

Paste it into **Steam → BOXROOM → Properties → General → Launch Options**, then start BOXROOM once and allow MelonLoader's first-run setup to finish. BoxMate does not rewrite Steam's account configuration automatically.

## Optional GitHub sign-in

Anonymous use works without an account and is backed by BoxMate's persistent release cache. An OAuth-enabled build also offers **Sign in with GitHub**, raising GitHub release lookups from 60 per IP per hour to 5,000 per signed-in user per hour. The browser/device flow never asks users to paste a personal access token. On Windows the token is encrypted for the current user with DPAPI. On Linux it is stored in BoxMate's per-user data directory with user-only file permissions. **Sign out of GitHub** removes it.

Before distributing an OAuth-enabled build, register a GitHub OAuth App, enable its Device Flow setting, and publish with its public client ID:

```powershell
dotnet publish BoxMate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:GitHubClientId=YOUR_PUBLIC_CLIENT_ID
```

The client ID is public application identity, not a secret. Never embed the OAuth client secret in BoxMate.

## Authoring a manifest

Copy `manifest.example.json` to the root of the mod repository and edit it. Users add the repository itself, for example:

```text
OWNER/REPOSITORY
```

`repository` must be the public GitHub repository. `release.asset` is the filename in its latest GitHub release and may use `*` or `?` wildcards. The pattern must match exactly one asset. Versions come from the latest release tag, so the manifest does not need updating for every release.

For future releases, prefer stable asset names such as `Boxroom-RadioStreams.zip`. They will allow BoxMate to progressively avoid API discovery through GitHub's `releases/latest/download` route.

Every download must have a SHA-256 value. BoxMate first uses the digest published by GitHub. If that release asset has no digest, publish a text asset such as `SHA256SUMS.txt` and set `release.checksumAsset`. Accepted checksum lines are:

```text
0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  Boxroom-Plus.zip
```

Dependencies use raw manifest links:

```json
{
  "manifest": "https://raw.githubusercontent.com/OWNER/REQUIRED-MOD/main/manifest.json",
  "minimumVersion": "1.0.0",
  "required": true
}
```

## Release ZIP layout

ZIP paths are relative to the selected BOXROOM folder and must start with one of these folders:

```text
Mods/
Plugins/
UserLibs/
UserData/
```

Example:

```text
Boxroom-Plus.zip
├── Mods/BoxroomPlus.dll
└── UserData/BoxroomPlus/defaults.json
```

For a ZIP whose contents are already rooted at `Mods`, `Plugins`, `UserLibs`, or `UserData`, omit `release.destination`. If a ZIP contains a root-level DLL, set `release.destination` to `Mods` and BoxMate will prepend it to every ZIP entry. For a single non-ZIP release asset, use a full destination such as `Mods/MyMod.dll`.

BoxMate rejects absolute paths, traversal outside BOXROOM, unexpected root folders, invalid hashes, dependency cycles, duplicate package IDs, and unsupported release providers. Existing files are backed up, and a partially failed installation is rolled back.

Installed state is recorded in `UserData/BoxMate/installed.json`.

Installed mod cards include an **Uninstall** action. BoxMate removes only the files recorded for that package, preserves files shared with another installed package, and refuses to remove a dependency while another installed mod still requires it. MelonLoader is managed separately and is never removed as part of uninstalling a mod.

## BoxMate updates

BoxMate checks its own latest GitHub release at startup. When a newer version is available, **Update BoxMate** appears in the header. The updater downloads the correct complete Windows or Linux runtime archive, verifies its SHA-256 checksum, stages the replacement outside the application folder, restarts through the staged copy, and relaunches the updated installation.

## Collection manifests

A collection is a catalogue and has no downloadable release of its own. BoxMate expands each repository into a normal mod card and still resolves the mod's required dependencies:

```json
{
  "schemaVersion": 1,
  "type": "collection",
  "id": "my-mod-collection",
  "name": "My Mod Collection",
  "author": "Author",
  "description": "A curated set of BOXROOM mods.",
  "mods": [
    { "repository": "OWNER/FIRST-MOD", "recommended": true },
    { "repository": "OWNER/SECOND-MOD", "recommended": false }
  ],
  "deprecatedMods": [
    {
      "id": "old-mod-id",
      "name": "[Deprecated] Old Mod",
      "repository": "OWNER/OLD-MOD",
      "replacement": "OWNER/NEW-MOD",
      "reason": "The replacement now provides this feature."
    }
  ]
}
```

Deprecated mods are shown only when their ID remains in `installed.json`. Their cards are red, explain the replacement, and provide only **Uninstall**. They are never offered as new installations.

## Build

```powershell
dotnet restore
dotnet build BoxMate.csproj -c Release --no-restore
```

To make self-contained builds that do not require .NET to be installed:

```powershell
dotnet publish BoxMate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish BoxMate.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```
