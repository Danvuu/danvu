# Music for Windows — preview (withdrawn)

**Do not run v0.1.0-preview.1.** Microsoft Defender reported a Severe / Trojan detection for the installed `Music.exe`. This release is withdrawn pending investigation. Do not restore it from quarantine or add an exclusion. No false-positive conclusion has been reached. See [the investigation status](ANTIVIRUS-INVESTIGATION.md).

Download **Music-win-x64.zip**, extract the entire archive, and open **Music.exe**. Keep its accompanying files in the extracted folder. This native Windows x64 distribution includes the .NET and Windows App SDK runtimes; the .NET SDK is needed only to build the source code.

Requires Windows 10 build 19041 or newer, or Windows 11, on an x64 computer. The application is unsigned. An optional `install.ps1` helper installs it for the current user and adds a Start menu shortcut.

This preview includes the desktop music browser, artist and album views, search, queue, playback controls, a large synced-lyrics screen, local LRC imports, and a Spotify account connection.

- Demo playback is a silent simulated timeline with original sample lyrics.
- Spotify mode controls an existing eligible Spotify Connect device. Playback control requires Spotify Premium; independent in-app Spotify audio streaming and offline downloads are not implemented.
- Spotify login uses your own developer Client ID and official browser authorization. Never enter a Spotify password or client secret in Music.
- Spotify's public API does not supply synced lyrics; import an LRC file for lyrics you have permission to use.
- Jam opens the official Spotify app to start or join there. Music does not independently host, synchronize, or inspect Jam sessions or participants.

The release workflow requires successful core tests and a native Windows publish before uploading this distribution. A successful build does not validate desktop rendering, installation, animation performance, or live Spotify login/playback. Those manual Windows and real-account checks remain outstanding.

**Music-win-x64.zip.sha256** contains the SHA-256 digest of the downloadable ZIP. Compare it with the output of this PowerShell command after downloading:

```powershell
Get-FileHash .\Music-win-x64.zip -Algorithm SHA256
```

See the repository README for setup details and `docs/WINDOWS-SMOKE-TEST.md` for the remaining desktop checks.
