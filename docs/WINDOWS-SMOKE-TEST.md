# Windows desktop smoke test

Run these checks on Windows after the core tests and native build pass. Linux core test results do not cover them.

1. Extract the full Release zip into an empty directory. Start `Music.exe` on an x64 Windows 10 build 19041+ or Windows 11 computer without a separately installed .NET runtime. Verify the application opens without a WebView or browser-based interface.
2. Browse demo Home, library, albums, playlists, and artist details. Search for an item present in the demo catalog and one that does not exist. Verify both results and the empty state.
3. Resize, maximize, minimize, and restore the window. Verify the player remains reachable and artwork, song rows, and the queue do not overlap at the app's minimum window size. Check display scaling at 100%, 150%, and 200%.
4. Play a demo track and verify the progress timeline advances. Pause, resume, seek, skip, and toggle shuffle/repeat. Demo mode is silent by design; no audio claim should be inferred from an advancing timeline.
5. Open the expanded player and lyrics screen. Verify the active lyric changes with playback, clicking a lyric seeks, and manually scrolling suspends following until “Jump to current” is selected. Import a valid local LRC file and then a malformed/no-timestamp file; verify the native file picker and appropriate empty/error state.
6. Open and close the queue repeatedly. Check row actions, hover feedback, and keyboard focus. If reordering is offered in demo mode, verify its displayed order changes. Spotify's public API cannot reorder an existing playback queue, so the real-account UI must not imply this operation is available.
7. Verify typography and controls with Windows text scaling, keyboard-only Tab/Shift+Tab navigation, and reduced motion enabled in Windows accessibility settings. Check visual transitions for interruptions while navigating quickly. Smoothness requires actual Windows/hardware observation.
8. Enter a valid Spotify Client ID with `http://127.0.0.1:5543/callback` registered. Sign in through the system browser. Test denial, a cancelled login, and a successful browser return. Verify no password or client secret is requested by the desktop app.
9. With an eligible account and active Spotify Connect device, verify search, artist details, library, device selection, track playback, pause/resume, seeking, and volume. Check a device becoming unavailable and an expired/denied API request. Verify useful messages rather than a frozen interface.
10. Open the Jam dialog and verify that it explains the official-app handoff. Select Open Spotify and verify the registered `spotify:` handler opens the official app, using the current Spotify track when available. Verify that Jam is started/joined in Spotify and that Music does not claim to host or synchronize its own session. Also test an absent Spotify URI handler.
11. Close and reopen Music. Verify the documented account persistence behavior. Disconnect the account, restart, and verify the app does not silently retain a usable token or account data in the UI.
12. Run the per-user install helper from the extracted distribution. Launch the Start menu shortcut. Close the application and uninstall it; verify only the app installation and its shortcut are removed, and personal settings are retained as documented.

Record the Windows version, CPU/GPU, display scaling, build commit, and pass/fail result for each check. Real Spotify checks require credentials and API access; report them as unrun when those prerequisites are absent.
