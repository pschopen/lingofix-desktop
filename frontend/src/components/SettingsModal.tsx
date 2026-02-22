import { useState, useEffect } from 'react';
import { invoke } from '../lib/bridge';
import { X, Loader2, RefreshCw, ChevronDown } from 'lucide-react';
import { Settings, Provider, DocxSettings, FontSize } from '../types';
import { Language, t } from '../i18n';

interface SettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
  settings: Settings;
  onSave: (settings: Settings) => void;
  onCheckUpdates: () => Promise<{ status: 'update-available' | 'up-to-date' | 'error'; message: string }>;
  lang: Language;
  isDarkMode?: boolean;
}

const PROVIDER_URLS: Record<Provider, string> = {
  openai: 'https://api.openai.com/v1',
  ollama: 'http://localhost:11434',
  openrouter: 'https://openrouter.ai/api/v1',
  huggingface: 'https://router.huggingface.co/v1',
  google: 'https://generativelanguage.googleapis.com/v1beta/openai',
  mistral: 'https://api.mistral.ai/v1',
  custom: '',
};

const PROVIDER_LABELS: Record<Provider, string> = {
  openai: 'OpenAI',
  ollama: 'Ollama',
  openrouter: 'Openrouter',
  huggingface: 'Hugging Face',
  google: 'Google AI Studio',
  mistral: 'Mistral',
  custom: 'Custom',
};

type TabType = 'general' | 'docx' | 'advanced';

interface CompareAccessStatus {
  ok: boolean;
  message: string;
  details: string;
}

