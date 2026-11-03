# Changelog

All notable changes to QRSnip are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-11-03

### Added

- Windows 11 style notification-area menu with your last three scans (Open, Copy, Copy password), and a setting to make a left-click start a scan.
- A one-time notice the first time the window is closed while QRSnip keeps running in the background.
- In-app updates: Settings shows the version and where it came from, with **Check for updates** and *Automatically check for updates* (at most once a day). Store installs update through the Store, the new GitHub installer updates itself, and portable copies link to the download.
- GitHub installer (`QRSnipApp-win-x64-Setup.exe` / `arm64`): per-user, no admin rights, Start menu entry, uninstall from *Installed apps*.
- **Quit QRSnip** in Settings.
- Automatic code finding (#4): as soon as the scan overlay opens, every monitor is decoded in parallel and each code gets an outline and a numbered chip, so a single code shows its card without dragging. Click a number, or use <kbd>Tab</kbd> and the arrow keys, to move between several codes; dragging still scans an area. One shortcut (<kbd>F10</kbd>) does both, with no mode to pick.
- The main window can be resized and maximized. When it is wide, scanning is on the left and recent scans on the right; its size is remembered.
- Scan pictures (#5): **Open picture**, drag and drop picture files (or a picture from a browser) onto the window, or paste with <kbd>Ctrl</kbd>+<kbd>V</kbd>. Each picture is shown with its codes outlined and the usual result cards; large phone photos are also tried at reduced sizes, and EXIF orientation is respected. `--file <path>` on the command line, and *Open with* in File Explorer for the Store version.
- Scan with the camera (#6): **Camera** on the home page or *Scan with camera* in the tray menu (`--camera`). Live preview with a scan frame, camera picker (remembered), mirror and light toggles; the code is outlined and shown in the usual card, and each distinct code is added to history once. The camera turns off when you leave the view, minimize QRSnip or it goes to the tray; access denied, no camera and camera-in-use each show what to do.
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

[1.1.0]: https://github.com/nakultt/QRSnip/releases/tag/v1.1.0
[1.0.0]: https://github.com/nakultt/QRSnip/releases/tag/v1.0.0
