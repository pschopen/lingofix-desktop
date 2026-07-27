import { describe, expect, it, vi, beforeEach } from 'vitest';
import {
  EU_LANGUAGE_CODES,
  defaultCustomPrompt,
  defaultSystemPrompt,
  defaultTranslationBatchPrompt,
  defaultTranslationFootnotePrompt,
  defaultTranslationMainPrompt,
  defaultTranslationSystemPrompt,
  detectLanguage,
  languageDisplayName,
  normalizeLanguage,
  t,
} from './i18n';

describe('i18n', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('detects german locale', () => {
    vi.spyOn(window.navigator, 'language', 'get').mockReturnValue('de-DE');
    expect(detectLanguage()).toBe('de');
  });

  it('falls back to english locale', () => {
    vi.spyOn(window.navigator, 'language', 'get').mockReturnValue('ja-JP');
    expect(detectLanguage()).toBe('en');
  });

  it('maps supported EU languages', () => {
    expect(normalizeLanguage('fr-FR')).toBe('fr');
    expect(normalizeLanguage('pl_PL')).toBe('pl');
    expect(normalizeLanguage('ga-IE')).toBe('ga');
  });

  it('uses Duden only for german default prompts', () => {
    expect(defaultCustomPrompt('de')).toContain('Duden-Regeln');
    expect(defaultCustomPrompt('fr')).toContain('règles officielles');
    expect(defaultCustomPrompt('fr')).not.toContain('offiziellen Regeln');
    expect(defaultCustomPrompt('en')).toContain('official rules');
    expect(defaultCustomPrompt('en')).not.toContain('offiziellen Regeln');
    for (const language of EU_LANGUAGE_CODES.filter((code) => code !== 'de')) {
      expect(defaultCustomPrompt(language)).not.toContain('Duden-Regeln');
    }
  });

  it('localizes custom and system default prompts for all supported languages', () => {
    for (const language of EU_LANGUAGE_CODES) {
      expect(defaultCustomPrompt(language).trim()).not.toBe('');
      expect(defaultSystemPrompt(language).trim()).not.toBe('');
    }

    expect(defaultSystemPrompt('fr')).toContain('texte corrigé');
    expect(defaultSystemPrompt('fr')).not.toContain('Respond with the corrected text only');
    expect(defaultSystemPrompt('es')).toContain('texto corregido');
    expect(defaultSystemPrompt('es')).not.toContain('Antworte nur mit dem korrigierten Text');
  });

  it('returns translations and falls back for unknown keys', () => {
    expect(t('button.correct', 'de')).toBe('Korrigieren');
    expect(t('button.correct', 'en')).toBe('Correct');
    expect(t('button.correct', 'fr')).toBe('Corriger');
    expect(t('unknown.key', 'en')).toBe('unknown.key');
  });

  it('resolves localized language display names, falling back to free text', () => {
    expect(languageDisplayName('de', 'de')).toBe('Deutsch');
    expect(languageDisplayName('en', 'de')).toBe('German');
    expect(languageDisplayName('fr', 'de')).toBe('allemand');
    expect(languageDisplayName('de', 'Schweizer Hochdeutsch')).toBe('Schweizer Hochdeutsch');
  });

  it('localizes translation prompts for all supported UI languages', () => {
    for (const language of EU_LANGUAGE_CODES) {
      expect(defaultTranslationSystemPrompt(language).trim()).not.toBe('');
      expect(defaultTranslationMainPrompt(language, 'de').trim()).not.toBe('');
      expect(defaultTranslationFootnotePrompt(language, 'de').trim()).not.toBe('');
      expect(defaultTranslationBatchPrompt(language).trim()).not.toBe('');
    }
  });

  it('German translation prompt says "nach Deutsch", not "ins Deutsch"', () => {
    const prompt = defaultTranslationMainPrompt('de', 'de');
    expect(prompt).toContain('nach Deutsch');
    expect(prompt).not.toContain('ins Deutsch');
  });

  it('translation prompt wording follows the UI language, not the target language', () => {
    const enPrompt = defaultTranslationMainPrompt('en', 'de');
    expect(enPrompt).toContain('Translate');
    expect(enPrompt).toContain('German');
    expect(enPrompt).not.toContain('Übersetze');

    const dePrompt = defaultTranslationMainPrompt('de', 'en');
    expect(dePrompt).toContain('Übersetze');
    expect(dePrompt).toContain('Englisch');
    expect(dePrompt).not.toContain('Translate');
  });

  it('footnote prompt extends the main prompt with a footnote-only suffix', () => {
    const main = defaultTranslationMainPrompt('en', 'fr');
    const footnote = defaultTranslationFootnotePrompt('en', 'fr');
    expect(footnote.startsWith(main)).toBe(true);
    expect(footnote.length).toBeGreaterThan(main.length);
  });
});
