import { Language, t } from '../i18n';

interface TranslationResultProps {
  original: string;
  translated: string;
  lang: Language;
  isDarkMode?: boolean;
}

/**
 * Translation mode's counterpart to the inline word-diff view: for a full-text
 * replacement, red/green diff markup is unreadable, so this shows original and
 * translation side by side (stacked on narrow windows) instead (see
 * docs/plans/translation-mode.md Phase 5d).
 */
export function TranslationResult({ original, translated, lang, isDarkMode = false }: TranslationResultProps) {
  return (
    <div
      className={`w-full h-full overflow-y-auto grid grid-cols-1 md:grid-cols-2 transition-colors duration-200 ${
        isDarkMode ? 'bg-surface-800 text-surface-100' : 'bg-white text-surface-800'
      }`}
    >
      <div className={`px-5 py-4 md:border-r ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
        <p className={`text-sm font-medium mb-2 ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>
          {t('translation.original_label', lang)}
        </p>
        <div className="font-sans text-base leading-relaxed whitespace-pre-wrap">{original}</div>
      </div>
      <div className={`px-5 py-4 border-t md:border-t-0 ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
        <p className={`text-sm font-medium mb-2 ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>
          {t('translation.result_label', lang)}
        </p>
        <div className="font-sans text-base leading-relaxed whitespace-pre-wrap">{translated}</div>
      </div>
    </div>
  );
}
