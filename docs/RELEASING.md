# Releasing

WinNewsReader is packaged with [Velopack](https://velopack.io). The installer and the update feed are served from
`winnewsreader.com/releases/`, which is where the app looks for updates (`src/App/UpdateService.cs`).

## Cut a release

1. Bump `<Version>` in `src/App/WinNewsReader.App.csproj`, and add an entry to `site/feed.xml` and `site/news/index.html`. Give the entry's `<article>` the id `v<version>` (for example `id="v1.0.0"`): the GitHub Release notes link to `https://winnewsreader.com/news/#v<version>`.
2. Tag the commit and push the tag: `git tag v1.0.0 && git push origin v1.0.0`. A tag with a hyphen, such as `v1.0.0-test.1`, goes to
   the test channel, which only people who chose "Test builds" in Preferences → Advanced follow.
3. The **Release** workflow tests, publishes and packs, attaches the installer and portable zip to a GitHub Release (public, on the
   repository's Releases page), and deploys the site with the new files in `releases/`. The **Pages** workflow
   carries those files along whenever the rest of the site changes.

## Build one by hand

```
dotnet publish src/App -c Release -r win-x64 --self-contained -p:Platform=x64 -o artifacts/publish
dotnet tool install -g vpk
vpk pack -u WinNewsReaderApp -v 1.0.0 -c release -p artifacts/publish -e WinNewsReader.exe -o artifacts/releases --packTitle WinNewsReader --framework webview2 --icon src/App/Assets/AppIcon.ico
```

`artifacts/releases/WinNewsReaderApp-release-Setup.exe` installs it for the current user, with no administrator rights, and the
`-Portable.zip` runs from any folder. Both install the WebView2 runtime first if the PC lacks it.

## Size

The app is self-contained, so the installer carries the .NET runtime and the Windows App SDK: about 80 MB (the app itself is a few
megabytes). The project references only the Windows App SDK parts it uses (`Base`, `Foundation`, `InteractiveExperiences`, `WinUI`,
`DWrite`, `Runtime`) rather than the `Microsoft.WindowsAppSDK` metapackage, which also brings AI, ML, Search and Widgets (ONNX
Runtime, DirectML and more) and added about 27 MB to the installer. Updates after the first install are deltas, so much smaller.

## Notes

- The install folder is `%LOCALAPPDATA%\WinNewsReaderApp`. The reader's data lives in `%LOCALAPPDATA%\WinNewsReader`, so
  uninstalling leaves feeds and settings alone.
- The installer runs the app with `--veloapp-install`, which registers `feed:`, `feeds:` and `winnewsreader:` links, the
  `.nnwtheme` file type and a Default apps entry (`ShellRegistration`); uninstalling reverses it.
- A second launch, such as clicking a `feed:` link, hands its arguments to the copy already running (`SingleInstance`).

## Signing and the Microsoft Store

Without signing, SmartScreen warns about the installer. The workflow signs with a Certum certificate through SimplySign when the
`CERTUM_USERNAME`, `CERTUM_OTP_URI` and `CERTUM_KEY_ID` repository secrets are all set, and publishes unsigned otherwise. It also
builds an unsigned MSIX for the Microsoft Store and uploads it as a workflow artifact. Both follow the sibling bunyi-app project; how
they work, the Partner Center identity still to fill in, and what to test before submitting are in
[packaging/windows/README.md](../packaging/windows/README.md). Getting the certificate and reserving the name are the open items in PLAN.md.
