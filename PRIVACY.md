# Privacy Policy

_Last updated: 1 November 2026_

QRSnip is a Windows app that reads QR codes from your screen. It is designed to work entirely on your device.

## What QRSnip accesses

- **Your screen, only when you start a scan.** When you press the scan shortcut, click **New scan**, or choose *Scan QR code* from the tray, QRSnip captures an image of your displays so you can select an area. The image is held in memory only while the selection overlay is open and is discarded when it closes. It is never written to disk and never transmitted.
- **Pictures, only the ones you give it.** When you open, drop or paste a picture (or open one with QRSnip from File Explorer), QRSnip reads it on your device to look for codes. It is kept in memory only while the results are shown, and is never copied, saved elsewhere or uploaded. Codes found are added to your history like screen scans.
- **Your clipboard, only when you ask.** Text is copied to the clipboard when you click **Copy** or **Copy password**, press <kbd>Ctrl</kbd>+<kbd>C</kbd> in the overlay, or turn on *Copy results automatically*. A picture on the clipboard is read only when you press <kbd>Ctrl</kbd>+<kbd>V</kbd> in QRSnip.

## What QRSnip stores

Stored locally in `%LOCALAPPDATA%\QRSnip` on your PC:

- **Settings** (your shortcut and preferences).
- **Scan history:** the decoded text of codes you scanned and when, up to 100 entries. You can remove entries, clear all history, or turn history off in Settings (turning it off deletes it).
- **Crash log**, only if the app crashes, to help you report the problem.

## Update checks

QRSnip checks whether a newer version exists, at most once a day (and when you click **Check for updates**). You can turn automatic checks off in **Settings › Updates**.

- **Microsoft Store version:** asks the Microsoft Store for updates to QRSnip, like any Store app. It may also read the release notes for the new version from GitHub.
- **Installed from GitHub or portable:** downloads the list of QRSnip releases from GitHub (`github.com/nakultt/QRSnip`). Updates for the installed version are downloaded from the same place, and each file is checked against its published SHA-256 hash before it is installed.

These requests contain no personal data and nothing about what you scanned. Like any web request they reveal your IP address to the server, along with QRSnip's version number. See the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) and the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

## What QRSnip does not do

- It does not collect analytics, telemetry or usage data.
- It does not send your screen, scans, history or settings anywhere. The update check above is its only network request. Links are opened in your default browser only when you click **Open link** or press <kbd>Enter</kbd>.
- It does not include advertising or third-party tracking.

## Children

QRSnip does not knowingly collect any information from anyone, including children.

## Changes

If this policy changes, the updated version will be published at
<https://github.com/nakultt/QRSnip/blob/main/PRIVACY.md> with a new date.

## Contact

Questions? Open an issue at <https://github.com/nakultt/QRSnip/issues>.
