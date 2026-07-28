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

// Icon layout must match scripts/assets/dmg-background.svg (720x640).
// The app is not notarized, so the only installation path that works on
// current macOS (Sequoia/Tahoe) is: drag the app onto the Applications
// symlink, then release it once via System Settings on first launch. The
// background image spells those steps out; here we just place the two icons
// inside the "Schritt 1" card where the background's arrow points. The
// background carries no per-item captions: Finder draws each item's real
// filename below its icon, so baked-in labels would collide with them.
const WINDOW_BOUNDS = { left: 400, top: 100, width: 720, height: 640 };
const ICON_SIZE = 128;
const ICON_POSITIONS = {
  'Lingofix Desktop.app': { x: 230, y: 205 },
  'Applications': { x: 500, y: 205 },
};

// A previous build (or a previous, redundant invocation in the same CI job)
// may have left "/Volumes/Lingofix Desktop" mounted; hdiutil create would then
// fail with "Resource busy". Detach any stale volume before starting.
ensureUnmounted(mountedVolume);

cleanup(stagingDir);
cleanup(dmgPath);
cleanup(rwDmgPath);
mkdirSync(artifactsDir, { recursive: true });
mkdirSync(stagingDir, { recursive: true });

console.log(`Staging: ${stagingDir}`);
run('ditto', [appPath, join(stagingDir, 'Lingofix Desktop.app')]);
symlinkSync('/Applications', join(stagingDir, 'Applications'));

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
// The device node is captured here because detaching has to target the image
// device, not the mountpoint: once anything unmounts the volume, the mountpoint
// is gone while the image itself is still attached, and `hdiutil detach
// /Volumes/...` can then only fail.
const rwDevice = parseAttachDevice(
  run('hdiutil', ['attach', rwDmgPath, '-mountpoint', mountedVolume], { capture: true }),
);

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
  detachWithRetry(mountedVolume, rwDevice);
}

console.log(`Converting to compressed DMG: ${dmgPath}`);
run('hdiutil', ['convert', rwDmgPath, '-format', 'UDZO', '-ov', '-o', dmgPath]);
cleanup(rwDmgPath);

console.log(`Verifying DMG: ${dmgPath}`);
run('hdiutil', ['verify', dmgPath]);

console.log(`Mounting DMG for signature verification: ${dmgPath}`);
const verifyDevice = parseAttachDevice(
  run('hdiutil', ['attach', dmgPath, '-nobrowse', '-mountpoint', mountedVolume], { capture: true }),
);

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
  detachWithRetry(mountedVolume, verifyDevice, { allowFailure: true, attempts: 5 });
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

function detachWithRetry(mountpoint, device, options = {}) {
  const attempts = options.attempts ?? 10;
  // Detaching the device is what actually releases the image; the mountpoint is
  // only a fallback for a stale volume whose device we could not resolve.
  const target = device ?? mountpoint;

  // Finder writes .DS_Store lazily after the layout script closes its window,
  // so flush pending writes before the first attempt rather than racing them.
  spawnSync('sync', [], { stdio: 'ignore' });

  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    if (!isAttached(device, mountpoint)) {
      return true;
    }

    const result = spawnSync('hdiutil', ['detach', target], { encoding: 'utf8' });
    if (result.status === 0) {
      return true;
    }

    // `diskutil unmount force` below tears the volume down without detaching
    // the image, and hdiutil then reports "no such file or directory" for the
    // mountpoint on every later attempt. A non-zero status therefore only
    // counts as a failure while the image is genuinely still attached.
    if (!isAttached(device, mountpoint)) {
      return true;
    }

    console.warn(`Detach attempt ${attempt}/${attempts} failed: ${describeFailure(result)}`);

    if (attempt === 1) {
      // Named holders make the CI log actionable; without this the script used
      // to fail after ~30 silent seconds with no hint at what was holding on.
      reportVolumeHolders(mountpoint);
      // Spotlight (mds/mdworker) and Finder are the usual holders on GitHub
      // Actions runners: the former indexes the volume, the latter keeps locks
      // from the AppleScript layout pass.
      spawnSync('mdutil', ['-i', 'off', mountpoint], { stdio: 'ignore' });
      spawnSync('killall', ['Finder'], { stdio: 'ignore' });
    }

    // Release any file-system-level locks (fseventsd, etc.).
    spawnSync('diskutil', ['unmount', 'force', mountpoint], { stdio: 'ignore' });
    spawnSync('sleep', ['3']);
  }

  const forced = spawnSync('hdiutil', ['detach', target, '-force'], { encoding: 'utf8' });
  if (forced.status === 0 || !isAttached(device, mountpoint)) {
    return true;
  }

  const message = `Could not detach ${target} after ${attempts} attempts: ${describeFailure(forced)}`;
  if (options.allowFailure) {
    console.warn(`Warning: ${message}`);
    return false;
  }

  fail(message);
}

// Whether the disk image is still attached. Checked against `hdiutil info`
// rather than the mountpoint, because an unmounted-but-attached image still
// blocks `hdiutil convert`.
function isAttached(device, mountpoint) {
  if (device) {
    const info = spawnSync('hdiutil', ['info'], { encoding: 'utf8' });
    if (info.status === 0) {
      const escaped = device.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      return new RegExp(`^${escaped}(s\\d+)?\\b`, 'm').test(info.stdout || '');
    }
  }

  return existsSync(mountpoint);
}

function reportVolumeHolders(mountpoint) {
  const result = spawnSync('lsof', ['+f', '--', mountpoint], { encoding: 'utf8' });
  const output = (result.stdout || '').trim();
  console.warn(output
    ? `Processes still holding ${mountpoint}:\n${output}`
    : `No process reported holding ${mountpoint}.`);
}

function describeFailure(result) {
  if (result.error) {
    return result.error.message;
  }
  return (result.stderr || result.stdout || '').trim() || `exit code ${result.status}`;
}

// `hdiutil attach` lists the image's devices, whole disk first:
//   /dev/disk4          GUID_partition_scheme
//   /dev/disk4s1        Apple_HFS               /Volumes/Lingofix Desktop
function parseAttachDevice(output) {
  const line = (output || '').split('\n').find((entry) => entry.startsWith('/dev/'));
  if (!line) {
    return null;
  }
  return line.trim().split(/\s+/)[0].replace(/s\d+$/, '');
}

function deviceForMountpoint(mountpoint) {
  const result = spawnSync('diskutil', ['info', mountpoint], { encoding: 'utf8' });
  if (result.status !== 0) {
    return null;
  }
  const match = (result.stdout || '').match(/Device Node:\s*(\/dev\/\S+)/);
  return match ? match[1].replace(/s\d+$/, '') : null;
}

function ensureUnmounted(mountpoint) {
  if (!existsSync(mountpoint)) {
    return;
  }
  console.log(`Detaching stale volume before build: ${mountpoint}`);
  detachWithRetry(mountpoint, deviceForMountpoint(mountpoint));
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
    stdio: options.capture ? ['ignore', 'pipe', 'inherit'] : 'inherit',
    encoding: 'utf8',
    shell: false,
  });

  if (options.capture && result.stdout) {
    process.stdout.write(result.stdout);
  }

  if (result.status !== 0 && !options.allowFailure) {
    fail(`Command failed: ${command} ${args.join(' ')}`);
  }

  return result.stdout ?? '';
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
