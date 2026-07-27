import { useMemo, useState } from 'react';
import { Settings, TranslationPromptPreset } from '../../types';
import { EU_LANGUAGE_CODES, Language, languageDisplayName, t } from '../../i18n';
import { FieldGroup, SelectField, ToggleRow } from './shared';
import { PresetDialogMode, PromptPresetEditor } from './PromptPresetEditor';

const OTHER_LANGUAGE_SENTINEL = '__other__';

interface TranslationSectionProps {
  formData: Settings;
  isDarkMode: boolean;
  lang: Language;
  menuBoundaryRef: React.RefObject<HTMLElement | null>;
  onToggleEnabled: () => void;
  onTargetLanguageChange: (value: string) => void;
  // Permanently adds a free-text language to translation.custom_languages and selects it
  // (docs/plans/translation-polish.md AP 3 follow-up). Adding is only possible here in
  // Settings; the main window can only pick from already-added languages.
  onAddLanguage: (value: string) => void;
  onRemoveLanguage: (value: string) => void;
  visiblePresets: TranslationPromptPreset[];
  activePresetId: string;
  activePresetName: string;
  presetMessage: string;
  presetDialogMode: PresetDialogMode;
  presetDialogValue: string;
  onPresetDialogValueChange: (value: string) => void;
  onPresetSelect: (id: string) => void;
  onPresetCreate: () => void;
  onPresetDuplicate: () => void;
  onPresetRename: () => void;
  onPresetDelete: () => void;
  onPresetDialogConfirm: () => void;
  onPresetDialogCancel: () => void;
  onMainPromptChange: (value: string) => void;
  onFootnotePromptChange: (value: string) => void;
}

