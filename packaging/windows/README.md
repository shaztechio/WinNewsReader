# Windows packaging: the Microsoft Store and code signing

This follows the method in the sibling bunyi-app project: an **unsigned MSIX** for the Microsoft Store (Partner Center signs it on
submission), and **Certum SimplySign** Authenticode signing for the downloads on winnewsreader.com. The installer and update feed
are still Velopack's (see [docs/RELEASING.md](../../docs/RELEASING.md)).

## The Store package

`build-msix.ps1` packs the self-contained win-x64 app, with the full-trust entry point and `runFullTrust` (no administrator
rights, no AppContainer), into `artifacts/msix/WinNewsReader-<version>-win-x64-store.msix` and a SHA-256 file. The package version is
the app's `<Version>` with a zero fourth part (`1.0.0` is `1.0.0.0`), as the Store requires. It needs Windows, PowerShell 7, the .NET
10 SDK and `MakeAppx.exe` and `MakePri.exe`, which it finds in the Windows SDK or in the `Microsoft.Windows.SDK.BuildTools` package the
app already restores (or pass `-SdkBinPath`).

```powershell
./packaging/windows/build-msix.ps1 -LocalTest          # a package with a test identity, to try packaging
./packaging/windows/build-msix.ps1                     # the Store package, once the identity below is filled in
./packaging/windows/build-msix.ps1 -PublishDirectory artifacts/publish   # reuse a publish folder
```

`test-msix.ps1` runs on the finished package: the manifest, version, executable, resource indexes and every logo the manifest names. Store builds also pass `-Store` to verify that the package name, publisher and publisher display name match Partner Center and that the MSIX container is unsigned.

### Identity

The manifest uses the product identity from Partner Center, reserved on 30 October 2026. These values are public package metadata,
not credentials. The package family name is derived by Windows, so it isn't a manifest field.

| Field | Value |
|---|---|
| Package name | `13743Shazron.WinNewsReader` |
| Publisher | `CN=47FEF9A0-273C-4EA1-A295-51AC5D89C37C` |
| Publisher display name | `Shazron` |
| Display name | `WinNewsReader` |

`build-msix.ps1` refuses a Store build if `REPLACE` is in the identity values. The Release workflow always requests a Store package and fails on invalid identity; manifest comments do not select the package type. Local-test packages require an explicit `-LocalTest` when building by hand.

To regenerate a submission package without republishing the installer, choose **Actions → Store MSIX → Run workflow** on `main`. It builds the version in the app project and uploads `WinNewsReader-Store-MSIX-<version>`, containing the unsigned `-store.msix` and its SHA-256 file. Extract the ZIP before uploading the MSIX to Partner Center. This verifies the package contents and identity; it does not run the Windows App Certification Kit or submit to the Store.

### Artwork and listing

`tools/make-icon.ps1` draws the app icon and also the package's images in `packaging/windows/Assets` (the 44, 150 and Store logos at
each scale, and the 16 to 256 px target sizes with unplated variants) and `StoreListing/StoreLogo-300.png`. `StoreListing/text` holds
the listing copy: description, short description, features and search terms. Screenshots are separate submission work.

### Updates and the Store

A Store copy is updated by the Store, not Velopack. `UpdateService.IsStorePackage` detects package identity, and Help → Check for
Updates says so. The installer's registry entries for `feed:`, `feeds:` and `winnewsreader:` links and `.nnwtheme` files are
Velopack's install hooks, so an MSIX doesn't make them; the package would need protocol and file-type extensions in the manifest
(not added yet: how a packaged full-trust app receives the link needs trying).

### WebView2

An MSIX can't install the WebView2 runtime the way the Velopack installer does. Windows 11 and recent Windows 10 have it, but a PC
without it would show only the control's error. So the package carries Microsoft's bootstrapper, `WebView2/MicrosoftEdgeWebview2Setup.exe`
(downloaded from `https://go.microsoft.com/fwlink/p/?LinkId=2124703`, signed by Microsoft; replace it from that link when Microsoft
updates it), and `WebView2Runtime.EnsureInstalled` asks to run it at startup when the runtime is missing. Whether Store certification
accepts an app running it is confirmed only by submitting; the fallback is to drop the install and keep the message with the link.

The manifest also declares the runtime with `win32dependencies:ExternalDependency` (optional). Microsoft documents that this is honoured
only when the package is installed through App Installer, and ignored for any other install, the Store's included; makeappx
doesn't validate it. It is harmless, but the bootstrapper above is what covers Store installs.

### Submission

Version 1.0.1 (`WinNewsReader-1.0.1-win-x64-store.msix`) was submitted for certification on 30 October 2026, with the en-US listing
from `StoreListing/text`, IARC ratings answered as a news reader whose feeds can carry any content (they come out at 12+), and two
screenshots from `site/help/images`. The Windows App Certification Kit was skipped. If certification rejects the WebView2 bootstrapper,
see the fallback under WebView2.

### Before submitting

Nothing here has been installed yet. Do these first, on a clean Windows VM: build with `-LocalTest`, sign that package with a test
certificate whose subject is exactly `CN=WinNewsReader Local Test`, trust the certificate, and `Add-AppxPackage` it. Then start the
app (the window must load: `resources.pri` merges the app's and the framework's `.pri` files with the package images),
add and read a feed, and check that Preferences, Help → Check for Updates and uninstall behave. Run the Windows App Certification
Kit on the installed package. Do not distribute the local-test package or commit a private key.

Restricted-capability explanation for certification:

> WinNewsReader is a WinUI 3 / .NET desktop feed reader. It uses the desktop full-trust entry point to fetch feeds over the
> network, keep its database in the user's profile and open articles in the default browser. It does not require administrator
> elevation.

## Code signing

`sign-files.ps1` signs with the Certum certificate chosen by its SHA-1 thumbprint, with a SHA-256 digest and a Certum RFC 3161
timestamp, then checks the signature, the thumbprint and the timestamp. `-VerifyOnly` only checks. Locally it needs SimplySign
connected and the Windows SDK's x64 `signtool.exe` (or `WNR_SIGNTOOL`).

The Release workflow signs when these Actions secrets are all set, and fails if only some are:

- `CERTUM_USERNAME`: the SimplySign login email.
- `CERTUM_OTP_URI`: the full `otpauth://totp/...` provisioning URI. It grants signing access, so keep it only in Actions secrets.
- `CERTUM_KEY_ID`: the certificate's 40-character SHA-1 thumbprint, without spaces.

It connects through `dismine/windows-app-signing-setup-action`, pinned to a reviewed commit, then `vpk pack --signTemplate` calls
`sign-files.ps1` for each file Velopack packs, one at a time (`--signParallel 1`) because SimplySign is one authenticated session. The
installer's signature is verified afterwards. Without the secrets the release is unsigned, as the website and README say; once a
signed release is out, change those notices. An OV certificate starts with no SmartScreen reputation, so early signed builds can warn.

The Store MSIX is never signed here.
