import { Cpu } from 'lucide-react';
import {
  Settings,
  Provider,
  PROVIDERS,
  PROVIDER_LABELS,
  DOCX_CORRECTION_SCOPE_PARTS,
  DocxCorrectionScopePart,
} from '../../types';
import { Language, t } from '../../i18n';
import { FieldGroup, SelectField } from './shared';

interface GeneralSectionProps {
  formData: Settings;
  setFormData: (updater: (prev: Settings) => Settings) => void;
  isDarkMode: boolean;
  lang: Language;
  menuBoundaryRef: React.RefObject<HTMLElement | null>;

  // Provider / model (the LLM connection)
  models: string[];
  isLoadingModels: boolean;
  modelError: string;
  onModelDropdownFocus: () => void;
  onProviderChange: (provider: Provider) => void;
  onApiKeyChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  onConfigureOllama: () => Promise<void> | void;

  // Document parts to process ("Korrekturumfang")
  onScopePartToggle: (part: DocxCorrectionScopePart) => void;
}

export function GeneralSection({
  formData,
  setFormData,
  isDarkMode,
  lang,
  menuBoundaryRef,
  models,
  isLoadingModels,
  modelError,
  onModelDropdownFocus,
  onProviderChange,
  onApiKeyChange,
  onConfigureOllama,
  onScopePartToggle,
}: GeneralSectionProps) {
  const isOllama = formData.provider === 'ollama';
  const isCustom = formData.provider === 'custom';

  return (
    <>
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="md:col-span-1">
          <FieldGroup label={t('settings.provider', lang)} isDarkMode={isDarkMode}>
            <SelectField
              value={formData.provider}
              onChange={(nextValue) => onProviderChange(nextValue as Provider)}
              menuBoundaryRef={menuBoundaryRef}
              isDarkMode={isDarkMode}
            >
              {PROVIDERS.map((key) => (
                <option key={key} value={key} className={isDarkMode ? '!bg-surface-700 !text-surface-100' : ''}>
                  {PROVIDER_LABELS[key]}
                </option>
              ))}
            </SelectField>
          </FieldGroup>
        </div>

        <div className="md:col-span-2">
          {isOllama ? (
            <FieldGroup label={t('settings.api_key', lang)} isDarkMode={isDarkMode}>
              <button type="button" onClick={() => void onConfigureOllama()} className="btn-secondary !text-base">
                <Cpu size={16} />
                {t('settings.ollama_configure.button', lang)}
              </button>
            </FieldGroup>
          ) : (
            <FieldGroup label={t('settings.api_key', lang)} required isDarkMode={isDarkMode}>
              <input
                type="password"
                value={formData.api_key || ''}
                onChange={onApiKeyChange}
                placeholder={t('settings.api_key.placeholder', lang)}
                className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
              />
            </FieldGroup>
          )}
        </div>
      </div>

      {isCustom && (
        <FieldGroup label={t('settings.api_url', lang)} hint={t('settings.api_url.hint', lang)} isDarkMode={isDarkMode}>
          <input
            type="text"
            value={formData.api_url}
            onChange={(e) => setFormData((prev) => ({ ...prev, api_url: e.target.value }))}
            placeholder={t('settings.api_url.placeholder', lang)}
            className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
          />
        </FieldGroup>
      )}

      <FieldGroup label={t('settings.model', lang)} error={modelError} isDarkMode={isDarkMode}>
        <SelectField
          value={isLoadingModels ? (formData.model || '__loading__') : (models.length === 0 ? (formData.model || '__no_models__') : formData.model)}
          onChange={(nextValue) => {
            if (nextValue === '__loading__' || nextValue === '__no_models__') {
              return;
            }
            setFormData((prev) => ({ ...prev, model: nextValue }));
          }}
          onOpen={onModelDropdownFocus}
          menuBoundaryRef={menuBoundaryRef}
          isDarkMode={isDarkMode}
        >
          {isLoadingModels ? (
            <option value={formData.model || '__loading__'}>{t('settings.model.loading', lang)}</option>
          ) : models.length === 0 ? (
            <option value={formData.model || '__no_models__'}>
              {formData.model?.trim() ? formData.model : t('settings.model.none', lang)}
            </option>
          ) : (
            models.map((model) => (
              <option key={model} value={model} className={isDarkMode ? '!bg-surface-700 !text-surface-100' : ''}>
                {model}
              </option>
            ))
          )}
        </SelectField>
      </FieldGroup>

      {/* Document parts to process ("Korrekturumfang"): used by both correction and translation */}
      <FieldGroup label={t('settings.docx.correction_scope_parts', lang)} isDarkMode={isDarkMode}>
        <div className="mt-1 flex flex-wrap gap-1.5">
          {DOCX_CORRECTION_SCOPE_PARTS.map((part) => (
            <label
              key={part}
              className={`inline-flex items-center gap-1.5 text-sm rounded-md px-1.5 py-0.5 ${isDarkMode ? 'text-surface-200' : 'text-surface-700'}`}
            >
              <input
                type="checkbox"
                checked={(formData.docx.correction_scope_parts ?? []).includes(part)}
                onChange={() => onScopePartToggle(part)}
              />
              <span>{t(`settings.docx.batching_parts.${part}`, lang)}</span>
            </label>
          ))}
        </div>
      </FieldGroup>
    </>
  );
}
