# Music v0.1.1-preview.1 for Windows

This preview fixes the observed native startup crash while loading WinUI resources. The app now initializes resources through the standard compiled `App.xaml`, including generated XAML resources and metadata. This is an application startup fix; it does not establish that the earlier antivirus detection was a false positive or promise that a future warning will disappear.

**v0.1.0-preview.1 remains withdrawn.** Microsoft Defender reported a Severe/Trojan alert on the user's Windows computer. That alert remains unresolved; see [the investigation status](ANTIVIRUS-INVESTIGATION.md).

Before considering this new preview, keep Microsoft Defender and real-time protection enabled, remove any old Music allow entry from **Windows Security > Virus & threat protection > Allowed threats**, and keep the old version removed or quarantined. If this preview is blocked, do not allow it, restore it, or add an exclusion. Report the exact Threat name and affected file so the detection can be investigated.

Publication requires successful core tests and a fresh normal-remediation Defender scan with verified real-time protection, followed by the actual native Home, album navigation, silent playback/pause, lyrics seek, and close checks. The final installer also requires a fresh normal Defender scan, an isolated silent installation whose full payload hashes match the tested bundle, and successful native uninstallation. Any failure blocks the binary upload and release. The release includes a dated link to its passing tag run; its diagnostic artifact records scanner versions, candidate hashes, screenshots, and check results. A hosted scan does not clear the earlier PC alert.

The simple installation download is **Music-Setup.exe**. Its native setup wizard installs Music for the current user without requiring administrator access, includes the desktop runtimes and artwork, creates a Start menu shortcut, and offers an optional desktop shortcut. It provides a native uninstaller and does not launch Music automatically. Requires Windows 10 build 19041 or newer, or Windows 11, on an x64 computer. No SDK or manual PowerShell setup is required. A portable ZIP remains available for advanced use.

- Demo playback is a silent simulated timeline with original sample lyrics.
- Spotify mode controls an existing eligible Spotify Connect device; playback control requires Spotify Premium. Independent Spotify streaming and offline downloads are not implemented.
- Spotify login uses your own developer Client ID and official browser authorization. Never enter a Spotify password or client secret in Music.
- Synced lyrics come from the original demo document or a local LRC file you have permission to use; Spotify's public API does not supply them.
- Jam opens the official Spotify app. Music does not independently host, synchronize, or inspect Jam sessions or participants.

Live Spotify login/playback, the interactive installer wizard on a user machine, and animation performance across hardware remain separate checks. Hosted silent installation and automated demo UI checks do not validate those capabilities.

**Publisher details: Danvu. This build remains unsigned because no code-signing certificate has been supplied.** Company/publisher metadata is not a Windows-verified publisher signature. Signing support is prepared for a future valid, trusted code-signing certificate with HTTPS timestamping; no certificate or trust root is created or imported for this preview.

Compare **Music-Setup.exe.sha256** with the result of:

```powershell
Get-FileHash .\Music-Setup.exe -Algorithm SHA256
```

See the README and `docs/WINDOWS-SMOKE-TEST.md` for setup and remaining manual checks.
