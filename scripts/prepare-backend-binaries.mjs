#!/usr/bin/env node

import { chmodSync, copyFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const repoRoot = resolve(__dirname, '..');

const backendProject = join(repoRoot, 'backend', 'Lingofix.Backend.csproj');
const tauriBinariesDir = join(repoRoot, 'tauri', 'binaries');
const tauriResourcesDir = join(repoRoot, 'tauri', 'resources');
const backendScript = join(repoRoot, 'backend', 'Resources', 'word-compare.scpt');

const ridConfig = {
  'osx-arm64': {
    rid: 'osx-arm64',
    sourceName: 'lingofix-backend',
    targetName: 'lingofix-backend-aarch64-apple-darwin',
    executable: true,
  },
  'osx-x64': {
    rid: 'osx-x64',
    sourceName: 'lingofix-backend',
    targetName: 'lingofix-backend-x86_64-apple-darwin',
    executable: true,
  },
  'win-x64': {
    rid: 'win-x64',
    sourceName: 'lingofix-backend.exe',
    targetName: 'lingofix-backend-x86_64-pc-windows-msvc.exe',
    executable: false,
  },
  'linux-x64': {
    rid: 'linux-x64',
    sourceName: 'lingofix-backend',
    targetName: 'lingofix-backend-x86_64-unknown-linux-gnu',
    executable: true,
  },
};

const defaultTargets = ['osx-arm64', 'osx-x64', 'win-x64', 'linux-x64'];
const targets = parseTargets(process.argv.slice(2));

mkdirSync(tauriBinariesDir, { recursive: true });
mkdirSync(join(tauriResourcesDir, 'binaries'), { recursive: true });

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
}

if (!existsSync(backendScript)) {
  fail(`Script file not found: ${backendScript}`);
}

const scriptTargets = [
  join(tauriResourcesDir, 'word-compare.scpt'),
  join(tauriResourcesDir, 'binaries', 'word-compare.scpt'),
];

for (const scriptTarget of scriptTargets) {
  copyFileSync(backendScript, scriptTarget);
  chmodSync(scriptTarget, 0o644);
}

console.log('Copied AppleScript resource files.');

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
