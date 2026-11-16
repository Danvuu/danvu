# Windows preview antivirus investigation

Status: **v0.1.0-preview.1 withdrawn; no replacement cleared.**

Microsoft Defender blocked the installed `Music.exe` with a Severe / Trojan alert. The user reports that the app never opened. A later screenshot showed that the detected entry had been restored and allowed. The user has been instructed to revoke the allowance and let Defender quarantine the file. The exact threat-family name has not yet been provided.

Do not run this preview, restore it from quarantine, create an antivirus exclusion, or change protection settings to make it run. Rebuilding, renaming, or signing a binary is not a substitute for investigating a detection.

The chat download was removed. The GitHub preview is being moved to draft through the dedicated withdrawal workflow. Binary artifact upload and release creation are paused unless the repository variable `MUSIC_RELEASES_ENABLED` is explicitly set to `true`; leave that variable unset while the investigation remains unresolved.

## Exact artifact

- ZIP SHA-256: `5abc1a283f7c70fd6690ab6bc4c4df35fa2ea3bff66c7c4e93d01132d305bd64`
- `Music.exe` SHA-256: `4376b77e3be4c9de932b4baafe9bef373560c6c3e1904da0c9c1b6602e50dbf5`
- The executable has no Authenticode signature.

The ZIP matches the checksum published with the original release. The executable's native code sections match the official .NET apphost used to build it; the managed DLL marker and application resources were changed by the SDK. These are provenance/integrity observations, **not antivirus clearance**. They do not establish that the managed application or its dependencies are harmless.

Required next evidence includes the exact Defender threat name, current-signature scanning of these unchanged bytes in Windows, scanner engine/signature versions, and review of the managed code and dependencies. An unavailable scanner, stale signatures, a build passing, or another scanner returning clean cannot by itself dismiss the user's detection.

## Recorded Windows on-demand scan

The unchanged release was scanned by the [Windows diagnostic run](https://github.com/Danvuu/danvu/actions/runs/43966794999). Its exact JSON report is preserved on the [`security-diagnostics` branch](https://github.com/Danvuu/danvu/blob/security-diagnostics/reports/antivirus/43966794999.json).

- Both pinned ZIP and executable hashes were verified before scanning.
- Microsoft Defender engine: `1.1.26100.4`; product: `4.18.26100.4`; signature version: `1.463.330.0`.
- Signature update succeeded; signatures were marked current, approximately 13 hours old.
- The original ZIP and extracted bundle each completed an on-demand custom scan with native exit code `0` and the scanner message `found no threats`.
- No scoped detections or diagnostic errors were reported.
- Antivirus and the scanning service were enabled. **Real-time protection was off on this hosted runner.** The script did not turn it off or change antivirus preferences.

This is a limited single-engine on-demand result on a hosted Windows runner. It does not reproduce or override the user's Severe / Trojan alert, and it does not establish a false positive. The exact detection name and the user's Defender versions are still missing. The preview remains withdrawn, binary publishing remains paused, and no replacement has been declared cleared. An exact copy of the original executable was provided to the user for inspection at their explicit request, with its unresolved status stated; no modified executable was issued as a workaround.
