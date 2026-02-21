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

## macOS Startup and Signing Notes

- Unsigned/ad-hoc signed apps can start slower on macOS and may show a blank window longer during initial verification.
- For production distribution, sign and notarize `Lingofix.app` with an Apple Developer ID certificate.
- Local testing build:

```bash
cd tauri
cargo tauri build --bundles app
open target/release/bundle/macos/Lingofix.app
```

## License

MIT
