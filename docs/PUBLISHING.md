# Publishing QRSnip

QRSnip ships through three channels, and each copy updates from the channel it came from
(Windows doesn't let a Store package take an update from anywhere else):

| Channel | Built as | Updates |
|---|---|---|
| Microsoft Store | MSIX bundle (x64 + ARM64), signed by the Store | Store, or **Check for updates** in Settings |
| GitHub installer | `QRSnipApp-win-<arch>-Setup.exe` ([Velopack](https://velopack.io)) | Downloads and installs itself from GitHub releases |
| Portable zip | `QRSnip-<version>-win-<arch>-portable.zip` | Shows a *Download* link when a release is newer |

The first part of this guide covers the Store; [GitHub installer and in-app updates](#github-installer-and-in-app-updates)
covers the other two.

## Microsoft Store

The Store signs the package, so you don't need a code-signing certificate. Everything in
the build is ready; these steps need your Partner Center account.

### 1. One-time setup

1. **Developer account.** Sign up at <https://storedeveloper.microsoft.com> as an
   *individual* (or company) developer. Check the page for the current registration
   fee and identity-verification steps.
2. **Reserve the name.** In [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview)
   choose **Apps and games › New product › MSIX or PWA app** and reserve **QRSnip**.
   If it's taken, try *QRSnip: Screen QR Scanner* and update `DisplayName` in the manifest to match.
3. **Copy the identity.** Open **Product management › Product identity** and copy
   `Package/Identity/Name`, `Package/Identity/Publisher` and `Package/Properties/PublisherDisplayName`.
4. Paste them into [`src/QRSnip/Package.appxmanifest`](../src/QRSnip/Package.appxmanifest) and commit.

   ✅ Done for QRSnip. The manifest uses the reserved identity:

   | Field | Value |
   |---|---|
   | Store ID | `9NSSPZM48DSR` |
   | `Identity/Name` | `NakulT.QRSnip` |
   | `Identity/Publisher` | `CN=945D7B2F-F832-490D-B1E0-F9297D5F227C` |
   | `PublisherDisplayName` | `Nakul T` |
   | Package family name | `NakulT.QRSnip_qpbc62z0nzv2y` |

### Before the first submission: check the packaged build

Install the bundle locally (`./build/package.ps1 -SkipPortable -SkipInstaller`, then sign the
`.msix` for your machine or use *Deploy* in Visual Studio) on x64 and ARM64 and check:

- [ ] *Start with Windows* uses the `StartupTask`: sign out and in; QRSnip starts in the tray
      (and appears in *Settings › Apps › Startup*).
- [ ] `qrsnip --scan` works from a terminal (app execution alias).
- [ ] Settings and history survive an update (install a higher version over it) and are gone
      after uninstalling. Packaged desktop apps write `%LOCALAPPDATA%\QRSnip` into the
      package's own storage, which Windows keeps on update and removes on uninstall.
- [ ] Both architectures install from the same bundle.
- [ ] The [Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit)
      passes: `appcert.exe test -appxpackagepath <bundle> -reportoutputpath wack.xml`.

### 2. Build the upload package

```powershell
./build/package.ps1 -SkipPortable
```

This writes `artifacts/QRSnip-<version>-store.msixupload`. Each new submission needs a
higher version: bump `Version` in `Directory.Build.props` and `Identity/@Version` in the
manifest (the 4th part must stay `0`).

Before uploading, you can run the [Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit)
on the bundle to catch problems early.

### 3. Create the submission

| Section | What to enter |
|---|---|
| **Pricing and availability** | Free, all markets (or your choice). |
| **Properties** | Category: *Utilities & tools*. Privacy policy URL: `https://github.com/nakultt/QRSnip/blob/main/PRIVACY.md`. Support: `https://github.com/nakultt/QRSnip/issues`. |
| **Age ratings** | Complete the IARC questionnaire (no user interaction, no data sharing → typically 3+). |
| **Packages** | Upload the `.msixupload`. |
| **Store listing** | Copy from [store/listing.md](store/listing.md). Screenshots: `docs/store/1-scan-result.png`, `2-drag-to-scan.png`, `3-app.png`. Logo: `docs/images/logo-300.png`. |
| **Properties › Product declarations / Camera** | QRSnip uses the webcam (`webcam` capability) only while the user scans with the camera; frames are processed on the device and not stored or transmitted. Mention this in the description (already in [store/listing.md](store/listing.md)). |
| **Submission options › Restricted capabilities** | `runFullTrust` is required by every WinUI 3 desktop app. Justification: *"QRSnip is a WinUI 3 desktop app (Windows App SDK). Full trust is needed to capture the screen when the user starts a scan, register the user-chosen global shortcut and show a notification-area icon."* |

Submit. Certification usually takes 1–3 business days. After that, put the Store link
in the README's *Install* table.

### Updating (automated)

After the first release is live, updates are submitted by the
[release workflow](../.github/workflows/release.yml) using the
[Microsoft Store Developer CLI](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview).
Publishing a GitHub release then:

1. builds and tests, and attaches the portable zips to the release;
2. uploads the `.msixupload` to the Store as a new submission;
3. sets *What's new in this version* from that version's section of `CHANGELOG.md`;
4. optionally starts a gradual rollout;
5. submits it for certification.

Pre-releases are never sent to the Store.

#### One-time setup

1. **Link a Microsoft Entra ID tenant** to Partner Center: *Account settings › Tenants*
   (associate an existing one or create a new one).
2. **Register an app** in that tenant ([Entra admin center](https://entra.microsoft.com) ›
   *App registrations › New registration*) and create a **client secret** for it
   (*Certificates & secrets*). Copy the secret immediately.
3. In Partner Center, *Account settings › User management › Microsoft Entra applications*,
   **add that app with the Manager role**.
4. In GitHub, *Settings › Secrets and variables › Actions*, add:

   | Secrets | Where to find it |
   |---|---|
   | `AZURE_AD_TENANT_ID` | Entra admin center › Overview › Tenant ID |
   | `AZURE_AD_APPLICATION_CLIENT_ID` | App registration › Application (client) ID |
   | `AZURE_AD_APPLICATION_SECRET` | The client secret value from step 2 |
   | `SELLER_ID` | Partner Center › Account settings › Identifiers › Seller ID |

   | Variables | Value |
   |---|---|
   | `STORE_PRODUCT_ID` | The app's Store ID, e.g. `9N...` (Partner Center › Product identity). **Setting this turns the Store job on.** |
   | `STORE_ROLLOUT_PERCENTAGE` | Optional, e.g. `10` for a gradual rollout. Leave unset to release to everyone. |

5. Optional: in *Settings › Environments › microsoft-store*, add yourself as a **required
   reviewer** so each Store submission waits for your approval in the Actions tab.

#### Releasing an update

1. Bump `Version` in `Directory.Build.props` and `Identity/@Version` in the manifest (4th part `0`).
2. Add a `## [x.y.z]` section to `CHANGELOG.md`. It becomes the Store's *What's new* text;
   preview it with `./build/publish-store.ps1 -PreviewNotes`.
3. Commit, then publish a GitHub release tagged `vx.y.z`.
4. Watch the **Release** workflow; the `store` job shows the submission status. Certification
   then takes a few hours to a day.

To send an existing build to the Store without a new release, run the **Release** workflow
manually with *Also submit this build to the Microsoft Store* ticked.

#### Gradual rollouts

If `STORE_ROLLOUT_PERCENTAGE` is set, increase or complete the rollout later from any machine
with `msstore` installed:

```powershell
msstore submission rollout update <ProductId> 50
msstore submission rollout finalize <ProductId>   # everyone
msstore submission rollout halt <ProductId>       # stop it
```

#### Manual fallback

Build the bundle with `./build/package.ps1 -SkipPortable` (or download it from the workflow's
`qrsnip-packages` artifact). Then, in Partner Center, choose **Start update**, replace the
package, update *What's new*, and submit.

## GitHub installer and in-app updates

`build/package.ps1` publishes each architecture once and turns the result into both the
portable zip and, with `vpk pack`, the installer and its update feed in
`artifacts/velopack/win-<arch>/`:

- `QRSnipApp-win-<arch>-Setup.exe`: per-user install into `%LOCALAPPDATA%\QRSnipApp`, no admin
  rights, Start menu entry, listed in *Installed apps*.
- `QRSnipApp-<version>-win-<arch>-full.nupkg` (and `-delta.nupkg` when the previous release
  was downloaded first) plus `releases.win-<arch>.json`, which installed copies read.

The installer's id is `QRSnipApp`, not `QRSnip`: Velopack empties its install folder if it
already exists, and `%LOCALAPPDATA%\QRSnip` holds the settings and history that portable
copies use too. Settings stay there across updates; uninstalling removes them.

The [release workflow](../.github/workflows/release.yml) downloads the previous release's
packages (for deltas), packs, and runs `vpk upload github --merge` for `win-x64` and
`win-arm64`, adding the files to the release you published. Pre-releases are marked as such
and installed copies ignore them. `vpk` is pinned in [.config/dotnet-tools.json](../.config/dotnet-tools.json);
keep it at the same version as the `Velopack` package in `src/QRSnip/QRSnip.csproj`.

Every pull request also builds the x64 installer and portable zip (the `installer` CI job);
download them from the run's `qrsnip-x64-preview` artifact to try a change.

### How the app updates

- **Settings › Updates** shows the version and channel, **Check for updates**, and
  *Automatically check for updates* (on by default). Automatic checks run at most once a day,
  never during a scan, and fail silently when offline or rate-limited.
- When an update is found, a bar appears at the top of the window with the version, its
  *What's new* text (from `CHANGELOG.md`) and **Restart to update**. While QRSnip is in the
  notification area, a Windows notification is shown once per version.
- **GitHub installer:** Velopack downloads the update (a delta when available), checks its
  SHA-256 against the feed, and restarts into the new version. The tray icon and shortcut
  keep working until the restart.
- **Store:** `StoreContext` checks for and installs the update; Windows restarts QRSnip
  afterwards. Updates marked *mandatory* in Partner Center show a bar that can't be dismissed.
- **Portable:** compares with the latest GitHub release and opens its page.

### Testing an update end to end

1. Publish a release (say `v1.1.0`) and install it with its `Setup.exe`.
2. Publish the next release (`v1.1.1`) with the workflow.
3. In the installed copy, open **Settings › Check for updates**, then **Restart to update**.
   QRSnip should restart as 1.1.1 with its settings and history intact.

For the Store channel, submit two versions and use *Check for updates* in the older one
(a package flight in Partner Center keeps this away from everyone else).

## winget

Two ways to get QRSnip into `winget`:

- **From the Store:** once QRSnip is live, `winget install QRSnip --source msstore` finds it
  automatically. Nothing to publish.
- **From GitHub releases (community repository):** a manifest in
  [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) pointing at the
  installers, so `winget install NakulT.QRSnip` works. The release workflow keeps it current.

### First submission (once)

After a release with installers exists:

```powershell
winget install wingetcreate
wingetcreate new https://github.com/nakultt/QRSnip/releases/download/vX.Y.Z/QRSnipApp-win-x64-Setup.exe `
                 https://github.com/nakultt/QRSnip/releases/download/vX.Y.Z/QRSnipApp-win-arm64-Setup.exe
```

Answer the prompts with:

| Field | Value |
|---|---|
| PackageIdentifier | `NakulT.QRSnip` |
| Publisher / PackageName | `Nakul T` / `QRSnip` |
| InstallerType | `exe` |
| Scope | `user` |
| Silent / SilentWithProgress switches | `--silent` |
| ProductCode (AppsAndFeaturesEntries) | `QRSnipApp` |
| License / ShortDescription | `MIT` / the Store short description |

Submit the pull request it opens. Once it's merged:

1. Create a classic personal access token with the `public_repo` scope (wingetcreate forks
   winget-pkgs with it) and add it as the **`WINGET_TOKEN`** secret.
2. Add the repository variable **`WINGET_PACKAGE_ID`** = `NakulT.QRSnip`. This turns on the
   `winget` job: every non-pre-release release then runs `wingetcreate update --submit`.

## Code signing

Without a signature, Windows SmartScreen warns on the first download of `Setup.exe` and the
portable `QRSnip.exe` (updates after that install silently). The Store signs its own
packages, so this only matters for GitHub downloads.

The release workflow can sign with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/)
(formerly Trusted Signing; [SignPath Foundation](https://signpath.org) is a free alternative for
open source). When configured it signs `QRSnip.exe` before it is zipped and packed, and `vpk`
signs the installer and the files it installs, all through `package.ps1 -SignTemplate`.

1. Create an Artifact Signing account and a *Public Trust* certificate profile (identity
   validation is required).
2. In Microsoft Entra ID, register an app with a **federated credential** for this
   repository (GitHub Actions, entity *Environment* or *Branch* as you prefer), and give it the
   *Artifact Signing Certificate Profile Signer* role on the account.
3. Add secrets **`AZURE_CLIENT_ID`**, **`AZURE_TENANT_ID`**, **`AZURE_SUBSCRIPTION_ID`**, and variables
   **`ARTIFACT_SIGNING_ENDPOINT`** (e.g. `https://weu.codesigning.azure.net/`),
   **`ARTIFACT_SIGNING_ACCOUNT`** and **`ARTIFACT_SIGNING_PROFILE`**. Setting the endpoint turns
   signing on.

Locally, pass any signing command with `{{file}}` where the file goes:

```powershell
./build/package.ps1 -SignTemplate '"C:\path\signtool.exe" sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 {{file}}'
```

A sideloaded MSIX with an `.appinstaller` file isn't produced: the GitHub installer already
gives non-Store users a Start menu entry, clean uninstall and automatic updates.
