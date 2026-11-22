# Developing BassNotes

BassNotes uses C# with .NET 10 and Avalonia 12.1.6. Install the .NET 10 SDK to build or run it from source.

## Run from source

From the repository directory:

```sh
dotnet run
```

To open a file on launch:

```sh
dotnet run -- "/path/to/bass-stem.flac"
```

## Audio implementation

Playback reads decoded samples from memory through Audio Queue Services on macOS and WASAPI on Windows. Each track retains its native sample rate.

## macOS app bundle

Run `./scripts/package-macos.sh` to build a locally signed app at `bin/Debug/BassNotes.app`, including its Finder and Dock icon. Pass `Release` to package a Release build. This bundle uses the installed .NET 10 runtime. The editable icon source is `assets/AppIcon.svg`; Windows and window icons use `assets/App.ico`, and the macOS bundle uses `assets/macos/AppIcon.icns`.

## Release packages

Run the **Release** workflow manually from GitHub Actions. It builds a self-contained Windows x64 ZIP and macOS Apple Silicon DMG, then uploads each as a downloadable artifact. Set the release version in `BassNotes.csproj`.

For signed and notarized macOS packages, configure the following repository secrets:

- `APPLE_DEVID_APP_CERT_DATA` — base64-encoded Developer ID Application certificate (.p12).
- `APPLE_DEVID_APP_CERT_PASS` — password for the certificate.
- `APPLE_ID_USER` — Apple ID used for notarization.
- `APPLE_ID_PASS` — app-specific password for that Apple ID.

The Apple team ID is read from the certificate's name. Without a certificate, the macOS script produces an ad-hoc-signed package for local testing. With a certificate, it requires notarization credentials and fails if signing or notarization fails. The notarization ticket is stapled to the DMG only.

For local release packaging, run `pwsh scripts/deploy_windows.ps1` or `bash scripts/deploy_macos.sh`. Packages are written to `dist/`. The macOS script builds for the current machine's architecture. These release packages include the .NET runtime.
