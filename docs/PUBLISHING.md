# Publishing QRSnip to the Microsoft Store

QRSnip ships to the Store as an MSIX bundle (x64 + ARM64). The Store signs the
package, so you don't need a code-signing certificate. Everything in the build is
ready; these steps need your Partner Center account.

## 1. One-time setup

1. **Developer account.** Sign up at <https://storedeveloper.microsoft.com> as an
   *individual* (or company) developer. Check the page for the current registration
   fee and identity-verification steps.
2. **Reserve the name.** In [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview)
   choose **Apps and games › New product › MSIX or PWA app** and reserve **QRSnip**.
   If it's taken, try *QRSnip: Screen QR Scanner* and update `DisplayName` in the manifest to match.
3. **Copy the identity.** Open **Product management › Product identity** and copy:
   - `Package/Identity/Name` (e.g. `12345NakulT.QRSnip`)
   - `Package/Identity/Publisher` (e.g. `CN=1A2B3C4D-...`)
   - `Package/Properties/PublisherDisplayName`
4. Paste them into [`src/QRSnip/Package.appxmanifest`](../src/QRSnip/Package.appxmanifest),
   replacing the placeholders `NakulT.QRSnip`, `CN=Nakul T` and `Nakul T`. Commit the change.

## 2. Build the upload package

```powershell
./build/package.ps1 -SkipPortable
```

This writes `artifacts/QRSnip-<version>-store.msixupload`. Each new submission needs a
higher version: bump `Version` in `Directory.Build.props` and `Identity/@Version` in the
manifest (the 4th part must stay `0`).

Before uploading, you can run the [Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit)
on the bundle to catch problems early.

## 3. Create the submission

| Section | What to enter |
|---|---|
| **Pricing and availability** | Free, all markets (or your choice). |
| **Properties** | Category: *Utilities & tools*. Privacy policy URL: `https://github.com/nakultt/QRSnip/blob/main/PRIVACY.md`. Support: `https://github.com/nakultt/QRSnip/issues`. |
| **Age ratings** | Complete the IARC questionnaire (no user interaction, no data sharing → typically 3+). |
| **Packages** | Upload the `.msixupload`. |
| **Store listing** | Copy from [store/listing.md](store/listing.md). Screenshots: `docs/store/1-scan-result.png`, `2-drag-to-scan.png`, `3-app.png`. Logo: `docs/images/logo-300.png`. |
| **Submission options › Restricted capabilities** | `runFullTrust` is required by every WinUI 3 desktop app. Justification: *"QRSnip is a WinUI 3 desktop app (Windows App SDK). Full trust is needed to capture the screen when the user starts a scan, register the user-chosen global shortcut and show a notification-area icon."* |

Submit. Certification usually takes 1–3 business days. After that, put the Store link
in the README's *Install* table.

## Updating

1. Bump the version (see above) and update `CHANGELOG.md`.
2. Publish a GitHub release; the release workflow attaches the new `.msixupload`.
3. In Partner Center, create an **update submission**, replace the package, and submit.
