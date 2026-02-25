# Lingofix

Lingofix is a desktop text and DOCX correction tool with a React frontend, a Tauri host, and a .NET backend for DOCX processing.

## Project Structure

```
lingofix-cs/
  Lingofix.slnx         .NET solution
  frontend/             React + Vite web UI
  backend/              .NET DOCX processor and compare engine
  tauri/                Tauri desktop host (Rust)
```

## Features

- Text correction with streaming output
- DOCX correction with progress updates
- Diff view and track-changes workflow
- Multiple providers (OpenAI-compatible, Ollama, OpenRouter, Hugging Face, Google, Mistral)
- English/German UI

## Prerequisites

- Node.js 18+
- .NET SDK 10+

## Development

```bash
npm run setup
npm run build
```

## Build

Windows (self-contained single-file backend build):

```bash
npm run publish:win
```

Further targets:

- macOS ARM64: `npm run publish:osx-arm64`
- macOS x64: `npm run publish:osx-x64`
- Linux x64: `npm run publish:linux-x64`

### Desktop App Bundles (self-contained backend included)

These commands publish the .NET backend as self-contained binaries and bundle them into the Tauri app, so no separate .NET runtime is required on target machines.

- macOS app bundle (includes ARM64 + x64 backend binaries): `npm run build:app:mac`
- macOS ARM64 app bundle target: `npm run build:app:mac:arm64`
- macOS x64 app bundle target: `npm run build:app:mac:x64`
- Windows installer build (includes win-x64 backend binary): `npm run build:app:win`
- Linux AppImage build (includes linux-x64 backend binary): `npm run build:app:linux`

You can also prepare backend binaries directly:

```bash
npm run prepare:backend:binaries
```

### CI Build Pipeline

GitHub Actions workflow `Desktop Bundles` builds distributable artifacts for:

- macOS ARM64 (`.app` zipped)
- macOS x64 (`.app` zipped)
- Windows x64 (NSIS `.exe` installer)
- Linux x64 (AppImage)

You can run it manually from the Actions tab via `workflow_dispatch`.

### GitHub Releases

Release assets are published automatically by the `Release` workflow when you push a version tag matching `v*`.

Version source of truth: `tauri/Cargo.toml` (`[package].version`).

Release tags must match that version exactly (`vX.Y.Z` for `X.Y.Z` in `tauri/Cargo.toml`).

Example:

```bash
git tag v0.2.0
git push origin v0.2.0
```

This creates a GitHub Release with:

- `Lingofix-Desktop-v0.2.0-macos-arm64.zip`
- `Lingofix-Desktop-v0.2.0-macos-x64.zip`
- `Lingofix-Desktop-v0.2.0-windows-x64.exe`
- `Lingofix-Desktop-v0.2.0-linux-x64.AppImage`

Release notes are generated automatically from GitHub.

## macOS Startup

If macOS blocks the app (unidentified developer):

1. Open **System Settings** > **Privacy & Security**.
2. Click **Allow Anyway** next to the blocked app message.
3. Open the app again and confirm with **Open**.

The app should now launch normally.

```bash
npm run build:app:mac
open "tauri/target/release/bundle/macos/Lingofix Desktop.app"
```

## License

GNU AGPL v3
