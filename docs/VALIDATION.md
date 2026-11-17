# Validation evidence

Status recorded November 17, 2026. The original Windows preview remains withdrawn after the user’s Severe / Trojan alert. No replacement has been declared cleared or published. See [the antivirus investigation](ANTIVIRUS-INVESTIGATION.md) for the exact artifact hashes and the limits of the original hosted scan.

## Current evidence

| Check | Result | Scope |
| --- | --- | --- |
| Release core test suite | 67 passed, 0 failed, 0 skipped | Portable catalog, playback state, lyrics, OAuth, token storage abstraction, and Spotify HTTP behavior. No live Spotify account was involved. |
| Current Windows managed source against WinUI/Windows references | Passed, 0 warnings and 0 errors | Checks C# and referenced API signatures. The temporary compilation supplies the generated `InitializeComponent` signature and excludes native XAML/build targets; it does not validate XBF, PRI resources, rendering, or runtime startup. |
| Native self-contained Windows build | [Build run 43984302179 passed](https://github.com/Danvuu/danvu/actions/runs/43984302179) | Demonstrates native compilation/publishing for that run’s source. It predates the current visual redesign and does not establish a desktop interaction pass. |
| Compiled application-resource startup | Native window observed after commit `34dc00c`; [run 43976683200](https://github.com/Danvuu/danvu/actions/runs/43976683200) captured the window | `App.xaml` and its generated resources/metadata replaced the earlier code-only resource initialization. The run’s later harness failure does not invalidate the observed window, but it was not a completed functional pass. |
| Latest recorded native interaction run | [Run 43989724594 failed](https://github.com/Danvuu/danvu/actions/runs/43989724594), source `07c7014` | The native window remained alive and exited with code 0, but Home navigation failed with `0x800F1000` while reparenting the persistent status element. The failure was exposed in the application log; this was not a completed playback/lyrics pass. |
| Latest candidate scan in that native run | Fresh scan exited 0, real-time protection verified on, no scoped detections | Normal remediation remained enabled. This hosted result does not resolve the earlier alert on the user’s PC. The exact client threat-family name remains unavailable. |
| Navigation/status correction | Source `07f8474` pushed; native verification pending | The status element now stays in its permanent viewport parent rather than being reattached during page construction. The composed loading indicator is retained. A successful managed compile does not prove the current native flow passes. |
| Current desktop redesign | Implemented in source `07f8474`; native screenshot review pending | Charcoal canvas, top transport/player, frosted navigation, compact artwork header, virtualized Song / Artist / Album / Time table, artwork shelves, explicit native slider/button styles, and expanded lyrics. No claim of an exact visual match or measured 60 fps is made. |
| Lyrics blur, following, and transport morph | Implemented in source `07f8474`; current native verification pending | Inactive nearby lyric lines use a real 1.5 px Gaussian composition blur; the active line is crisp. Blur surfaces are bounded to the viewport and released outside it. A reported graphics-driver fallback retains lyrics and seeking if effect creation is unsupported. Following uses a damped spring, and play/pause uses a native animated path morph. |
| Single-file Windows installer | Requested `Music-Setup.exe` is being prepared; unverified and unpublished | Publisher metadata is Danvu. No signing certificate is available, so the installer is unsigned unless legitimate signing credentials are later configured. Metadata alone does not create a verified publisher. No self-signed trust installation or antivirus exception is part of this route. |
| Live Spotify authorization/playback and Jam handoff | Unrun | Requires a developer Client ID, an authorized eligible account, an active Spotify Connect device, and the installed official Spotify app for Jam handoff. |
| Native LRC file picker and restart persistence | Unrun | Save/load paths are implemented; source inspection does not validate picker behavior or restart behavior on Windows. |
| Installation on a clean user machine | Unrun | Native publish includes runtimes, but the install helper, Start menu launch, update, and uninstall still require direct testing. |
| Accessibility, reduced motion, display scaling, and animation performance | Further Windows/hardware checks required | Native control semantics and reduced-motion branches are implemented. Performance and scaling cannot be established from managed compilation or a still image. |

## Resource initialization and build scope

The app is an unpackaged, self-contained WinUI 3 application. A single installer can package its complete runtime bundle; this does not turn the installed application into a standalone single-file WinUI executable. `App` is a partial `Application` class that calls generated `InitializeComponent()` from compiled `App.xaml`. Its application resources merge the standard `XamlControlsResources`; the Windows XAML build produces the application metadata and resource files. The custom STA entry point initializes COM wrappers and the XAML dispatcher synchronization context before constructing the application.

The original Linux native-build attempt failed during Windows App SDK manifest generation. The cached package’s `tools/MSIX/*.msix` names differ in case from the target’s `tools/Msix/*.Msix` glob, and later manifest merging requires Windows SDK tools. The Windows workflow is the native build path. No fabricated manifest or Linux managed check substitutes for that build.

## Required distribution gates

The [Windows distribution workflow](../.github/workflows/windows.yml) requires portable tests, a normal-remediation Defender scan with current signatures and verified real-time protection, then actual native Home, album navigation, demo timeline advance/pause, timed lyrics seek, lyrics close, and graceful window-close checks. A failure blocks binary upload and release. The exact old preview tag is excluded. The candidate `v0.1.1-preview.1` tag has a dedicated gated route; other releases require the repository’s release-enablement variable as well. These are workflow requirements, not recorded passing results for the current source.

The [native diagnostic workflow](../.github/workflows/windows-smoke.yml) captures bounded reports, UI Automation results, application logs, and actual window screenshots. It exports no application binaries or crash dumps to its diagnostic branch. Evidence identifies the source commit so screenshots from an earlier layout cannot validate a later revision.

## Remaining direct verification

1. Run the current native build and complete its protected desktop checks. Inspect the actual Home, collection/table, and lyrics captures against the user’s references before supplying a replacement download.
2. Perform the [Windows desktop smoke test](WINDOWS-SMOKE-TEST.md), including resize/display scaling, interrupted transitions, keyboard focus, reduced motion, native file import, restart behavior, and installation on a clean machine.
3. Test real Spotify browser authorization, denial/cancellation, device playback and loss, expired/denied requests, and disconnection with authorized credentials. Check the official-app Jam handoff separately; Music does not host or inspect Jam sessions.

Demo playback is silent simulation. Spotify mode controls an existing Spotify Connect device; independent in-app streaming is not implemented. The original demo lyrics or an authorized local LRC file supply timed lyrics. The public Spotify API does not provide synced lyrics or Jam participant state. None of the automated demo checks establishes complete Spotify feature parity.
