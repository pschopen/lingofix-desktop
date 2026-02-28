#!/usr/bin/env node

import { chmodSync, copyFileSync, existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const repoRoot = resolve(__dirname, '..');

const backendProject = join(repoRoot, 'backend', 'Lingofix.Backend.csproj');
const tauriBinariesDir = join(repoRoot, 'tauri', 'binaries');
const tauriResourcesDir = join(repoRoot, 'tauri', 'resources');
const tauriPandocResourcesDir = join(tauriResourcesDir, 'pandoc');
const wordCompareScript = join(repoRoot, 'backend', 'Resources', 'word-compare.scpt');
const libreOfficeCompareScript = join(repoRoot, 'backend', 'Resources', 'libreoffice-compare.py');
const pandocVersion = process.env.PANDOC_VERSION || '3.6.4';

const ridConfig = {
  'osx-arm64': {
    rid: 'osx-arm64',
    sourceName: 'lingofix-backend',
    targetName: 'lingofix-backend-aarch64-apple-darwin',
    executable: true,
    pandocArchive: `pandoc-${pandocVersion}-arm64-macOS.zip`,
    pandocBinaryName: 'pandoc',
    pandocSidecarName: 'pandoc-aarch64-apple-darwin',
  },
  'osx-x64': {
    rid: 'osx-x64',
    sourceName: 'lingofix-backend',
    targetName: 'lingofix-backend-x86_64-apple-darwin',
    executable: true,
    pandocArchive: `pandoc-${pandocVersion}-x86_64-macOS.zip`,
    pandocBinaryName: 'pandoc',
    pandocSidecarName: 'pandoc-x86_64-apple-darwin',
  },
  'win-x64': {
    rid: 'win-x64',
    sourceName: 'lingofix-backend.exe',
    targetName: 'lingofix-backend-x86_64-pc-windows-msvc.exe',
    executable: false,
    pandocArchive: `pandoc-${pandocVersion}-windows-x86_64.zip`,
    pandocBinaryName: 'pandoc.exe',
    pandocSidecarName: 'pandoc-x86_64-pc-windows-msvc.exe',
  },
  'linux-x64': {
    rid: 'linux-x64',
    sourceName: 'lingofix-backend',
    targetName: 'lingofix-backend-x86_64-unknown-linux-gnu',
    executable: true,
    pandocArchive: `pandoc-${pandocVersion}-linux-amd64.tar.gz`,
    pandocBinaryName: 'pandoc',
    pandocSidecarName: 'pandoc-x86_64-unknown-linux-gnu',
  },
};

const defaultTargets = ['osx-arm64', 'osx-x64', 'win-x64', 'linux-x64'];
const targets = parseTargets(process.argv.slice(2));

mkdirSync(tauriBinariesDir, { recursive: true });
mkdirSync(join(tauriResourcesDir, 'binaries'), { recursive: true });
mkdirSync(tauriPandocResourcesDir, { recursive: true });

for (const target of targets) {
  const cfg = ridConfig[target];
  if (!cfg) {
    fail(`Unsupported target '${target}'. Supported: ${Object.keys(ridConfig).join(', ')}`);
  }

  const publishDir = join(tauriBinariesDir, 'publish', cfg.rid);
  mkdirSync(publishDir, { recursive: true });

  run('dotnet', [
    'publish',
    backendProject,
    '-c',
    'Release',
    '-r',
    cfg.rid,
    '-p:PublishSingleFile=true',
    '-p:SelfContained=true',
    '-p:PublishTrimmed=false',
    '-o',
    publishDir,
  ]);

  const sourceBinary = join(publishDir, cfg.sourceName);
  if (!existsSync(sourceBinary)) {
    fail(`Expected published backend binary not found: ${sourceBinary}`);
  }

  const targetBinary = join(tauriBinariesDir, cfg.targetName);
  copyFileSync(sourceBinary, targetBinary);
  if (cfg.executable) {
    chmodSync(targetBinary, 0o755);
  }

  console.log(`Prepared ${target}`);
  console.log(`  source: ${sourceBinary}`);
  console.log(`  target: ${targetBinary}`);

  const pandocBinary = preparePandocBinary(cfg);
  console.log(`  pandoc: ${pandocBinary}`);
}

if (!existsSync(wordCompareScript)) {
  fail(`Script file not found: ${wordCompareScript}`);
}

if (!existsSync(libreOfficeCompareScript)) {
  fail(`Script file not found: ${libreOfficeCompareScript}`);
}

const wordScriptTargets = [
  join(tauriResourcesDir, 'word-compare.scpt'),
  join(tauriResourcesDir, 'binaries', 'word-compare.scpt'),
];

for (const scriptTarget of wordScriptTargets) {
  copyFileSync(wordCompareScript, scriptTarget);
  chmodSync(scriptTarget, 0o644);
}

const libreOfficeScriptTargets = [
  join(tauriResourcesDir, 'libreoffice-compare.py'),
  join(tauriResourcesDir, 'binaries', 'libreoffice-compare.py'),
];

for (const scriptTarget of libreOfficeScriptTargets) {
  copyFileSync(libreOfficeCompareScript, scriptTarget);
  chmodSync(scriptTarget, 0o644);
}

console.log('Copied compare resource files.');

function preparePandocBinary(cfg) {
  const pandocTargetPath = join(tauriPandocResourcesDir, cfg.pandocSidecarName);
  if (existsSync(pandocTargetPath)) {
    if (cfg.executable) {
      chmodSync(pandocTargetPath, 0o755);
    }
    return pandocTargetPath;
  }

  const cacheRoot = join(tauriBinariesDir, 'pandoc-cache', cfg.rid);
  mkdirSync(cacheRoot, { recursive: true });

  const archivePath = join(cacheRoot, cfg.pandocArchive);
  const downloadUrl = `https://github.com/jgm/pandoc/releases/download/${pandocVersion}/${cfg.pandocArchive}`;
  if (!existsSync(archivePath)) {
    run('curl', ['-L', '--fail', '-o', archivePath, downloadUrl]);
  }

  const extractDir = join(cacheRoot, 'extracted');
  rmSync(extractDir, { recursive: true, force: true });
  mkdirSync(extractDir, { recursive: true });
  if (cfg.pandocArchive.endsWith('.zip')) {
    extractZip(archivePath, extractDir);
  } else if (cfg.pandocArchive.endsWith('.tar.gz')) {
    run('tar', ['-xzf', archivePath, '-C', extractDir]);
  } else {
    fail(`Unsupported pandoc archive format: ${cfg.pandocArchive}`);
  }

  const pandocSource = findFileRecursive(extractDir, cfg.pandocBinaryName);
  if (!pandocSource) {
    fail(`Pandoc binary '${cfg.pandocBinaryName}' not found after extraction: ${extractDir}`);
  }

  copyFileSync(pandocSource, pandocTargetPath);
  if (cfg.executable) {
    chmodSync(pandocTargetPath, 0o755);
  }
  return pandocTargetPath;
}

function findFileRecursive(root, targetFileName) {
  const entries = readdirSync(root, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = join(root, entry.name);
    if (entry.isDirectory()) {
      const found = findFileRecursive(fullPath, targetFileName);
      if (found) {
        return found;
      }
      continue;
    }

    if (entry.name === targetFileName) {
      return fullPath;
    }
  }

  return null;
}

function extractZip(archivePath, destinationDir) {
  if (process.platform === 'win32') {
    run('powershell.exe', [
      '-NoProfile',
      '-Command',
      `Expand-Archive -Path '${archivePath.replace(/'/g, "''")}' -DestinationPath '${destinationDir.replace(/'/g, "''")}' -Force`,
    ]);
    return;
  }

  run('unzip', ['-q', '-o', archivePath, '-d', destinationDir]);
}

function parseTargets(argv) {
  const flag = argv.find((arg) => arg.startsWith('--targets='));
  if (flag) {
    return normalizeTargets(flag.slice('--targets='.length));
  }

  const idx = argv.indexOf('--targets');
  if (idx >= 0 && argv[idx + 1]) {
    return normalizeTargets(argv[idx + 1]);
  }

  return defaultTargets;
}

function normalizeTargets(value) {
  return value
    .split(',')
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    stdio: 'inherit',
    shell: false,
  });
  if (result.status !== 0) {
    fail(`Command failed: ${command} ${args.join(' ')}`);
  }
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
