import { Loader2, Sparkles } from 'lucide-react';
import {
  Settings,
  DocxSettings,
  EditorSettings,
  FontSize,
  FONT_SIZES,
  SETTINGS_LIMITS,
  DOCX_BATCHING_PARTS,
  REASONING_EFFORTS,
  SPEED_MODES,
  SpeedMode,
  DocxBatchingPart,
  ReasoningEffort,
} from '../../types';
import { EU_LANGUAGE_CODES, LANGUAGE_LABELS, Language, t } from '../../i18n';
import { FieldGroup, SelectField, ToggleRow } from './shared';

interface AdvancedSectionProps {
  formData: Settings;
  setFormData: (updater: (prev: Settings) => Settings) => void;
  isDarkMode: boolean;
  lang: Language;
  menuBoundaryRef: React.RefObject<HTMLElement | null>;

  onDocxSettingChange: <K extends keyof DocxSettings>(key: K, value: DocxSettings[K]) => void;
  onEditorSettingChange: <K extends keyof EditorSettings>(key: K, value: EditorSettings[K]) => void;
  onBatchingPartToggle: (part: DocxBatchingPart) => void;
  onUiLanguageChange: (language: Language) => void;

  isCheckingUpdates: boolean;
  updateCheckMessage: string;
  onCheckUpdates: () => Promise<void>;

  isResettingApp: boolean;
  resetMessage: string;
  resetMessageIsError: boolean;
  onResetApp: () => Promise<void>;
  onRerunWizard: () => Promise<void> | void;
  systemPathMessage: string;
  onOpenTempFolder: () => Promise<void>;
  onOpenSettingsJson: () => Promise<void>;
  onOpenDebugLog: () => Promise<void>;
}

