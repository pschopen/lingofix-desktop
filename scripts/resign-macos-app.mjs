#!/usr/bin/env node

import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const targetArg = getArgValue(process.argv.slice(2), '--target') ?? 'native';
const appPath = resolvePathForTarget(targetArg);

if (!existsSync(appPath)) {
  fail(`macOS app bundle not found: ${appPath}`);
}

run('codesign', ['--remove-signature', appPath], { allowFailure: true });
run('codesign', ['--force', '--deep', '--sign', '-', appPath]);
run('codesign', ['--verify', '--deep', '--strict', '--verbose=2', appPath]);

console.log(`Re-signed and verified: ${appPath}`);

function resolvePathForTarget(target) {
  const byTarget = {
    native: 'tauri/target/release/bundle/macos/Lingofix Desktop.app',
    'aarch64-apple-darwin': 'tauri/target/aarch64-apple-darwin/release/bundle/macos/Lingofix Desktop.app',
    'x86_64-apple-darwin': 'tauri/target/x86_64-apple-darwin/release/bundle/macos/Lingofix Desktop.app',
  };

  const relativePath = byTarget[target];
  if (!relativePath) {
    fail(`Unsupported --target '${target}'. Expected one of: ${Object.keys(byTarget).join(', ')}`);
  }

  return resolve(process.cwd(), relativePath);
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
