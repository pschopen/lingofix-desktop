# Lingofix

Lingofix is a desktop app for correcting text, DOCX files, and ODT files with AI.

It is built for people who want fast proofreading help without using a browser-based editor. You can paste plain text, load one or more documents, review the result, and save corrected output on your computer.

<p>
  <a href="https://github.com/pschopen/lingofix-desktop/releases">
    <img src="https://img.shields.io/badge/Download_latest_release-GitHub_Releases-2ea44f?style=for-the-badge&logo=github" alt="Download latest Lingofix release from GitHub Releases">
  </a>
</p>

## What Lingofix Does

- Corrects plain text with AI
- Processes `.docx` and `.odt` files
- Supports tracked-change style workflows for office documents
- Lets you choose between multiple AI providers
- Runs as a desktop app for macOS, Windows, and Linux
- Offers an English and German interface

## Who It Is For

Lingofix is useful if you want to:

- proofread drafts, emails, reports, or academic text
- improve spelling and grammar while keeping your writing style
- correct Word or OpenDocument files without copying everything into a web form
- use your own AI provider and model

## How It Works

### Plain text

1. Open Lingofix.
2. Open `Settings`.
3. Choose your provider, enter your API key if required, and select a model.
4. Paste your text into the editor.
5. Click `Correct`.
6. Review the highlighted differences and either apply or reject the changes.

### DOCX or ODT documents

1. Open Lingofix.
2. Configure your provider and model in `Settings`.
3. Drag a `.docx` or `.odt` file into the app, or choose a file manually.
4. Click `Correct`.
5. Wait for processing to finish.
6. Open the generated output file from the result banner.

Depending on your compare mode, Lingofix can generate a corrected file with tracked changes or fall back to a corrected output file without generated change markup.

## Download and Installation

Download the latest release from GitHub:

- [Download latest release](https://github.com/pschopen/lingofix-desktop/releases)

### macOS

Choose the correct file for your Mac:

- Apple Silicon (M1, M2, M3, M4): `Lingofix-Desktop-vX.Y.Z-macos-arm64.zip`
- Intel Mac: `Lingofix-Desktop-vX.Y.Z-macos-x64.zip`

Install steps:

1. Download the `.zip` file from the latest release.
2. Unzip it.
3. Drag `Lingofix Desktop.app` into `Applications`.
4. Start the app.

### Windows

Download:

- `Lingofix-Desktop-vX.Y.Z-windows-x64.exe`

Install steps:

1. Download the installer.
2. Run the `.exe` file.
3. Follow the installation wizard.

### Linux

Download:

- `Lingofix-Desktop-vX.Y.Z-linux-x64.flatpak`

Install with Flatpak:

```bash
flatpak install --user ./Lingofix-Desktop-vX.Y.Z-linux-x64.flatpak
flatpak run com.lingofix.desktop
```

## macOS: Open an App From an Unidentified Developer

If macOS blocks Lingofix because it is not signed by a verified Apple developer, use these steps:

1. Move the app to `Applications`.
2. Double-click the app once.
3. macOS will show a warning and refuse to open it.
4. Open `System Settings` > `Privacy & Security`.
5. Scroll to the security section near the bottom.
6. Find the message about `Lingofix Desktop.app` being blocked.
7. Click `Open Anyway`.
8. Open the app again.
9. Confirm by clicking `Open` in the dialog.

If `Open Anyway` does not appear immediately, try this fallback:

1. In `Applications`, right-click `Lingofix Desktop.app`.
2. Choose `Open`.
3. Confirm with `Open`.

After that, macOS should allow future launches normally.

## First-Time Setup

Before Lingofix can correct text, you usually need to configure an AI provider.

1. Open `Settings`.
2. Select a provider.
3. Enter the API URL only if your provider requires a custom one.
4. Enter your API key if needed.
5. Click `Load models`.
6. Pick a model.
7. Save the settings.

### Provider notes

- `OpenAI`, `OpenRouter`, `Hugging Face`, `Google AI Studio`, and `Mistral` usually require an API key.
- `Ollama` is for local use and usually works without an API key if Ollama is running on your computer.
- `Custom` is for OpenAI-compatible or other custom endpoints.

## Working With Plain Text

Lingofix can correct pasted text directly in the editor.

- Type or paste your text into the main editor
- Press `Correct`
- Wait for the corrected result
- Review the differences
- Use `Apply` to keep the correction or `Reject` to discard it

The app is designed to keep the output focused on the corrected text instead of long explanations.

## Working With DOCX and ODT Files

You can drop office documents into the app or select them with the file picker.

Typical workflow:

- add one or more `.docx` or `.odt` files
- start correction
- review progress and log messages
- open the corrected result from the result banner

### Compare modes

Lingofix includes different compare modes for document correction.

#### OpenXML (built-in)

- Built into the app
- Works without Microsoft Word or LibreOffice
- Best for self-contained workflows
- Can change layout or formatting in some cases
- Not recommended for ODT files

#### Word (native)

- Recommended for `.docx` files when Microsoft Word is available
- Requires Microsoft Word
- On macOS, you may need to grant automation permissions the first time

#### LibreOffice UNO (native)

- Useful when working with LibreOffice or `.odt` files
- Requires LibreOffice and the `soffice` command to be available

## Updates

Lingofix can check GitHub Releases for updates.

- automatic update checks can run at startup and then once per day
- you can also trigger a manual update check from `Settings`
- download links open the official release page for this repository

## Troubleshooting

### The app cannot connect to my model

Check the following:

- provider is selected correctly
- API key is valid
- API URL is correct
- the selected model is available for that provider
- your local service is running if you use Ollama

### A DOCX or ODT run does not produce tracked changes

That can happen if:

- the selected compare mode is not ideal for the document
- Word or LibreOffice is not available
- the app falls back to a corrected file without generated track changes

For best results:

- use `Word` mode for `.docx` when Microsoft Word is installed
- use `LibreOffice UNO` for `.odt` when LibreOffice is installed

### The app behaves strangely after a broken configuration

Open `Settings` > `Advanced` and use `Reset app`.

You can also open:

- the temp folder
- `settings.json`
- `debug.log`

directly from the advanced settings section.

## Privacy and Credentials

Lingofix sends text or document content to the AI provider you configure.

Please make sure you understand the privacy and data-handling rules of your chosen provider before processing sensitive material.

## For Developers

### Project structure

```text
lingofix-desktop/
  Lingofix.slnx         .NET solution
  frontend/             React + Vite UI
  backend/              .NET document processing backend
  tauri/                Tauri desktop host
```

### Prerequisites

- Node.js 18+
- .NET SDK 10+
- Rust toolchain

### Setup

```bash
npm run setup
npm run build
```

### Build targets

- macOS universal workflow helper: `npm run build:app:mac`
- macOS ARM64: `npm run build:app:mac:arm64`
- macOS x64: `npm run build:app:mac:x64`
- Windows x64 installer: `npm run build:app:win`
- Linux Flatpak: `npm run build:app:linux:flatpak`

### Backend binaries only

```bash
npm run prepare:backend:binaries
```

### Release process

GitHub Actions publishes release assets when you push a version tag matching `v*`.

Version source of truth:

- `tauri/Cargo.toml`

Example:

```bash
git tag v0.1.0
git push origin v0.1.0
```

This produces release assets such as:

- `Lingofix-Desktop-v0.1.0-macos-arm64.zip`
- `Lingofix-Desktop-v0.1.0-macos-x64.zip`
- `Lingofix-Desktop-v0.1.0-windows-x64.exe`
- `Lingofix-Desktop-v0.1.0-linux-x64.flatpak`

## License

GNU AGPL v3
