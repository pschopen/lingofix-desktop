#!/usr/bin/env node

import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync, copyFileSync } from 'node:fs';
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
const rwDmgPath = join(artifactsDir, 'dmg-staging-rw.dmg');
const mountedVolume = '/Volumes/Lingofix Desktop';
const volumeName = 'Lingofix Desktop';
const backupFolderName = 'Manuelle Installation (Backup)';

// Icon layout must match scripts/assets/dmg-background.svg (720x600). The
// app and Applications symlink live inside the backup folder so the manual
// drag-and-drop path is visually and structurally secondary to Install.command.
// The background carries no per-item captions: Finder draws each item's real
// filename below its icon, so baked-in labels would collide with them.
// Item y positions map 1:1 onto the background; the title bar eats ~28px at
// the bottom, so keep the lowest icon + its (possibly two-line) filename well
// clear of the window's bottom edge.
const WINDOW_BOUNDS = { left: 400, top: 100, width: 720, height: 600 };
const ICON_SIZE = 128;
const ICON_POSITIONS = {
  'Install.command': { x: 360, y: 215 },
  [backupFolderName]: { x: 250, y: 452 },
  'README.txt': { x: 470, y: 452 },
};

cleanup(stagingDir);
cleanup(dmgPath);
cleanup(rwDmgPath);
mkdirSync(artifactsDir, { recursive: true });
mkdirSync(stagingDir, { recursive: true });

console.log(`Staging: ${stagingDir}`);
const backupDir = join(stagingDir, backupFolderName);
mkdirSync(backupDir, { recursive: true });
run('ditto', [appPath, join(backupDir, 'Lingofix Desktop.app')]);
symlinkSync('/Applications', join(backupDir, 'Applications'));

const installScript = join(repoRoot, 'scripts', 'install-mac.sh');
if (existsSync(installScript)) {
  const destScript = join(stagingDir, 'Install.command');
  run('ditto', [installScript, destScript]);
  run('chmod', ['+x', destScript]);
  console.log('Added Install.command to DMG');
}

const readmeContent = `Lingofix Desktop - Installation
================================

Bitte Doppelklick auf "Install.command".
Falls macOS beim ersten Öffnen warnt: mit der rechten Maustaste (oder
Ctrl-Klick) auf "Install.command" klicken -> "Öffnen" waehlen -> "Öffnen"
bestätigen. Das Script installiert die App und fragt dabei automatisch
nach deinem Mac-Passwort. Kein weiterer Schritt nötig.

--------------------------------------------------------------------
Nur falls "Install.command" bei dir nicht funktioniert (Backup):
--------------------------------------------------------------------
  1. Ordner "${backupFolderName}" öffnen.
  2. "Lingofix Desktop.app" in den "Applications"-Ordner ziehen.
  3. Falls macOS beim Start warnt: Systemeinstellungen -> Datenschutz &
     Sicherheit -> ganz unten auf "Trotzdem öffnen" klicken (erscheint erst,
     nachdem einmal versucht wurde, die App zu öffnen).
`;
const readmePath = join(stagingDir, 'README.txt');
writeFileSync(readmePath, readmeContent, 'utf8');
console.log('Added README.txt to DMG');

const backgroundAsset = join(repoRoot, 'scripts', 'assets', 'dmg-background.tiff');
const volumeIconAsset = join(repoRoot, 'tauri', 'icons', 'icon.icns');
const hasBackground = existsSync(backgroundAsset);
const hasVolumeIcon = existsSync(volumeIconAsset);

if (hasBackground) {
  const backgroundDir = join(stagingDir, '.background');
  mkdirSync(backgroundDir, { recursive: true });
  copyFileSync(backgroundAsset, join(backgroundDir, 'background.tiff'));
  console.log('Added DMG background image');
}

if (hasVolumeIcon) {
  copyFileSync(volumeIconAsset, join(stagingDir, '.VolumeIcon.icns'));
  console.log('Added DMG volume icon');
}

// Prevents Spotlight from indexing the mounted volume, which otherwise can
// hold an exclusive lock on it for a few seconds and make hdiutil detach fail.
writeFileSync(join(stagingDir, '.metadata_never_index'), '');

console.log(`Creating writable DMG: ${rwDmgPath}`);
const sizeMb = getFolderSizeMb(stagingDir) + 200;
run('hdiutil', [
  'create',
  '-volname', volumeName,
  '-srcfolder', stagingDir,
  '-format', 'UDRW',
  '-fs', 'HFS+',
  '-size', `${sizeMb}m`,
  '-ov',
  rwDmgPath,
]);

