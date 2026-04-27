import { describe, expect, it, vi, beforeEach } from 'vitest';
import {
  EU_LANGUAGE_CODES,
  defaultCustomPrompt,
  defaultSystemPrompt,
  detectLanguage,
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
});
