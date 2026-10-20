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

## Updating (automated)

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

### One-time setup

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

### Releasing an update

1. Bump `Version` in `Directory.Build.props` and `Identity/@Version` in the manifest (4th part `0`).
2. Add a `## [x.y.z]` section to `CHANGELOG.md`. It becomes the Store's *What's new* text;
   preview it with `./build/publish-store.ps1 -PreviewNotes`.
3. Commit, then publish a GitHub release tagged `vx.y.z`.
4. Watch the **Release** workflow; the `store` job shows the submission status. Certification
   then takes a few hours to a day.

To send an existing build to the Store without a new release, run the **Release** workflow
manually with *Also submit this build to the Microsoft Store* ticked.

### Gradual rollouts

If `STORE_ROLLOUT_PERCENTAGE` is set, increase or complete the rollout later from any machine
with `msstore` installed:

```powershell
msstore submission rollout update <ProductId> 50
msstore submission rollout finalize <ProductId>   # everyone
msstore submission rollout halt <ProductId>       # stop it
```

### Manual fallback

Build the bundle with `./build/package.ps1 -SkipPortable` (or download it from the workflow's
`qrsnip-packages` artifact). Then, in Partner Center, choose **Start update**, replace the
package, update *What's new*, and submit.
