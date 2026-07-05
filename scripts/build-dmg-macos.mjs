#!/usr/bin/env node

import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const repoRoot = resolve(__dirname, '..');

const targetArg = getArgValue(process.argv.slice(2), '--target') ?? 'native';
const { appPath, bundleDir, triple } = resolveTarget(targetArg);

if (!existsSync(appPath)) {
  fail(`macOS app bundle not found: ${appPath}`);
}

const arch = detectArch(appPath, triple);
const version = readCargoVersion();
const artifactsDir = join(repoRoot, 'artifacts');
const stagingDir = join(artifactsDir, 'dmg-staging');
const dmgName = `Lingofix-Desktop-v${version}-macos-${arch}.dmg`;
const dmgPath = join(artifactsDir, dmgName);
const mountedVolume = '/Volumes/Lingofix Desktop';

cleanup(stagingDir);
cleanup(dmgPath);
mkdirSync(artifactsDir, { recursive: true });
mkdirSync(stagingDir, { recursive: true });

console.log(`Staging: ${stagingDir}`);
run('ditto', [appPath, join(stagingDir, 'Lingofix Desktop.app')]);
symlinkSync('/Applications', join(stagingDir, 'Applications'));

console.log(`Creating DMG: ${dmgPath}`);
run('hdiutil', [
  'create',
  '-srcfolder', stagingDir,
  '-format', 'UDZO',
  '-fs', 'HFS+',
  '-volname', 'Lingofix Desktop',
  '-ov',
  dmgPath,
]);

console.log(`Verifying DMG: ${dmgPath}`);
run('hdiutil', ['verify', dmgPath]);

console.log(`Mounting DMG for signature verification: ${dmgPath}`);
run('hdiutil', ['attach', dmgPath, '-nobrowse', '-quiet', '-mountpoint', mountedVolume]);

try {
  const mountedApp = join(mountedVolume, 'Lingofix Desktop.app');
  if (!existsSync(mountedApp)) {
    fail(`Expected app not found in mounted DMG: ${mountedApp}`);
  }

  const symlinkTarget = join(mountedVolume, 'Applications');
  if (!existsSync(symlinkTarget)) {
    fail(`Expected Applications symlink not found in mounted DMG: ${symlinkTarget}`);
  }

  run('codesign', ['--verify', '--deep', '--strict', '--verbose=2', mountedApp]);
} finally {
  run('hdiutil', ['detach', mountedVolume, '-quiet'], { allowFailure: true });
}

cleanup(stagingDir);

console.log(`Built and verified: ${dmgPath}`);
console.log(`Target triple: ${triple}`);
console.log(`Detected arch: ${arch}`);

function resolveTarget(target) {
  const byTarget = {
    native: {
      triple: 'native',
      bundleDir: join(repoRoot, 'tauri', 'target', 'release', 'bundle', 'macos'),
    },
    'aarch64-apple-darwin': {
      triple: 'aarch64-apple-darwin',
      bundleDir: join(repoRoot, 'tauri', 'target', 'aarch64-apple-darwin', 'release', 'bundle', 'macos'),
    },
    'x86_64-apple-darwin': {
      triple: 'x86_64-apple-darwin',
      bundleDir: join(repoRoot, 'tauri', 'target', 'x86_64-apple-darwin', 'release', 'bundle', 'macos'),
    },
  };

  const cfg = byTarget[target];
  if (!cfg) {
    fail(`Unsupported --target '${target}'. Expected one of: ${Object.keys(byTarget).join(', ')}`);
  }

  return {
    ...cfg,
    appPath: join(cfg.bundleDir, 'Lingofix Desktop.app'),
  };
}

function detectArch(appPath, triple) {
  if (triple === 'aarch64-apple-darwin') return 'arm64';
  if (triple === 'x86_64-apple-darwin') return 'x64';

  const binaryPath = join(appPath, 'Contents', 'MacOS', 'Lingofix Desktop');
  if (!existsSync(binaryPath)) {
    fail(`Could not detect architecture: binary not found at ${binaryPath}.`);
  }

  const result = spawnSync('lipo', ['-archs', binaryPath], { encoding: 'utf8' });
  if (result.status !== 0) {
    fail(`lipo -archs failed: ${result.stderr || 'unknown error'}`);
  }

  const archs = (result.stdout || '').trim().split(/\s+/).filter(Boolean);
  if (archs.length === 0) {
    fail(`lipo -archs returned no architectures for ${binaryPath}.`);
  }

  if (archs.includes('arm64') && archs.includes('x86_64')) return 'universal';
  if (archs.includes('arm64')) return 'arm64';
  if (archs.includes('x86_64')) return 'x64';

  fail(`Unexpected architectures reported by lipo: ${archs.join(', ')}`);
}

function readCargoVersion() {
  const cargoTomlPath = join(repoRoot, 'tauri', 'Cargo.toml');
  if (!existsSync(cargoTomlPath)) {
    fail(`Could not find ${cargoTomlPath} to read version.`);
  }

  const text = readFileSync(cargoTomlPath, 'utf8');
  const match = text.match(/^version\s*=\s*"([^"]+)"\s*$/m);
  if (!match) {
    fail(`Could not parse version from ${cargoTomlPath}.`);
  }

  return match[1];
}

function cleanup(path) {
  if (!existsSync(path)) {
    return;
  }

  rmSync(path, { recursive: true, force: true });
}

function getArgValue(argv, key) {
  const direct = argv.find((arg) => arg.startsWith(`${key}=`));
  if (direct) {
    return direct.slice(`${key}=`.length);
  }

  const index = argv.indexOf(key);
  if (index >= 0 && argv[index + 1]) {
    return argv[index + 1];
  }

  return null;
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    stdio: 'inherit',
    shell: false,
  });

  if (result.status !== 0 && !options.allowFailure) {
    fail(`Command failed: ${command} ${args.join(' ')}`);
  }
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
