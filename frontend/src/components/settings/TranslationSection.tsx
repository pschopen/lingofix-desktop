import { Settings, TranslationPromptPreset } from '../../types';
import { EU_LANGUAGE_CODES, LANGUAGE_LABELS, Language, t } from '../../i18n';
import { FieldGroup, SelectField } from './shared';
import { PresetDialogMode, PromptPresetEditor } from './PromptPresetEditor';

const OTHER_LANGUAGE_SENTINEL = '__other__';

interface TranslationSectionProps {
  formData: Settings;
  isDarkMode: boolean;
  lang: Language;
  menuBoundaryRef: React.RefObject<HTMLElement | null>;
  onTargetLanguageChange: (value: string) => void;
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
  onTargetLanguageChange,
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
  const isKnownLanguage = EU_LANGUAGE_CODES.includes(targetLanguage as Language);
  const activePreset = visiblePresets.find((preset) => preset.id === activePresetId);

  return (
    <>
      <FieldGroup label={t('mode.target_language', lang)} isDarkMode={isDarkMode}>
        <SelectField
          value={isKnownLanguage ? targetLanguage : OTHER_LANGUAGE_SENTINEL}
          onChange={(nextValue) => {
            if (nextValue === OTHER_LANGUAGE_SENTINEL) {
              onTargetLanguageChange('');
              return;
            }
            onTargetLanguageChange(nextValue);
          }}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {EU_LANGUAGE_CODES.map((language) => (
            <option key={language} value={language}>
              {LANGUAGE_LABELS[language]}
            </option>
          ))}
          <option value={OTHER_LANGUAGE_SENTINEL}>{t('mode.target_language.other', lang)}</option>
        </SelectField>

        {!isKnownLanguage && (
          <input
            type="text"
            value={targetLanguage}
            onChange={(e) => onTargetLanguageChange(e.target.value)}
            placeholder={t('mode.target_language.other_placeholder', lang)}
            className={`input !text-base mt-2 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
          />
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

      <p className={`text-sm ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>
        {t('settings.translation.context_hint', lang)}
      </p>
    </>
  );
}
