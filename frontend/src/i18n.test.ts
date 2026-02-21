import { describe, expect, it, vi, beforeEach } from 'vitest';
import { detectLanguage, t } from './i18n';

describe('i18n', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('detects german locale', () => {
    vi.spyOn(window.navigator, 'language', 'get').mockReturnValue('de-DE');
    expect(detectLanguage()).toBe('de');
  });

  it('falls back to english locale', () => {
    vi.spyOn(window.navigator, 'language', 'get').mockReturnValue('en-US');
    expect(detectLanguage()).toBe('en');
  });

  it('returns translations and falls back for unknown keys', () => {
    expect(t('button.correct', 'de')).toBe('Korrigieren');
    expect(t('button.correct', 'en')).toBe('Correct');
    expect(t('unknown.key', 'en')).toBe('unknown.key');
  });
});
