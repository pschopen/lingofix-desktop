import { Loader2 } from 'lucide-react';
import {
  Settings,
  CustomPromptPreset,
  DocxSettings,
  DOCX_COMPARE_MODES,
  CITATION_NORMALIZATION_MODES,
} from '../../types';
import { EU_LANGUAGE_CODES, LANGUAGE_LABELS, Language, t } from '../../i18n';
import { FieldGroup, SelectField, ToggleRow } from './shared';
import { PresetDialogMode, PromptPresetEditor } from './PromptPresetEditor';

interface CompareAccessStatus {
  ok: boolean;
  message: string;
  details: string;
}

interface CorrectionSectionProps {
  formData: Settings;
  isDarkMode: boolean;
  lang: Language;
  isMac: boolean;
  menuBoundaryRef: React.RefObject<HTMLElement | null>;
  onCorrectionLanguageChange: (language: Language) => void;
  onDocxSettingChange: <K extends keyof DocxSettings>(key: K, value: DocxSettings[K]) => void;
  isCheckingCompareAccess: boolean;
  compareAccessStatus: CompareAccessStatus | null;
  onCompareAccessCheck: () => Promise<void>;
  // Correction prompt presets
  visiblePresets: CustomPromptPreset[];
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
  onCustomPromptChange: (value: string) => void;
  activePresetName: string;
}

export function CorrectionSection({
  formData,
  isDarkMode,
  lang,
  isMac,
  menuBoundaryRef,
  onCorrectionLanguageChange,
  onDocxSettingChange,
  isCheckingCompareAccess,
  compareAccessStatus,
  onCompareAccessCheck,
  visiblePresets,
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
  onCustomPromptChange,
  activePresetName,
}: CorrectionSectionProps) {
  // The correction preset's own value, not formData.custom_prompt: that shared field is
  // only guaranteed to hold the correction prompt while mode === 'correction' (translation
  // mode owns it while active — see SettingsModal's mode guard).
  const activePreset = visiblePresets.find((preset) => preset.id === formData.active_custom_prompt_preset_id);

  return (
    <>
      <FieldGroup label={t('settings.correction_language', lang)} isDarkMode={isDarkMode}>
        <SelectField
          value={formData.correction_language}
          onChange={(nextValue) => onCorrectionLanguageChange(nextValue as Language)}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {EU_LANGUAGE_CODES.map((language) => (
            <option key={language} value={language}>
              {LANGUAGE_LABELS[language]}
            </option>
          ))}
        </SelectField>
      </FieldGroup>

      <PromptPresetEditor
        label={t('settings.prompt_presets', lang)}
        presets={visiblePresets}
        activePresetId={formData.active_custom_prompt_preset_id}
        activePresetName={activePresetName}
        onSelect={onPresetSelect}
        onCreate={onPresetCreate}
        onDuplicate={onPresetDuplicate}
        onRename={onPresetRename}
        onDelete={onPresetDelete}
        fields={[
          {
            key: 'value',
            value: activePreset?.value ?? '',
            onChange: onCustomPromptChange,
            placeholder: t('settings.prompt.hint', lang),
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

      {/* Compare Mode */}
      <FieldGroup label={t('settings.docx.compare_mode', lang)} isDarkMode={isDarkMode}>
        <SelectField
          value={formData.docx.compare_mode}
          onChange={(nextValue) => onDocxSettingChange('compare_mode', nextValue as DocxSettings['compare_mode'])}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {DOCX_COMPARE_MODES.map((mode) => (
            <option key={mode} value={mode}>
              {mode === 'openxml'
                ? t('settings.docx.compare_mode.openxml', lang)
                : mode === 'word-native'
                  ? t('settings.docx.compare_mode.word_native', lang)
                  : t('settings.docx.compare_mode.libreoffice_uno', lang)}
            </option>
          ))}
        </SelectField>
      </FieldGroup>

      {formData.docx.compare_mode === 'openxml' && (
        <div className={`rounded-lg border px-4 py-3 ${isDarkMode ? 'border-amber-800/60 bg-amber-950/30' : 'border-amber-200 bg-amber-50'}`}>
          <p className={`text-sm ${isDarkMode ? 'text-amber-200' : 'text-amber-800'}`}>
            {t('settings.docx.openxml.warning', lang)}
          </p>
        </div>
      )}

      {(formData.docx.compare_mode === 'word-native' || formData.docx.compare_mode === 'libreoffice-uno') && (
        <div className={`rounded-lg border px-4 py-3 ${isDarkMode ? 'border-surface-700 bg-surface-800/70' : 'border-surface-200 bg-surface-50'}`}>
          <div className="flex items-start justify-between gap-3">
            <p className={`text-sm flex-1 ${isDarkMode ? 'text-surface-300' : 'text-surface-700'}`}>
              {formData.docx.compare_mode === 'libreoffice-uno'
                ? t('settings.docx.libreoffice_check.hint', lang)
                : isMac
                  ? t('settings.docx.word_check.hint', lang)
                  : t('settings.docx.word_check.hint_non_macos', lang)}
            </p>
            <button
              type="button"
              onClick={() => void onCompareAccessCheck()}
              disabled={isCheckingCompareAccess}
              className="btn-secondary !text-base !whitespace-nowrap shrink-0"
            >
              {isCheckingCompareAccess ? (
                <Loader2 className="animate-spin" size={14} />
              ) : null}
              {formData.docx.compare_mode === 'libreoffice-uno'
                ? t('settings.docx.libreoffice_check.button', lang)
                : t('settings.docx.word_check.button', lang)}
            </button>
          </div>
          {compareAccessStatus && (
            <p className={`mt-2 text-sm whitespace-pre-wrap ${compareAccessStatus.ok ? 'text-emerald-600' : 'text-amber-600'}`}>
              {compareAccessStatus.message}
              {compareAccessStatus.details ? `\n${compareAccessStatus.details}` : ''}
            </p>
          )}
        </div>
      )}

      <ToggleRow
        label={t('settings.docx.restore_non_breaking_spaces', lang)}
        checked={formData.docx.restore_non_breaking_spaces}
        onChange={() => onDocxSettingChange('restore_non_breaking_spaces', !formData.docx.restore_non_breaking_spaces)}
        isDarkMode={isDarkMode}
      />

      <ToggleRow
        label={t('settings.docx.ignore_trailing_paragraph_whitespace', lang)}
        checked={formData.docx.ignore_trailing_paragraph_whitespace}
        onChange={() => onDocxSettingChange('ignore_trailing_paragraph_whitespace', !formData.docx.ignore_trailing_paragraph_whitespace)}
        isDarkMode={isDarkMode}
      />

      <FieldGroup
        label={t('settings.citation_normalization', lang)}
        hint={t('settings.citation_normalization.hint', lang)}
        isDarkMode={isDarkMode}
      >
        <SelectField
          value={formData.docx.citation_normalization}
          onChange={(nextValue) => onDocxSettingChange('citation_normalization', nextValue as DocxSettings['citation_normalization'])}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {CITATION_NORMALIZATION_MODES.map((mode) => (
            <option key={mode} value={mode} className={isDarkMode ? '!bg-surface-700 !text-surface-100' : ''}>
              {t(`settings.citation_normalization.${mode}`, lang)}
            </option>
          ))}
        </SelectField>
      </FieldGroup>
    </>
  );
}