export function AdvancedSection({
  formData,
  setFormData,
  isDarkMode,
  lang,
  menuBoundaryRef,
  onDocxSettingChange,
  onEditorSettingChange,
  onBatchingPartToggle,
  onUiLanguageChange,
  isCheckingUpdates,
  updateCheckMessage,
  onCheckUpdates,
  isResettingApp,
  resetMessage,
  resetMessageIsError,
  onResetApp,
  onRerunWizard,
  systemPathMessage,
  onOpenTempFolder,
  onOpenSettingsJson,
  onOpenDebugLog,
}: AdvancedSectionProps) {
  const isOllama = formData.provider === 'ollama';

  return (
    <>
      <FieldGroup label={`${t('settings.temperature', lang)}: ${formData.temperature}`} isDarkMode={isDarkMode}>
        <input
          type="range"
          min={SETTINGS_LIMITS.temperature.min}
          max={SETTINGS_LIMITS.temperature.max}
          step={SETTINGS_LIMITS.temperature.step}
          value={formData.temperature}
          onChange={(e) => setFormData((prev) => ({ ...prev, temperature: parseFloat(e.target.value) }))}
          className="w-full mt-1"
        />
      </FieldGroup>

      <ToggleRow
        label={t('settings.enable_reasoning', lang)}
        checked={formData.enable_reasoning}
        onChange={() => setFormData((prev) => ({ ...prev, enable_reasoning: !prev.enable_reasoning }))}
        isDarkMode={isDarkMode}
      />

      {formData.enable_reasoning && (
        <div className="pl-4 border-l-2 border-accent-100">
          <FieldGroup label={t('settings.reasoning_effort', lang)} isDarkMode={isDarkMode}>
            <SelectField
              value={formData.reasoning_effort}
              onChange={(nextValue) => setFormData((prev) => ({ ...prev, reasoning_effort: nextValue as ReasoningEffort }))}
              menuBoundaryRef={menuBoundaryRef}
              isDarkMode={isDarkMode}
            >
              {REASONING_EFFORTS.map((effort) => (
                <option key={effort} value={effort}>
                  {t(`settings.reasoning_effort.${effort}`, lang)}
                </option>
              ))}
            </SelectField>
          </FieldGroup>
        </div>
      )}

      <FieldGroup label={t('settings.system_prompt', lang)} hint={t('settings.system_prompt.hint', lang)} isDarkMode={isDarkMode}>
        <textarea
          value={formData.system_prompt}
          onChange={(e) => setFormData((prev) => ({ ...prev, system_prompt: e.target.value }))}
          placeholder={t('settings.system_prompt.placeholder', lang)}
          className={`textarea !text-base h-28 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
        />
      </FieldGroup>

      <FieldGroup label={`${t('settings.docx.chunk_size', lang)}: ${formData.docx.chunk_size}`} isDarkMode={isDarkMode}>
        <input
          type="range"
          min={SETTINGS_LIMITS.chunkSize.min}
          max={SETTINGS_LIMITS.chunkSize.max}
          step={SETTINGS_LIMITS.chunkSize.step}
          value={formData.docx.chunk_size}
          onChange={(e) => onDocxSettingChange('chunk_size', Number(e.target.value))}
          className="w-full mt-1"
        />
      </FieldGroup>

      <FieldGroup label={`${t('settings.editor.chunk_size', lang)}: ${formData.editor.chunk_size}`} isDarkMode={isDarkMode}>
        <input
          type="range"
          min={SETTINGS_LIMITS.chunkSize.min}
          max={SETTINGS_LIMITS.chunkSize.max}
          step={SETTINGS_LIMITS.chunkSize.step}
          value={formData.editor.chunk_size}
          onChange={(e) => onEditorSettingChange('chunk_size', Number(e.target.value))}
          className="w-full mt-1"
        />
      </FieldGroup>

      <ToggleRow
        label={t('settings.docx.batching', lang)}
        checked={formData.docx.enable_batching}
        onChange={() => {
          setFormData((prev) => {
            const nextEnabled = !prev.docx.enable_batching;
            return {
              ...prev,
              docx: {
                ...prev.docx,
                enable_batching: nextEnabled,
                batching_parts: nextEnabled && prev.docx.batching_parts.length === 0
                  ? [...DOCX_BATCHING_PARTS]
                  : prev.docx.batching_parts,
              },
            };
          });
        }}
        isDarkMode={isDarkMode}
      />

      {formData.docx.enable_batching && (
        <div className="pl-4 border-l-2 border-accent-100 space-y-4">
          <FieldGroup label={t('settings.docx.batching_parts', lang)} isDarkMode={isDarkMode}>
            <div className="mt-1 flex flex-wrap gap-1.5">
              {DOCX_BATCHING_PARTS.map((part) => (
                <label
                  key={part}
                  className={`inline-flex items-center gap-1.5 text-sm rounded-md px-1.5 py-0.5 ${isDarkMode ? 'text-surface-200' : 'text-surface-700'}`}
                >
                  <input
                    type="checkbox"
                    checked={formData.docx.batching_parts.includes(part)}
                    onChange={() => onBatchingPartToggle(part)}
                  />
                  <span>{t(`settings.docx.batching_parts.${part}`, lang)}</span>
                </label>
              ))}
            </div>
          </FieldGroup>
          <FieldGroup label={`${t('settings.docx.batch_max_chars', lang)}: ${formData.docx.batch_max_chars}`} isDarkMode={isDarkMode}>
            <input
              type="range"
              min={SETTINGS_LIMITS.batchMaxChars.min}
              max={SETTINGS_LIMITS.batchMaxChars.max}
              step={SETTINGS_LIMITS.batchMaxChars.step}
              value={formData.docx.batch_max_chars}
              onChange={(e) => onDocxSettingChange('batch_max_chars', Number(e.target.value))}
              className="w-full mt-1"
            />
          </FieldGroup>
          <FieldGroup label={`${t('settings.docx.batch_max_paragraphs', lang)}: ${formData.docx.batch_max_paragraphs}`} isDarkMode={isDarkMode}>
            <input
              type="range"
              min={SETTINGS_LIMITS.batchMaxParagraphs.min}
              max={SETTINGS_LIMITS.batchMaxParagraphs.max}
              step={SETTINGS_LIMITS.batchMaxParagraphs.step}
              value={formData.docx.batch_max_paragraphs}
              onChange={(e) => onDocxSettingChange('batch_max_paragraphs', Number(e.target.value))}
              className="w-full mt-1"
            />
          </FieldGroup>
        </div>
      )}

      <ToggleRow
        label={t('settings.docx.cache', lang)}
        checked={formData.docx.enable_cache}
        onChange={() => onDocxSettingChange('enable_cache', !formData.docx.enable_cache)}
        isDarkMode={isDarkMode}
      />

      <FieldGroup
        label={t('settings.docx.speed_mode', lang)}
        hint={t('settings.docx.speed_mode.hint', lang)}
        isDarkMode={isDarkMode}
      >
        <SelectField
          value={formData.docx.speed_mode}
          onChange={(nextValue) => onDocxSettingChange('speed_mode', nextValue as SpeedMode)}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {SPEED_MODES.map((mode) => (
            <option key={mode} value={mode} className={isDarkMode ? '!bg-surface-700 !text-surface-100' : ''}>
              {t(`settings.docx.speed_mode.${mode}`, lang)}
            </option>
          ))}
        </SelectField>
      </FieldGroup>

      {formData.docx.speed_mode === 'manual' && (
        <div className="pl-4 border-l-2 border-accent-100 space-y-4">
          <FieldGroup label={`${t('settings.docx.max_parallel_requests', lang)}: ${formData.docx.max_parallel_requests}`} isDarkMode={isDarkMode}>
            <input
              type="range"
              min={SETTINGS_LIMITS.maxParallelRequests.min}
              max={SETTINGS_LIMITS.maxParallelRequests.max}
              step={SETTINGS_LIMITS.maxParallelRequests.step}
              value={formData.docx.max_parallel_requests}
              onChange={(e) => onDocxSettingChange('max_parallel_requests', Number(e.target.value))}
              className="w-full mt-1"
            />
          </FieldGroup>

          <FieldGroup
            label={t('settings.docx.requests_per_minute', lang)}
            hint={isOllama
              ? t('settings.docx.requests_per_minute.ollama_hint', lang)
              : t('settings.docx.requests_per_minute.hint', lang)}
            isDarkMode={isDarkMode}
          >
            <input
              type="number"
              min={0}
              step={1}
              disabled={isOllama}
              placeholder={t('settings.docx.requests_per_minute.placeholder', lang)}
              value={formData.docx.manual_requests_per_minute ?? ''}
              onChange={(e) => {
                const raw = e.target.value.trim();
                const parsed = raw === '' ? null : Math.max(0, Math.round(Number(raw)));
                onDocxSettingChange(
                  'manual_requests_per_minute',
                  parsed === null || Number.isNaN(parsed) ? null : parsed,
                );
              }}
              className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100' : ''} ${isOllama ? 'opacity-50 cursor-not-allowed' : ''}`}
            />
          </FieldGroup>
        </div>
      )}

      <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
        <FieldGroup label={t('settings.ui_language', lang)} isDarkMode={isDarkMode}>
          <SelectField
            value={formData.ui_language}
            onChange={(nextValue) => onUiLanguageChange(nextValue as Language)}
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
      </div>

      <FieldGroup label={t('settings.font_size', lang)} isDarkMode={isDarkMode}>
        <SelectField
          value={formData.font_size}
          onChange={(nextValue) => setFormData((prev) => ({ ...prev, font_size: nextValue as FontSize }))}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {FONT_SIZES.map((size: FontSize) => (
            <option key={size} value={size}>
              {t(`settings.font_size.${size}`, lang)}
            </option>
          ))}
        </SelectField>
      </FieldGroup>

      <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
        <FieldGroup label={t('settings.auto_check_updates', lang)} isDarkMode={isDarkMode}>
          <ToggleRow
            label={t('settings.auto_check_updates.toggle', lang)}
            checked={formData.auto_check_updates}
            onChange={() => setFormData((prev) => ({ ...prev, auto_check_updates: !prev.auto_check_updates }))}
            isDarkMode={isDarkMode}
          />
          <button
            type="button"
            onClick={() => void onCheckUpdates()}
            disabled={isCheckingUpdates}
            className="btn-secondary !mt-2 !text-base"
          >
            {isCheckingUpdates ? <Loader2 className="animate-spin" size={14} /> : null}
            {t('settings.check_updates', lang)}
          </button>
          {updateCheckMessage && (
            <p className={`mt-2 text-sm ${isDarkMode ? 'text-surface-300' : 'text-surface-700'}`}>
              {updateCheckMessage}
            </p>
          )}
        </FieldGroup>
      </div>

      <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
        <FieldGroup label={t('settings.app_reset', lang)} isDarkMode={isDarkMode}>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void onResetApp()}
              disabled={isResettingApp}
              className="btn-secondary !text-base"
            >
              {isResettingApp ? <Loader2 className="animate-spin" size={14} /> : null}
              {t('settings.app_reset.button', lang)}
            </button>
            <button
              type="button"
              onClick={() => void onRerunWizard()}
              className="btn-secondary !text-base"
            >
              <Sparkles size={14} />
              {t('settings.rerun_wizard.button', lang)}
            </button>
          </div>
          {resetMessage && (
            <p className={`mt-2 text-sm ${resetMessageIsError ? 'text-amber-600' : 'text-emerald-600'}`}>
              {resetMessage}
            </p>
          )}
        </FieldGroup>
      </div>

      <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
        <FieldGroup label={t('settings.system_paths', lang)} isDarkMode={isDarkMode}>
          <div className="flex flex-wrap gap-2">
            <button type="button" onClick={() => void onOpenTempFolder()} className="btn-secondary !text-base">
              {t('settings.system_paths.temp_folder', lang)}
            </button>
            <button type="button" onClick={() => void onOpenSettingsJson()} className="btn-secondary !text-base">
              {t('settings.system_paths.settings_json', lang)}
            </button>
            <button type="button" onClick={() => void onOpenDebugLog()} className="btn-secondary !text-base">
              {t('settings.system_paths.debug_log', lang)}
            </button>
          </div>
          {systemPathMessage && (
            <p className="mt-2 text-sm text-amber-600">
              {systemPathMessage}
            </p>
          )}
        </FieldGroup>
      </div>
    </>
  );
}