console.log('Mounting writable DMG to arrange layout');
run('hdiutil', ['attach', rwDmgPath, '-mountpoint', mountedVolume]);

try {
  if (hasVolumeIcon) {
    run('SetFile', ['-a', 'V', join(mountedVolume, '.VolumeIcon.icns')]);
  }

  if (hasBackground) {
    arrangeFinderWindow();
  }

  // Finder consumes .VolumeIcon.icns into the volume's icon resource as
  // part of the window "update" above and clears the custom-icon flag in
  // the process, so it must be (re-)applied last to make it stick.
  if (hasVolumeIcon) {
    run('SetFile', ['-a', 'C', mountedVolume]);
  }
} finally {
  detachWithRetry(mountedVolume);
}

console.log(`Converting to compressed DMG: ${dmgPath}`);
run('hdiutil', ['convert', rwDmgPath, '-format', 'UDZO', '-ov', '-o', dmgPath]);
cleanup(rwDmgPath);

console.log(`Verifying DMG: ${dmgPath}`);
run('hdiutil', ['verify', dmgPath]);

console.log(`Mounting DMG for signature verification: ${dmgPath}`);
run('hdiutil', ['attach', dmgPath, '-nobrowse', '-quiet', '-mountpoint', mountedVolume]);

try {
  const mountedApp = join(mountedVolume, backupFolderName, 'Lingofix Desktop.app');
  if (!existsSync(mountedApp)) {
    fail(`Expected app not found in mounted DMG: ${mountedApp}`);
  }

  const symlinkTarget = join(mountedVolume, backupFolderName, 'Applications');
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

function arrangeFinderWindow() {
  console.log('Arranging Finder window, icons and background');
  const right = WINDOW_BOUNDS.left + WINDOW_BOUNDS.width;
  const bottom = WINDOW_BOUNDS.top + WINDOW_BOUNDS.height;

  const positionLines = Object.entries(ICON_POSITIONS)
    .map(([name, pos]) => `    set position of item "${name}" of container window to {${pos.x}, ${pos.y}}`)
    .join('\n');

  const script = `
tell application "Finder"
  tell disk "${volumeName}"
    open
    delay 1
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {${WINDOW_BOUNDS.left}, ${WINDOW_BOUNDS.top}, ${right}, ${bottom}}
    set theViewOptions to the icon view options of container window
    set arrangement of theViewOptions to not arranged
    set icon size of theViewOptions to ${ICON_SIZE}
    set text size of theViewOptions to 12
    set background picture of theViewOptions to file ".background:background.tiff"
${positionLines}
    update without registering applications
    delay 1
    close
  end tell
end tell
`;

  const result = spawnSync('osascript', [], { input: script, encoding: 'utf8' });
  if (result.status !== 0) {
    console.error(`Warning: Finder layout script failed, continuing without custom layout.\n${result.stderr || ''}`);
  }
}

function detachWithRetry(mountpoint, attempts = 10) {
  for (let i = 0; i < attempts; i += 1) {
    const result = spawnSync('hdiutil', ['detach', mountpoint, '-quiet'], { stdio: 'inherit' });
    if (result.status === 0) {
      return;
    }
    // Spotlight (mdworker) can briefly hold an exclusive lock right after
    // mounting; unmounting the filesystem first usually clears it.
    spawnSync('diskutil', ['unmount', mountpoint], { stdio: 'ignore' });
    spawnSync('sleep', ['2']);
  }

  const forced = spawnSync('hdiutil', ['detach', mountpoint, '-force', '-quiet'], { stdio: 'inherit' });
  if (forced.status === 0) {
    return;
  }

  fail(`Could not detach ${mountpoint} after ${attempts} attempts.`);
}

function getFolderSizeMb(path) {
  const result = spawnSync('du', ['-sm', path], { encoding: 'utf8' });
  if (result.status !== 0) {
    fail(`du -sm failed for ${path}: ${result.stderr || 'unknown error'}`);
  }
  const match = (result.stdout || '').match(/^(\d+)/);
  if (!match) {
    fail(`Could not parse folder size for ${path}`);
  }
  return parseInt(match[1], 10);
}

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

  const binaryPath = join(appPath, 'Contents', 'MacOS', 'lingofix-desktop');
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