export function SettingsModal({
  isOpen,
  onClose,
  settings,
  onSave,
  onCheckUpdates,
  lang,
  isDarkMode = false,
}: SettingsModalProps) {
  const [formData, setFormData] = useState<Settings>(settings);
  const [activeTab, setActiveTab] = useState<TabType>('general');
  const [models, setModels] = useState<string[]>([]);
  const [isLoadingModels, setIsLoadingModels] = useState(false);
  const [modelError, setModelError] = useState<string>('');
  const [isCheckingCompareAccess, setIsCheckingCompareAccess] = useState(false);
  const [compareAccessStatus, setCompareAccessStatus] = useState<CompareAccessStatus | null>(null);
  const [isCheckingUpdates, setIsCheckingUpdates] = useState(false);
  const [updateCheckMessage, setUpdateCheckMessage] = useState('');
  const [systemPathMessage, setSystemPathMessage] = useState('');

  useEffect(() => {
    if (isOpen) {
      setFormData(settings);
      setCompareAccessStatus(null);
      setUpdateCheckMessage('');
      setSystemPathMessage('');
    }
  }, [isOpen, settings]);

  const isMac = navigator.userAgent.toLowerCase().includes('mac');

  const handleFetchModels = async () => {
    if (!formData.api_url) {
      setModelError(t('settings.url_required', lang));
      return;
    }

    setIsLoadingModels(true);
    setModelError('');

    try {
      const fetchedModels = await invoke<string[]>('fetch_models', {
        apiUrl: formData.api_url,
        apiKey: formData.api_key,
        provider: formData.provider,
      });
      
      fetchedModels.sort((a, b) => a.localeCompare(b));
      
      setModels(fetchedModels);
      
      if (fetchedModels.length > 0 && !fetchedModels.includes(formData.model)) {
        setFormData(prev => ({ ...prev, model: fetchedModels[0] }));
      }
    } catch (error) {
      console.error('Failed to fetch models:', error);
      setModelError(`${t('settings.model.error', lang)}: ${error}`);
      setModels([]);
    } finally {
      setIsLoadingModels(false);
    }
  };

  const handleProviderChange = (newProvider: Provider) => {
    const updatedKeys = {
      ...formData.provider_keys,
      [formData.provider]: formData.api_key,
    };

    const newApiKey = updatedKeys[newProvider] || null;
    
    setFormData(prev => ({
      ...prev,
      provider: newProvider,
      api_url: PROVIDER_URLS[newProvider] || prev.api_url,
      api_key: newApiKey,
      provider_keys: updatedKeys,
      model: '',
    }));
    setModels([]);
  };

  const handleDocxSettingChange = <K extends keyof DocxSettings>(key: K, value: DocxSettings[K]) => {
    setFormData(prev => ({
      ...prev,
      docx: {
        ...prev.docx,
        [key]: value,
      },
    }));
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (isCustom && formData.api_url) {
      try {
        new URL(formData.api_url);
      } catch {
        setModelError(t('settings.url_required', lang));
        return;
      }
    }
    onSave(formData);
  };

  const handleCompareAccessCheck = async () => {
    setIsCheckingCompareAccess(true);
    const command = formData.docx.compare_mode === 'libreoffice-uno'
      ? 'check_libreoffice_compare_access'
      : 'check_word_compare_access';

    try {
      const status = await invoke<CompareAccessStatus>(command);
      setCompareAccessStatus(status);
    } catch (error) {
      setCompareAccessStatus({
        ok: false,
        message: t('settings.docx.compare_check.failed', lang),
        details: String(error),
      });
    } finally {
      setIsCheckingCompareAccess(false);
    }
  };

  const handleClose = () => {
    setModelError('');
    onClose();
  };

  const handleCheckUpdates = async () => {
    setIsCheckingUpdates(true);
    setUpdateCheckMessage('');
    try {
      const result = await onCheckUpdates();
      setUpdateCheckMessage(result.message);
    } catch (error) {
      setUpdateCheckMessage(String(error));
    } finally {
      setIsCheckingUpdates(false);
    }
  };

  const handleOpenTempFolder = async () => {
    setSystemPathMessage('');
    try {
      await invoke('open_temp_lingofix_folder');
    } catch (error) {
      setSystemPathMessage(`${t('settings.system_paths.open_failed', lang)}: ${error}`);
    }
  };

  const handleOpenSettingsJson = async () => {
    setSystemPathMessage('');
    try {
      await invoke('open_settings_json');
    } catch (error) {
      setSystemPathMessage(`${t('settings.system_paths.open_failed', lang)}: ${error}`);
    }
  };

  if (!isOpen) return null;

  const isOllama = formData.provider === 'ollama';
  const isCustom = formData.provider === 'custom';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center modal-backdrop animate-fade-in">
      <div className={`card w-full max-w-2xl max-h-[90vh] overflow-hidden flex flex-col animate-scale-in mx-4 ${isDarkMode ? '!bg-surface-800 !border-surface-700' : ''}`}>
        {/* Header */}
        <div className={`flex items-center justify-between px-6 py-4 border-b ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
          <h2 className={`text-base font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
            {t('settings.title', lang)}
          </h2>
          <button
            onClick={handleClose}
            className="btn-ghost !p-1.5 !rounded-lg"
          >
            <X size={16} />
          </button>
        </div>

        {/* Tabs */}
        <div className="flex gap-1 px-6 pt-3 pb-0">
          <button
            onClick={() => setActiveTab('general')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-all duration-200 ${
              activeTab === 'general'
                ? (isDarkMode ? 'bg-accent-900/40 text-accent-300 shadow-premium' : 'bg-accent-50 text-accent-700 shadow-premium')
                : (isDarkMode ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-700' : 'text-surface-600 hover:text-surface-700 hover:bg-surface-50')
            }`}
          >
            {t('settings.tab.general', lang)}
          </button>
          <button
            onClick={() => setActiveTab('docx')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-all duration-200 ${
              activeTab === 'docx'
                ? (isDarkMode ? 'bg-accent-900/40 text-accent-300 shadow-premium' : 'bg-accent-50 text-accent-700 shadow-premium')
                : (isDarkMode ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-700' : 'text-surface-600 hover:text-surface-700 hover:bg-surface-50')
            }`}
          >
            {t('settings.tab.docx', lang)}
          </button>
          <button
            onClick={() => setActiveTab('advanced')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-all duration-200 ${
              activeTab === 'advanced'
                ? (isDarkMode ? 'bg-accent-900/40 text-accent-300 shadow-premium' : 'bg-accent-50 text-accent-700 shadow-premium')
                : (isDarkMode ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-700' : 'text-surface-600 hover:text-surface-700 hover:bg-surface-50')
            }`}
          >
            {t('settings.tab.advanced', lang)}
          </button>
        </div>

        {/* Form */}
        <form onSubmit={handleSubmit} className="flex-1 overflow-y-auto">
          <div className="p-6 space-y-5">
            {activeTab === 'general' ? (
              <>
                {/* Provider Selection */}
                <FieldGroup label={t('settings.provider', lang)}>
                  <SelectField
                    value={formData.provider}
                    onChange={(e) => handleProviderChange(e.target.value as Provider)}
                    isDarkMode={isDarkMode}
                  >
                    {(Object.keys(PROVIDER_LABELS) as Provider[]).map((key) => (
                      <option key={key} value={key}>
                        {PROVIDER_LABELS[key]}
                      </option>
                    ))}
                  </SelectField>
                </FieldGroup>

                {/* API URL — only for Custom */}
                {isCustom && (
                  <FieldGroup label={t('settings.api_url', lang)} hint={t('settings.api_url.hint', lang)} isDarkMode={isDarkMode}>
                    <input
                      type="text"
                      value={formData.api_url}
                      onChange={(e) => setFormData({ ...formData, api_url: e.target.value })}
                      placeholder={t('settings.api_url.placeholder', lang)}
                      className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                    />
                  </FieldGroup>
                )}

                {/* API Key */}
                <FieldGroup
                  label={t('settings.api_key', lang)}
                  required={!isOllama}
                  isDarkMode={isDarkMode}
                >
                  <input
                    type="password"
                    value={formData.api_key || ''}
                    onChange={(e) => setFormData({ ...formData, api_key: e.target.value || null })}
                    placeholder={isOllama ? t('settings.api_key.optional', lang) : t('settings.api_key.placeholder', lang)}
                    className={`input !text-sm ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                  />
                </FieldGroup>

                {/* Model Selection */}
                <FieldGroup label={t('settings.model', lang)} error={modelError} isDarkMode={isDarkMode}>
                  <div className="flex gap-2">
                    <SelectField
                      value={formData.model}
                      onChange={(e) => setFormData({ ...formData, model: e.target.value })}
                      className="flex-1"
                      isDarkMode={isDarkMode}
                    >
                      {models.length === 0 ? (
                        <option value={formData.model}>{formData.model}</option>
                      ) : (
                        models.map((model) => (
                          <option key={model} value={model}>
                            {model}
                          </option>
                        ))
                      )}
                    </SelectField>
                    <button
                      type="button"
                      onClick={handleFetchModels}
                      disabled={isLoadingModels}
                      className="btn-secondary !py-2 !text-base flex-shrink-0"
                    >
                      {isLoadingModels ? (
                        <Loader2 className="animate-spin" size={14} />
                      ) : (
                        <RefreshCw size={14} />
                      )}
                      {t('settings.model.load', lang)}
                    </button>
                  </div>
                </FieldGroup>

                {/* Prompt */}
                <FieldGroup label={t('settings.prompt', lang)} hint={t('settings.prompt.hint', lang)} isDarkMode={isDarkMode}>
                  <textarea
                    value={formData.custom_prompt}
                    onChange={(e) => setFormData({ ...formData, custom_prompt: e.target.value })}
                    placeholder={t('settings.prompt.placeholder', lang)}
                    className={`textarea !text-base h-28 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                  />
                </FieldGroup>

                {/* Font Size */}
                <FieldGroup label={t('settings.font_size', lang)} isDarkMode={isDarkMode}>
                  <SelectField
                    value={formData.font_size}
                    onChange={(e) => setFormData({ ...formData, font_size: e.target.value as FontSize })}
                    isDarkMode={isDarkMode}
                  >
                    {(['small', 'default', 'large', 'xl', 'xxl'] as FontSize[]).map((size) => (
                      <option key={size} value={size}>
                        {t(`settings.font_size.${size}`, lang)}
                      </option>
                    ))}
                  </SelectField>
                </FieldGroup>
              </>
            ) : activeTab === 'docx' ? (
              <>
                {/* Compare Mode */}
                <FieldGroup label={t('settings.docx.compare_mode', lang)} isDarkMode={isDarkMode}>
                  <SelectField
                    value={formData.docx.compare_mode}
                    onChange={(e) => handleDocxSettingChange('compare_mode', e.target.value as 'openxml' | 'word-native' | 'libreoffice-uno')}
                    isDarkMode={isDarkMode}
                  >
                    <option value="openxml">{t('settings.docx.compare_mode.openxml', lang)}</option>
                    <option value="word-native">{t('settings.docx.compare_mode.word_native', lang)}</option>
                    <option value="libreoffice-uno">{t('settings.docx.compare_mode.libreoffice_uno', lang)}</option>
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
                    <p className={`text-sm ${isDarkMode ? 'text-surface-300' : 'text-surface-700'}`}>
                      {formData.docx.compare_mode === 'libreoffice-uno'
                        ? t('settings.docx.libreoffice_check.hint', lang)
                        : isMac
                          ? t('settings.docx.word_check.hint', lang)
                          : t('settings.docx.word_check.hint_non_macos', lang)}
                    </p>
                    <button
                      type="button"
                      onClick={handleCompareAccessCheck}
                      disabled={isCheckingCompareAccess}
                      className="btn-secondary !mt-2 !text-base"
                    >
                      {isCheckingCompareAccess ? (
                        <Loader2 className="animate-spin" size={14} />
                      ) : null}
                      {formData.docx.compare_mode === 'libreoffice-uno'
                        ? t('settings.docx.libreoffice_check.button', lang)
                        : t('settings.docx.word_check.button', lang)}
                    </button>
                    {compareAccessStatus && (
                      <p className={`mt-2 text-sm whitespace-pre-wrap ${compareAccessStatus.ok ? 'text-emerald-600' : 'text-amber-600'}`}>
                        {compareAccessStatus.message}
                        {compareAccessStatus.details ? `\n${compareAccessStatus.details}` : ''}
                      </p>
                    )}
                  </div>
                )}

                {/* Batching */}
                <ToggleRow
                  label={t('settings.docx.batching', lang)}
                  checked={formData.docx.enable_batching}
                  onChange={() => handleDocxSettingChange('enable_batching', !formData.docx.enable_batching)}
                  isDarkMode={isDarkMode}
                />

                {formData.docx.enable_batching && (
                  <div className="pl-4 border-l-2 border-accent-100 space-y-4">
                    <FieldGroup label={`${t('settings.docx.batch_max_chars', lang)}: ${formData.docx.batch_max_chars}`} isDarkMode={isDarkMode}>
                      <input
                        type="range" min="1000" max="1000000" step="1000"
                        value={formData.docx.batch_max_chars}
                        onChange={(e) => handleDocxSettingChange('batch_max_chars', Number(e.target.value))}
                        className="w-full mt-1"
                      />
                    </FieldGroup>
                    <FieldGroup label={`${t('settings.docx.batch_max_paragraphs', lang)}: ${formData.docx.batch_max_paragraphs}`} isDarkMode={isDarkMode}>
                      <input
                        type="range" min="1" max="10000" step="10"
                        value={formData.docx.batch_max_paragraphs}
                        onChange={(e) => handleDocxSettingChange('batch_max_paragraphs', Number(e.target.value))}
                        className="w-full mt-1"
                      />
                    </FieldGroup>
                  </div>
                )}

                {/* Cache */}
                <ToggleRow
                  label={t('settings.docx.cache', lang)}
                  checked={formData.docx.enable_cache}
                  onChange={() => handleDocxSettingChange('enable_cache', !formData.docx.enable_cache)}
                  isDarkMode={isDarkMode}
                />

                {/* Parallelization */}
                <ToggleRow
                  label={t('settings.docx.parallelization', lang)}
                  checked={formData.docx.enable_parallelization}
                  onChange={() => handleDocxSettingChange('enable_parallelization', !formData.docx.enable_parallelization)}
                  isDarkMode={isDarkMode}
                />

                {formData.docx.enable_parallelization && (
                  <div className="pl-4 border-l-2 border-accent-100">
                    <FieldGroup label={`${t('settings.docx.max_parallel_requests', lang)}: ${formData.docx.max_parallel_requests}`} isDarkMode={isDarkMode}>
                      <input
                        type="range" min="1" max="32" step="1"
                        value={formData.docx.max_parallel_requests}
                        onChange={(e) => handleDocxSettingChange('max_parallel_requests', Number(e.target.value))}
                        className="w-full mt-1"
                      />
                    </FieldGroup>
                  </div>
                )}

              </>
            ) : (
              <>
                {/* Temperature */}
                <FieldGroup label={`${t('settings.temperature', lang)}: ${formData.temperature}`} isDarkMode={isDarkMode}>
                  <input
                    type="range"
                    min="0"
                    max="2"
                    step="0.1"
                    value={formData.temperature}
                    onChange={(e) => setFormData({ ...formData, temperature: parseFloat(e.target.value) })}
                    className="w-full mt-1"
                  />
                </FieldGroup>

                {/* System Prompt (shared) */}
                <FieldGroup label={t('settings.system_prompt', lang)} hint={t('settings.system_prompt.hint', lang)} isDarkMode={isDarkMode}>
                  <textarea
                    value={formData.system_prompt}
                    onChange={(e) => setFormData({ ...formData, system_prompt: e.target.value })}
                    placeholder={t('settings.system_prompt.value', lang)}
                    className={`textarea !text-base h-28 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                  />
                </FieldGroup>

                <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
                  <FieldGroup
                    label={t('settings.auto_check_updates', lang)}
                    hint={t('settings.auto_check_updates.hint', lang)}
                    isDarkMode={isDarkMode}
                  >
                    <ToggleRow
                      label={t('settings.auto_check_updates.toggle', lang)}
                      checked={formData.auto_check_updates}
                      onChange={() => setFormData({ ...formData, auto_check_updates: !formData.auto_check_updates })}
                      isDarkMode={isDarkMode}
                    />
                    <button
                      type="button"
                      onClick={handleCheckUpdates}
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
                  <FieldGroup
                    label={t('settings.system_paths', lang)}
                    hint={t('settings.system_paths.hint', lang)}
                    isDarkMode={isDarkMode}
                  >
                    <div className="flex flex-wrap gap-2">
                      <button
                        type="button"
                        onClick={handleOpenTempFolder}
                        className="btn-secondary !text-base"
                      >
                        {t('settings.system_paths.temp_folder', lang)}
                      </button>
                      <button
                        type="button"
                        onClick={handleOpenSettingsJson}
                        className="btn-secondary !text-base"
                      >
                        {t('settings.system_paths.settings_json', lang)}
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
            )}
          </div>

          {/* Footer */}
          <div className={`sticky bottom-0 px-6 py-4 border-t flex justify-end gap-2 ${isDarkMode ? 'bg-surface-900/50 border-surface-700' : 'bg-white border-surface-100'}`}>
            <button
              type="button"
              onClick={handleClose}
              className="btn-secondary !text-base"
            >
              {t('settings.cancel', lang)}
            </button>
            <button
              type="submit"
              className="btn-primary !text-base"
            >
              {t('settings.save', lang)}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

/* ================================================================
   Sub-components for cleaner settings layout
   ================================================================ */

function FieldGroup({
  label,
  hint,
  error,
  required,
  isDarkMode = false,
  children,
}: {
  label: string;
  hint?: string;
  error?: string;
  required?: boolean;
  isDarkMode?: boolean;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label className={`block text-base font-medium mb-1.5 ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>
        {label}
        {required && <span className="text-red-400 ml-0.5">*</span>}
      </label>
      {children}
      {hint && <p className={`mt-1 text-sm ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>{hint}</p>}
      {error && <p className="mt-1 text-sm text-red-500">{error}</p>}
    </div>
  );
}

function SelectField({
  value,
  onChange,
  children,
  className = '',
  isDarkMode = false,
}: {
  value: string;
  onChange: (e: React.ChangeEvent<HTMLSelectElement>) => void;
  children: React.ReactNode;
  className?: string;
  isDarkMode?: boolean;
}) {
  return (
    <div className={`relative ${className}`}>
      <select
        value={value}
        onChange={onChange}
        className={`input !text-base !pr-9 cursor-pointer ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100' : ''}`}
      >
        {children}
      </select>
      <div className="absolute inset-y-0 right-0 flex items-center px-3 pointer-events-none">
        <ChevronDown size={16} className={isDarkMode ? 'text-surface-400' : 'text-surface-500'} />
      </div>
    </div>
  );
}

function ToggleRow({
  label,
  checked,
  onChange,
  isDarkMode = false,
}: {
  label: string;
  checked: boolean;
  onChange: () => void;
  isDarkMode?: boolean;
}) {
  return (
    <div className="flex items-center justify-between py-1">
      <label className={`text-base font-medium ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>{label}</label>
      <button
        type="button"
        onClick={onChange}
        className={`toggle-track ${checked ? 'toggle-track-on' : 'toggle-track-off'}`}
      >
        <span className={`toggle-thumb ${checked ? 'toggle-thumb-on' : 'toggle-thumb-off'}`} />
      </button>
    </div>
  );
}
