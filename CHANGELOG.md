# Changelog

All notable changes to QRSnip are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Windows 11 style notification-area menu with your last three scans (Open, Copy, Copy password), and a setting to make a left-click start a scan.
- A one-time notice the first time the window is closed while QRSnip keeps running in the background.
- In-app updates: Settings shows the version and where it came from, with **Check for updates** and *Automatically check for updates* (at most once a day). Store installs update through the Store, the new GitHub installer updates itself, and portable copies link to the download.
- GitHub installer (`QRSnipApp-win-x64-Setup.exe` / `arm64`): per-user, no admin rights, Start menu entry, uninstall from *Installed apps*.
- **Quit QRSnip** in Settings.
- Full screen mode (#4): <kbd>Shift</kbd>+<kbd>F10</kbd> (configurable), *Scan full screen* in the tray menu, or the **Area | Full screen** switch in the overlay. Every monitor is decoded in parallel; each code gets an outline and a numbered chip, with <kbd>Tab</kbd> and the arrow keys to move between them. The last mode used is remembered. `--scan-full` on the command line.
- Monitors that GDI captures as black (hardware-accelerated or full-screen apps) are captured again with Windows.Graphics.Capture.

### Fixed

- The cursor no longer flickers while dragging a selection (#1).
- Sharper notification-area icon at every display scaling, with a variant for light taskbars.

### Changed

- QRSnip releases its window and trims memory while it waits in the notification area.

## [1.0.0] - 2026-10-20

First release.

### Added

- Snipping-style capture overlay across all monitors: drag a box around a code, or click to scan a whole screen.
- Result card with Open link, Copy and Copy password actions; Enter, Ctrl+C and Esc shortcuts.
- Decoding of QR codes plus Data Matrix, Aztec and PDF417, including several codes at once, tiny codes and inverted codes.
- Recognition of links, Wi-Fi, email, phone, SMS, contacts and map locations.
- Global scan shortcut (F10 by default, configurable) and a notification-area icon.
- Scan history stored on the device, with Wi-Fi passwords masked in the list.
- Settings: auto-copy, keep running in the background, start with Windows, save history.
- Windows 11 design: WinUI 3, Mica, Fluent icons, light and dark theme.
- Microsoft Store (MSIX) and portable builds for x64 and ARM64.

[1.0.0]: https://github.com/nakultt/QRSnip/releases/tag/v1.0.0
