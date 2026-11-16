# Music

> **Windows preview withdrawn:** Microsoft Defender blocked `Music.exe` from `v0.1.0-preview.1` with a Severe / Trojan alert. Do not run it, restore it from quarantine, or add an antivirus exception. The detection is under investigation; it has not been established to be a false positive. Binary publishing is paused. See [the incident status](docs/ANTIVIRUS-INVESTIGATION.md).

A native Windows music app with an Apple Music–inspired interface and a Spotify account connection. The desktop UI is C# and WinUI 3: no Electron, embedded website, WebView, or local web server powers the interface.

The visual direction combines a dark desktop layout, translucent surfaces, spacious artwork, a persistent player, and an expanded lyrics screen with artwork-colored atmosphere.

## What this version does

- Browse demo albums, artists, playlists, and tracks immediately, without credentials.
- Search music, open artist and album details, and browse a personal library.
- Show a playback bar, expanded player, queue, playback position, volume, shuffle, and repeat controls.
- Show large time-synced lyrics, highlight the current line, seek from a line, and return to the current line after manual scrolling. The visual treatment uses dimming, scaling, and edge fades; inactive lines do not yet have a true Gaussian blur effect.
- Import a local `.lrc` lyrics file and save it for the current track. Demo lyrics are original sample text, not lyrics fetched from Spotify.
- Connect a personal Spotify account through OAuth PKCE in the system browser; no client secret or Spotify password is entered in Music.
- Use Spotify's public API for the available catalog, library, playlist, device, and playback-control operations.
- Open a native Jam handoff dialog, then open the official Spotify app to start or join a Jam there. This is a handoff, not independent Jam hosting or synchronization inside Music.

**Playback boundary:** demo playback is a simulated timeline with no audio. Spotify mode controls an existing Spotify Connect device; it does not decrypt, download, or stream Spotify audio inside Music. An active eligible Spotify playback device and Spotify Premium are required for playback-control operations. The official Spotify app may need to be open on the selected device. Independent in-app Spotify streaming is not implemented.

The public Spotify API does not reproduce every official Spotify feature. Spotify's exact Home feed, DJ, offline downloads, music video playback, historical listening statistics, and synced lyrics are not supplied by this app. Availability of individual API features depends on the Spotify developer app's access mode and the connected account.

## Download for Windows

Binary downloads are currently withdrawn. Do not run a previously downloaded `Music-win-x64.zip` from `v0.1.0-preview.1`. Future cleared previews would be published through this repository’s [Releases](https://github.com/Danvuu/danvu/releases). The .NET and Windows App SDK runtimes are included; you do not need to install the .NET SDK to run this distribution. Release downloads also include a SHA-256 checksum file.

Read the [preview release notes](docs/RELEASES.md) for its current capabilities and remaining Windows/Spotify validation. Keep all files next to the executable when moving the extracted app.

## Build and run from source on Windows

Requirements for building:

- Windows 10 version 2004 (build 19041) or newer, or Windows 11; x64.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). `global.json` permits later stable .NET 10 feature bands.
- Windows SDK/build tools compatible with WinUI 3. Visual Studio 2026 with Windows application development support is the recommended full development installation. GitHub's `windows-latest` runner provides Windows build tools for the included CI workflow.

From an existing checkout, open PowerShell in the repository root:

```powershell
.\scripts\run.ps1
```

This restores and publishes a Debug build, then opens the native `Music.exe`. It uses the same self-contained deployment layout as the Release distribution. It does not start a website.

For a Release distribution:

```powershell
.\scripts\build.ps1
```

Outputs:

```text
artifacts/publish/win-x64/Music.exe
artifacts/Music-win-x64.zip
```

The zip contains the executable **and its required native/runtime files**. Extract the entire zip before launching `Music.exe`; do not copy only the executable. A single-file executable is deliberately not used for the WinUI deployment. The .NET and Windows App SDK runtimes are included by the publish settings.

Published artifacts are unsigned. This repository does not yet produce a signed MSIX/MSI installer or a Microsoft Store release.

### Optional per-user installation

Inside an extracted distribution, run:

```powershell
.\install.ps1
```

This copies the distribution to `%LOCALAPPDATA%\Programs\Music` and adds a Start menu shortcut. It does not require administrator access. Close Music before installing an update from a newly extracted distribution. To remove the application, run `uninstall.ps1` from the installed folder. The uninstaller retains personal app settings/account data.

