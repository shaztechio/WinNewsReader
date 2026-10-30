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

`test-msix.ps1` runs on the finished package: the manifest, version, executable, resource indexes and every logo the manifest names.

### Identity

The manifest uses the product identity from Partner Center, reserved on 30 October 2026. These values are public package metadata,
not credentials. The package family name is derived by Windows, so it isn't a manifest field.

| Field | Value |
|---|---|
| Package name | `13743Shazron.WinNewsReader` |
| Publisher | `CN=47FEF9A0-273C-4EA1-A295-51AC5D89C37C` |
| Publisher display name | `Shazron` |
| Display name | `WinNewsReader` |

`build-msix.ps1` refuses a Store build if `REPLACE` is in the identity, and the release workflow then builds the `-LocalTest` package
instead. With the real identity in place, every release builds the Store package.

### Artwork and listing

`tools/make-icon.ps1` draws the app icon and also the package's images in `packaging/windows/Assets` (the 44, 150 and Store logos at
each scale, and the 16 to 256 px target sizes with unplated variants) and `StoreListing/StoreLogo-300.png`. `StoreListing/text` holds
the listing copy: description, short description, features and search terms. Screenshots are separate submission work.

### Updates and the Store

A Store copy is updated by the Store, not Velopack. `UpdateService.IsStorePackage` detects package identity, and Help → Check for
Updates says so. The installer's registry entries for `feed:`, `feeds:` and `winnewsreader:` links and `.nnwtheme` files are
Velopack's install hooks, so an MSIX doesn't make them; the package would need protocol and file-type extensions in the manifest
(not added yet: how a packaged full-trust app receives the link needs trying).

### Before submitting

Nothing here has been installed yet. Do these first, on a clean Windows VM: build with `-LocalTest`, sign that package with a test
certificate whose subject is exactly `CN=WinNewsReader Local Test`, trust the certificate, and `Add-AppxPackage` it. Then start the
app (the window must load: `resources.pri` indexes only the package images, and the app's own `WinNewsReader.pri` sits beside it),
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
