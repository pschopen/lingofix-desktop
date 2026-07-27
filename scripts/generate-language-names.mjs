#!/usr/bin/env node
// Generates frontend/src/languageNames.ts and tauri/src/language_names.rs from Node's
// built-in Intl.DisplayNames (CLDR data), so the "what is {targetLanguage} called in
// {uiLanguage}?" table is generated once and kept byte-identical between the Rust and
// TypeScript sides — those two sides fill the same {lang} placeholder into
// independently-maintained but textually-matching default translation prompts, so any
// drift between them would make the "is this still the default prompt?" comparison in
// main.rs silently stop matching.
//
// Run with: node scripts/generate-language-names.mjs

import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const LANGUAGE_CODES = [
  'bg', 'cs', 'da', 'de', 'el', 'en', 'es', 'et', 'fi', 'fr', 'ga', 'hr',
  'hu', 'it', 'lt', 'lv', 'mt', 'nl', 'pl', 'pt', 'ro', 'sk', 'sl', 'sv',
];

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');

/** @type {Record<string, Record<string, string>>} */
const table = {};
for (const uiLang of LANGUAGE_CODES) {
  const displayNames = new Intl.DisplayNames([uiLang], { type: 'language' });
  /** @type {Record<string, string>} */
  const row = {};
  for (const target of LANGUAGE_CODES) {
    const name = displayNames.of(target);
    if (!name) {
      throw new Error(`Intl.DisplayNames could not resolve "${target}" for UI language "${uiLang}"`);
    }
    row[target] = name;
  }
  table[uiLang] = row;
}

function tsQuote(value) {
  return `'${value.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
}

function rustQuote(value) {
  return `"${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

const tsHeader = `// GENERATED FILE — do not edit by hand.
// Regenerate with: node scripts/generate-language-names.mjs
// (edit scripts/generate-language-names.mjs instead, then re-run it)

import type { Language } from './i18n';

/** localized name of every EU_LANGUAGE_CODES language, keyed by UI language then target language code. */
export const LANGUAGE_DISPLAY_NAMES: Record<Language, Record<Language, string>> = {
`;

const tsRows = LANGUAGE_CODES.map((uiLang) => {
  const entries = LANGUAGE_CODES.map((target) => `    ${target}: ${tsQuote(table[uiLang][target])},`).join('\n');
  return `  ${uiLang}: {\n${entries}\n  },`;
}).join('\n');

const tsFooter = `
};

/**
 * Localized display name of \`targetLanguage\` as it would be shown to a \`uiLang\` speaker.
 * Known EU language codes resolve via the generated table above; free text (a
 * user-typed "other language") is returned exactly as given, trimmed.
 */
export function languageDisplayName(uiLang: Language, targetLanguage: string): string {
  const trimmed = targetLanguage.trim();
  const lower = trimmed.toLowerCase();
  const row = LANGUAGE_DISPLAY_NAMES[uiLang] as Record<string, string | undefined>;
  return row[lower] ?? trimmed;
}
`;

const tsOutput = tsHeader + tsRows + tsFooter;
writeFileSync(path.join(repoRoot, 'frontend/src/languageNames.ts'), tsOutput, 'utf8');

const rustHeader = `// GENERATED FILE — do not edit by hand.
// Regenerate with: node scripts/generate-language-names.mjs
// (edit scripts/generate-language-names.mjs instead, then re-run it)

/// Localized display name of \`target_language\` as it would be shown to a \`ui_lang\`
/// speaker. Known EU language codes resolve via the generated tables below; free text (a
/// user-typed "other language") is returned exactly as given, trimmed. Must stay
/// byte-identical to languageDisplayName in frontend/src/languageNames.ts (both are
/// generated from the same Intl.DisplayNames data by generate-language-names.mjs) so the
/// two sides fill the same "{lang}" placeholder identically into the default translation
/// prompts.
pub fn language_display_name(ui_lang: &str, target_language: &str) -> String {
    let trimmed = target_language.trim();
    let lower = trimmed.to_ascii_lowercase();
    let table: &[(&str, &str)] = match ui_lang {
`;

const rustMatchArms = LANGUAGE_CODES.map((uiLang) => `        "${uiLang}" => &${uiLang.toUpperCase()}_NAMES,`).join('\n');

const rustMatchFooter = `
        _ => &EN_NAMES,
    };
    table
        .iter()
        .find(|(code, _)| *code == lower)
        .map(|(_, name)| name.to_string())
        .unwrap_or_else(|| trimmed.to_string())
}
`;

const rustTables = LANGUAGE_CODES.map((uiLang) => {
  const entries = LANGUAGE_CODES.map((target) => `    (${rustQuote(target)}, ${rustQuote(table[uiLang][target])}),`).join('\n');
  return `const ${uiLang.toUpperCase()}_NAMES: [(&str, &str); ${LANGUAGE_CODES.length}] = [\n${entries}\n];`;
}).join('\n\n');

const rustOutput = `${rustHeader}${rustMatchArms}${rustMatchFooter}\n${rustTables}\n`;
writeFileSync(path.join(repoRoot, 'tauri/src/language_names.rs'), rustOutput, 'utf8');

console.log('Wrote frontend/src/languageNames.ts and tauri/src/language_names.rs');