export function TranslationSection({
  formData,
  isDarkMode,
  lang,
  menuBoundaryRef,
  onToggleEnabled,
  onTargetLanguageChange,
  onAddLanguage,
  onRemoveLanguage,
  visiblePresets,
  activePresetId,
  activePresetName,
  presetMessage,
  presetDialogMode,
  presetDialogValue,
  onPresetDialogValueChange,
  onPresetSelect,
  onPresetCreate,
  onPresetDuplicate,
  onPresetRename,
  onPresetDelete,
  onPresetDialogConfirm,
  onPresetDialogCancel,
  onMainPromptChange,
  onFootnotePromptChange,
}: TranslationSectionProps) {
  const targetLanguage = formData.translation.target_language;
  const customLanguages = formData.translation.custom_languages;
  const isKnownLanguage = EU_LANGUAGE_CODES.includes(targetLanguage as Language);
  const isRememberedCustomLanguage = customLanguages.some(
    (entry) => entry.trim().toLowerCase() === targetLanguage.trim().toLowerCase(),
  );
  const isSelectableLanguage = isKnownLanguage || isRememberedCustomLanguage;
  const activePreset = visiblePresets.find((preset) => preset.id === activePresetId);

  // "Add a new language" is its own explicit action, decoupled from the select's current
  // value: opening it must not change the active target language, and typing must not
  // either — only pressing the Add button (or Enter) does (docs/plans/translation-polish.md
  // AP 3 follow-up). Lazily defaults to open, pre-filled, if the loaded target_language is
  // itself neither a EU_LANGUAGE_CODES entry nor already remembered — a legacy/stray value
  // that predates custom_languages tracking — so it isn't silently invisible.
  const [isAddingLanguage, setIsAddingLanguage] = useState(!isSelectableLanguage);
  const [newLanguageInput, setNewLanguageInput] = useState(isSelectableLanguage ? '' : targetLanguage);

  const closeAddLanguage = () => {
    setIsAddingLanguage(false);
    setNewLanguageInput('');
  };

  const confirmAddLanguage = () => {
    const trimmed = newLanguageInput.trim();
    if (!trimmed) {
      return;
    }
    onAddLanguage(trimmed);
    closeAddLanguage();
  };

  // EU languages sorted by their name in the current UI language (see
  // docs/plans/translation-polish.md AP 4).
  const sortedEuLanguageCodes = useMemo(
    () => [...EU_LANGUAGE_CODES].sort((a, b) => languageDisplayName(lang, a).localeCompare(languageDisplayName(lang, b), lang)),
    [lang],
  );

  return (
    <>
      {/* The translation feature is experimental and off by default; the rest of this
          section (and the mode switch in the main window) only appears once enabled here. */}
      <ToggleRow
        label={t('settings.translation.enable', lang)}
        checked={formData.translation_enabled}
        onChange={onToggleEnabled}
        isDarkMode={isDarkMode}
      />

      {formData.translation_enabled && (
        <>
          <FieldGroup label={t('mode.target_language', lang)} isDarkMode={isDarkMode}>
            <SelectField
              value={isSelectableLanguage ? targetLanguage : OTHER_LANGUAGE_SENTINEL}
              onChange={(nextValue) => {
                if (nextValue === OTHER_LANGUAGE_SENTINEL) {
                  setIsAddingLanguage(true);
                  setNewLanguageInput('');
                  return;
                }
                closeAddLanguage();
                onTargetLanguageChange(nextValue);
              }}
              menuBoundaryRef={menuBoundaryRef}
              isDarkMode={isDarkMode}
              removableValues={customLanguages}
              onRemoveOption={onRemoveLanguage}
              removeLabel={(value) => t('mode.target_language.remove', lang).replace('{lang}', value)}
            >
              {customLanguages.map((language) => (
                <option key={language} value={language}>
                  {language}
                </option>
              ))}
              {sortedEuLanguageCodes.map((language) => (
                <option key={language} value={language}>
                  {languageDisplayName(lang, language)}
                </option>
              ))}
              <option value={OTHER_LANGUAGE_SENTINEL}>{t('mode.target_language.other', lang)}</option>
            </SelectField>

            {isAddingLanguage && (
              <div className="mt-2 flex gap-2">
                <input
                  type="text"
                  value={newLanguageInput}
                  onChange={(e) => setNewLanguageInput(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      confirmAddLanguage();
                    } else if (e.key === 'Escape') {
                      e.preventDefault();
                      closeAddLanguage();
                    }
                  }}
                  placeholder={t('mode.target_language.other_placeholder', lang)}
                  autoFocus
                  className={`input flex-1 min-w-0 !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                />
                <button
                  type="button"
                  onClick={confirmAddLanguage}
                  disabled={!newLanguageInput.trim()}
                  className="btn-secondary !text-sm !px-3 flex-shrink-0 disabled:opacity-50"
                >
                  {t('mode.target_language.add', lang)}
                </button>
                <button
                  type="button"
                  onClick={closeAddLanguage}
                  className="btn-secondary !text-sm !px-3 flex-shrink-0"
                >
                  {t('settings.cancel', lang)}
                </button>
              </div>
            )}
          </FieldGroup>

          <PromptPresetEditor
            label={t('settings.translation.prompt_presets', lang)}
            presets={visiblePresets}
            activePresetId={activePresetId}
            activePresetName={activePresetName}
            onSelect={onPresetSelect}
            onCreate={onPresetCreate}
            onDuplicate={onPresetDuplicate}
            onRename={onPresetRename}
            onDelete={onPresetDelete}
            fields={[
              {
                key: 'main_prompt',
                label: t('settings.translation.main_prompt.label', lang),
                value: activePreset?.main_prompt ?? '',
                onChange: onMainPromptChange,
              },
              {
                key: 'footnote_prompt',
                label: t('settings.translation.footnote_prompt.label', lang),
                hint: t('settings.translation.footnote_prompt.hint', lang),
                value: activePreset?.footnote_prompt ?? '',
                onChange: onFootnotePromptChange,
              },
            ]}
            message={presetMessage}
            dialogMode={presetDialogMode}
            dialogValue={presetDialogValue}
            onDialogValueChange={onPresetDialogValueChange}
            onDialogConfirm={onPresetDialogConfirm}
            onDialogCancel={onPresetDialogCancel}
            isDarkMode={isDarkMode}
            lang={lang}
            menuBoundaryRef={menuBoundaryRef}
          />
        </>
      )}
    </>
  );
}