The scripts respect your machine's PowerShell execution policy; they do not modify it.

## Connect your Spotify account

1. Create a developer app in the [Spotify developer dashboard](https://developer.spotify.com/dashboard), subject to Spotify's current account and development-mode requirements.
2. Register this exact redirect URI:

   ```text
   http://127.0.0.1:5543/callback
   ```

   Keep the path and absence of a trailing slash exactly as shown. Spotify supports an HTTP loopback IP redirect for this local desktop authorization flow. The callback listener exists only during login; it does not serve the app interface.
3. Copy the app's **Client ID** into Music's Spotify settings and select the connect action. A Client ID is public application identification, not a secret.
4. Complete consent on Spotify's page in your system browser. If your developer app requires an allowlist, add the account there first.
5. Open Spotify on an eligible device, start or select playback there, and choose an available device in Music.

Never put a client secret, account password, or token in source files. Authentication uses a fresh PKCE verifier and validates OAuth state before exchanging the authorization code. Tokens are stored locally using Windows account protection. Account authorization does not guarantee every endpoint is available to your developer app; Music reports Spotify errors rather than bypassing access restrictions.

Spotify mode does not fetch lyrics. Import an LRC file for lyrics you are entitled to use. LRC timestamps must match the recording; clicking a line seeks the selected Spotify device. Imports must be no larger than 1 MB and contain between 1 and 2,000 timestamped lines. Imported files are retained per track in `%LOCALAPPDATA%\Music\Lyrics`, using a hash of the track ID as the filename, and are loaded when that track is opened again. This persistence is implemented in source; the native file-picker and restart behavior still require Windows testing.

### Spotify Jam

The **Jam** button opens a native explanation with an **Open Spotify** action. The action launches the official Spotify application, using the current Spotify track when available. Start or join a Jam in Spotify, then use Music to control the available Spotify Connect playback device.

Spotify's public API does not expose creating, joining, inviting participants to, or synchronizing a Jam session. It also does not provide Jam session/member information for a participant list in Music. Music does not implement these session operations. The Spotify app must be installed and a working `spotify:` URI handler must be available for this handoff.

## Tests and validation

The portable music/authentication/lyrics core can be tested on Linux, macOS, or Windows:

```sh
dotnet test tests/Danvu.Core.Tests/Danvu.Core.Tests.csproj --configuration Release
```

Build the WinUI project on Windows, using `scripts/build.ps1`. Building the whole solution on Linux does not validate the native UI or Windows packaging.

The [Windows workflow](.github/workflows/windows.yml) runs core tests and publishes a self-contained Windows x64 zip. It runs on pull requests, pushes to `main`, version tags beginning with `v`, and manual dispatch. The `Music-Windows-x64` binary artifact is currently gated by the same release-enablement variable while the antivirus report is investigated. When the repository variable `MUSIC_RELEASES_ENABLED` is explicitly set to `true`, a successful push of a newly created version tag also publishes `Music-win-x64.zip` and its SHA-256 checksum as release downloads. All automated releases are marked as prereleases. Only the tag-gated release job receives permission to create releases; build jobs keep read-only repository access. An existing release is not overwritten by a rerun.

See [the Windows smoke test](docs/WINDOWS-SMOKE-TEST.md) for the checks required on an actual Windows desktop. These include layout, animation, OAuth browser return, native file import, and Spotify device behavior.

[Recorded implementation validation](docs/VALIDATION.md) describes the original cloud checks. Subsequent Windows build evidence is available in the corresponding GitHub Actions run; a successful native publish does not replace the remaining desktop and live-account checks.

## Project layout

```text
src/
  Danvu.Core/         Portable music models, demo catalog, Spotify API/OAuth, LRC parsing
  Danvu.Windows/      Native WinUI desktop application, visual effects, local account storage
tests/
  Danvu.Core.Tests/   Core behavior and HTTP contract tests
scripts/             Windows build, launch, install, and uninstall commands
.github/workflows/   Test and Windows artifact build
```

Use the existing checkout for development. Cloud tasks already have isolated environments; do not create an extra Git worktree unless explicitly requested. On a Linux cloud machine, run core tests there and use the Windows workflow or a Windows development machine for native build and desktop validation.

Music is an independent application and is not affiliated with Apple or Spotify. Catalog and artwork access remain subject to Spotify's API terms.
