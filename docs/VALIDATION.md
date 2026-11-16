# Validation evidence

Evidence recorded during implementation on the Linux cloud machine, November 16, 2026.

| Check | Result | Scope |
| --- | --- | --- |
| Release core test suite | 67 passed, 0 failed, 0 skipped | Portable catalog, playback-state, lyrics, OAuth, token, and Spotify HTTP logic exercised by the existing tests. No live Spotify account involved. |
| All Windows managed source compiled against WinUI/Windows references | Passed, 0 warnings and 0 errors | A separate managed compilation checked the C# source and referenced API signatures. It excluded native Windows SDK build targets and is not a native application build. |
| PowerShell build/run/install/uninstall scripts | All 4 parsed successfully with PowerShell 7.6 | Syntax validation; installation and native launching were not executed on Windows. The build script also correctly rejects a Linux host. |
| Workflow YAML and solution | Parsed/listed successfully | Both CI jobs and all 3 solution projects were present. This does not mean a GitHub Actions job ran. |
| Project/manifest XML, 2 embedded XAML templates, and 17 PNG artwork assets | Parsed/decoded successfully | Syntax and asset integrity checks only; not WinUI rendering, XAML runtime loading, or animation validation. |
| Actual WinUI self-contained build attempted on Linux | Failed during Windows App SDK manifest generation | The package's MSIX glob uses `tools/Msix` and `.Msix`, while the cached payload uses `tools/MSIX` and `.msix`. Linux case-sensitive matching leaves the extraction input empty, so the runtime `AppxManifest.xml` is absent. Later manifest merging also uses Windows SDK `mt.exe`. |
| Windows executable and distribution | Not built on Windows | No working `Music.exe`, installer execution, or successful Windows CI run has been verified from this workspace. |
| Native desktop behavior | Unrun | Window layout, animations, reduced motion, file picker, media controls, device selection, and startup on a clean Windows computer need direct observation. |
| Live Spotify login/playback/Jam handoff | Unrun | Requires a configured developer Client ID, an authorized account, an eligible Spotify Connect device, and the installed Spotify app for Jam handoff. |

The Linux MSIX failure is explained by the package's Windows-host assumptions. The native project is unpackaged (`WindowsPackageType=None`); it does not need a fabricated application `AppxManifest.xml` to hide this error. The SDK extracts its own runtime manifest, generates registration information, and merges that with the project's `app.manifest` using `mt.exe` on Windows.

A targeted MSBuild evaluation confirmed the correct x64 architecture and runtime package path, with an empty MSIX input list on Linux. The package target searches `tools/Msix/*.Msix`, while the package files are under `tools/MSIX/*.msix`; Windows matches these paths without case sensitivity. The runtime package itself contains `AppxManifest.xml`. The self-contained configuration also correctly enables registration-free WinRT initialization and disables the framework-dependent bootstrap initializer.

The code-only `Application` startup does not use an `App.xaml` file. WinUI's markup targets run only when XAML application/page items exist; the app creates its resources and window from C#. Managed compilation validates those source references, while runtime initialization still needs the Windows test below.

## Required next verification

1. Run the [Windows workflow](../.github/workflows/windows.yml), or `scripts/build.ps1` on a Windows development machine, and require a successful native publish.
2. Extract the complete `Music-win-x64.zip` and perform the [Windows smoke test](WINDOWS-SMOKE-TEST.md), including a clean machine without separately installed .NET/Windows App SDK runtimes.
3. Configure a Spotify developer Client ID in the native app's Settings and test real browser authorization, device playback, denial/cancellation, and disconnection. Check Jam's official-app handoff separately; Music does not host or inspect a Jam session.

Source inspection confirms per-track LRC save/load paths under `%LOCALAPPDATA%\Music\Lyrics` and the selection of the active device in the device picker. Neither has yet been exercised in a running native window. No claim of completed Windows runtime validation or Spotify feature parity is made.
