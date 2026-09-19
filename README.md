# EasyImageImporter

Guided desktop app that takes a trail-camera SD card from "insert" to "safely archived, card wiped".
Windows is the target; development happens on macOS.

- [viltkamera-app-spec.md](viltkamera-app-spec.md): what and why
- [technical-plan.md](technical-plan.md): how, and the milestones

## Develop

```bash
dotnet test
```

Run the app against scratch folders so it doesn't touch your real Pictures folder:

```bash
EASYIMAGEIMPORTER_DATA=/tmp/vk/data EASYIMAGEIMPORTER_ARCHIVE=/tmp/vk/archive dotnet run --project src/EasyImageImporter.App
```

A fake SD card on macOS: a FAT disk image with a `DCIM` folder.

```bash
hdiutil create -size 64m -fs MS-DOS -volname VILTKAM -layout MBRSPUD /tmp/vk/card.dmg
hdiutil attach /tmp/vk/card.dmg
```

Logs go to `<data>/logs/`.

## Release

Push a plain version tag (no `v` prefix), or create a release with a new tag like `0.1.0` in
the GitHub UI. The release workflow tests on Windows, builds the installer and attaches it to the
GitHub release. Installed copies download it in the background and switch to it on next start.

```bash
git tag 0.1.0 && git push origin 0.1.0
```

Configuration for signing and for hosting releases in a separate public repo is described at
the top of [.github/workflows/release.yml](.github/workflows/release.yml).

The installer is `EasyImageImporter-win-Setup.exe`. It installs per user (no admin rights),
adds Desktop and Start menu shortcuts, and starts the app in the tray when Windows starts.
The app's data (database, staging copies) lives in `%LOCALAPPDATA%\EasyImageImporterData`, deliberately
separate from the install folder `%LOCALAPPDATA%\EasyImageImporter`, so uninstalling never
removes staged images. Imported images go to `Pictures\Viltkamera\`.
