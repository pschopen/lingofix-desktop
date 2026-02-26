import { Children, isValidElement, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { invoke } from '../lib/bridge';
import { X, Loader2, ChevronDown, Plus, Copy, Pencil, Trash2 } from 'lucide-react';
import {
  Settings,
  CustomPromptPreset,
  Provider,
  DocxSettings,
  FontSize,
  FONT_SIZES,
  PROVIDERS,
  PROVIDER_DEFAULT_URLS,
  PROVIDER_LABELS,
  SETTINGS_LIMITS,
  DOCX_COMPARE_MODES,
} from '../types';
import { Language, t } from '../i18n';

interface SettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
  settings: Settings | null;
  onSave: (settings: Settings) => void;
  onResetSettings: () => Promise<Settings>;
  onCheckUpdates: () => Promise<{ status: 'update-available' | 'up-to-date' | 'error'; message: string }>;
  lang: Language;
  isDarkMode?: boolean;
}

type TabType = 'general' | 'docx' | 'advanced';

interface CompareAccessStatus {
  ok: boolean;
  message: string;
  details: string;
}

type PresetDialogMode = 'new' | 'rename' | 'delete' | null;

export function SettingsModal({
  isOpen,
  onClose,
  settings,
  onSave,
  onResetSettings,
  onCheckUpdates,
  lang,
  isDarkMode = false,
}: SettingsModalProps) {
  const modalPanelRef = useRef<HTMLDivElement | null>(null);
  const [formData, setFormData] = useState<Settings | null>(settings);
  const [activeTab, setActiveTab] = useState<TabType>('general');
  const [models, setModels] = useState<string[]>([]);
  const [isLoadingModels, setIsLoadingModels] = useState(false);
  const [modelError, setModelError] = useState<string>('');
  const [isCheckingCompareAccess, setIsCheckingCompareAccess] = useState(false);
  const [compareAccessStatus, setCompareAccessStatus] = useState<CompareAccessStatus | null>(null);
  const [isCheckingUpdates, setIsCheckingUpdates] = useState(false);
  const [updateCheckMessage, setUpdateCheckMessage] = useState('');
  const [systemPathMessage, setSystemPathMessage] = useState('');
  const [isResettingApp, setIsResettingApp] = useState(false);
  const [resetMessage, setResetMessage] = useState('');
  const [resetMessageIsError, setResetMessageIsError] = useState(false);
  const [presetMessage, setPresetMessage] = useState('');
  const [presetDialogMode, setPresetDialogMode] = useState<PresetDialogMode>(null);
  const [presetDialogValue, setPresetDialogValue] = useState('');

  useEffect(() => {
    if (isOpen) {
      setFormData(settings);
      setCompareAccessStatus(null);
      setUpdateCheckMessage('');
      setSystemPathMessage('');
      setResetMessage('');
      setResetMessageIsError(false);
      setPresetMessage('');
      setPresetDialogMode(null);
      setPresetDialogValue('');
    }
  }, [isOpen, settings]);

  const isMac = navigator.userAgent.toLowerCase().includes('mac');

  const fetchModelsForSettings = async (candidate: Settings) => {
    if (!candidate.api_url) {
      setModelError(t('settings.url_required', lang));
      return;
    }

    setIsLoadingModels(true);
    setModelError('');

    try {
      const fetchedModels = await invoke<string[]>('fetch_models', {
        apiUrl: candidate.api_url,
        apiKey: candidate.api_key,
        provider: candidate.provider,
      });
      
      fetchedModels.sort((a, b) => a.localeCompare(b));
      
      setModels(fetchedModels);
      
      if (fetchedModels.length > 0 && !fetchedModels.includes(candidate.model)) {
        setFormData((prev) => {
          if (!prev) {
            return prev;
          }
          return { ...prev, model: fetchedModels[0] };
        });
      }
    } catch (error) {
      console.error('Failed to fetch models:', error);
      setModelError(`${t('settings.model.error', lang)}: ${error}`);
      setModels([]);
    } finally {
      setIsLoadingModels(false);
    }
  };

  const handleFetchModels = async () => {
    if (!formData) {
      return;
    }

    await fetchModelsForSettings(formData);
  };

  const handleProviderChange = (newProvider: Provider) => {
    if (!formData) {
      return;
    }

    const updatedKeys = {
      ...formData.provider_keys,
      [formData.provider]: formData.api_key,
    };

    const newApiKey = updatedKeys[newProvider] || null;
    
    const nextSettings: Settings = {
      ...formData,
      provider: newProvider,
      api_url: PROVIDER_DEFAULT_URLS[newProvider] || formData.api_url,
      api_key: newApiKey,
      provider_keys: updatedKeys,
      model: '',
    };

    setFormData(nextSettings);
    setModels([]);
    void fetchModelsForSettings(nextSettings);
  };

  const handleDocxSettingChange = <K extends keyof DocxSettings>(key: K, value: DocxSettings[K]) => {
    if (!formData) {
      return;
    }

    setFormData({
      ...formData,
      docx: {
        ...formData.docx,
        [key]: value,
      },
    });
  };

  const handleModelDropdownFocus = () => {
    if (isLoadingModels || models.length > 0) {
      return;
    }

    void handleFetchModels();
  };

  const handleApiKeyChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (!formData) {
      return;
    }

    const nextApiKey = e.target.value || null;
    const hadApiKey = !!formData.api_key?.trim();
    const hasApiKeyNow = !!nextApiKey?.trim();
    const nextSettings: Settings = {
      ...formData,
      api_key: nextApiKey,
    };

    setFormData(nextSettings);

    if (!hadApiKey && hasApiKeyNow) {
      void fetchModelsForSettings(nextSettings);
    }
  };

  const createPresetId = () => {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
      return crypto.randomUUID();
    }
    return `preset-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
  };

  const getActivePreset = (current: Settings): CustomPromptPreset | undefined => {
    return current.custom_prompt_presets.find((preset) => preset.id === current.active_custom_prompt_preset_id);
  };

  const handleSelectCustomPromptPreset = (presetId: string) => {
    if (!formData) {
      return;
    }

    const selected = formData.custom_prompt_presets.find((preset) => preset.id === presetId);
    if (!selected) {
      return;
    }

    setPresetMessage('');
    setFormData({
      ...formData,
      active_custom_prompt_preset_id: selected.id,
      custom_prompt: selected.value,
    });
  };

  const handleCreateCustomPromptPreset = () => {
    if (!formData) {
      return;
    }

    setPresetMessage('');
    setPresetDialogValue(t('settings.prompt_presets.new_name_default', lang));
    setPresetDialogMode('new');
  };

  const handleDuplicateCustomPromptPreset = () => {
    if (!formData) {
      return;
    }

    const active = getActivePreset(formData);
    if (!active) {
      return;
    }

    const duplicated: CustomPromptPreset = {
      ...active,
      id: createPresetId(),
      name: `${active.name} (${t('settings.prompt_presets.copy_suffix', lang)})`,
    };

    setPresetMessage('');
    setFormData({
      ...formData,
      custom_prompt_presets: [...formData.custom_prompt_presets, duplicated],
      active_custom_prompt_preset_id: duplicated.id,
      custom_prompt: duplicated.value,
    });
  };

  const handleRenameCustomPromptPreset = () => {
    if (!formData) {
      return;
    }

    const active = getActivePreset(formData);
    if (!active) {
      return;
    }

    setPresetMessage('');
    setPresetDialogValue(active.name);
    setPresetDialogMode('rename');
  };

  const handleDeleteCustomPromptPreset = () => {
    if (!formData) {
      return;
    }

    if (formData.custom_prompt_presets.length <= 1) {
      setPresetMessage(t('settings.prompt_presets.keep_one', lang));
      return;
    }

    const active = getActivePreset(formData);
    if (!active) {
      return;
    }

    setPresetMessage('');
    setPresetDialogMode('delete');
  };

  const closePresetDialog = () => {
    setPresetDialogMode(null);
    setPresetDialogValue('');
  };

  const handleConfirmPresetDialog = () => {
    if (!formData || !presetDialogMode) {
      return;
    }

    if (presetDialogMode === 'delete') {
      const active = getActivePreset(formData);
      if (!active) {
        closePresetDialog();
        return;
      }

      const remaining = formData.custom_prompt_presets.filter((preset) => preset.id !== active.id);
      const nextActive = remaining[0];
      if (!nextActive) {
        closePresetDialog();
        return;
      }

      setPresetMessage('');
      setFormData({
        ...formData,
        custom_prompt_presets: remaining,
        active_custom_prompt_preset_id: nextActive.id,
        custom_prompt: nextActive.value,
      });
      closePresetDialog();
      return;
    }

    const trimmedName = presetDialogValue.trim();
    if (!trimmedName) {
      setPresetMessage(t('settings.prompt_presets.empty_name', lang));
      return;
    }

    if (presetDialogMode === 'new') {
      const locale = lang === 'de' ? 'de' : 'en';
      const newPreset: CustomPromptPreset = {
        id: createPresetId(),
        name: trimmedName,
        value: formData.custom_prompt,
        locale,
      };

      setPresetMessage('');
      setFormData({
        ...formData,
        custom_prompt_presets: [...formData.custom_prompt_presets, newPreset],
        active_custom_prompt_preset_id: newPreset.id,
      });
      closePresetDialog();
      return;
    }

    const active = getActivePreset(formData);
    if (!active) {
      closePresetDialog();
      return;
    }

    setPresetMessage('');
    setFormData({
      ...formData,
      custom_prompt_presets: formData.custom_prompt_presets.map((preset) =>
        preset.id === active.id ? { ...preset, name: trimmedName } : preset,
      ),
    });
    closePresetDialog();
  };

  const handleCustomPromptChange = (value: string) => {
    if (!formData) {
      return;
    }

    const activeId = formData.active_custom_prompt_preset_id;
    setPresetMessage('');
    setFormData({
      ...formData,
      custom_prompt: value,
      custom_prompt_presets: formData.custom_prompt_presets.map((preset) =>
        preset.id === activeId ? { ...preset, value } : preset,
      ),
    });
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!formData) {
      return;
    }

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
    if (!formData) {
      return;
    }

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

  const handleOpenDebugLog = async () => {
    setSystemPathMessage('');
    try {
      await invoke('open_debug_log');
    } catch (error) {
      setSystemPathMessage(`${t('settings.system_paths.open_failed', lang)}: ${error}`);
    }
  };

  const handleResetApp = async () => {
    setResetMessage('');
    setResetMessageIsError(false);
    setSystemPathMessage('');
    setIsResettingApp(true);
    try {
      const resetSettings = await onResetSettings();
      setFormData(resetSettings);
      setResetMessage(t('settings.app_reset.success', lang));
      setResetMessageIsError(false);
    } catch (error) {
      setResetMessage(`${t('settings.app_reset.failed', lang)}: ${error}`);
      setResetMessageIsError(true);
    } finally {
      setIsResettingApp(false);
    }
  };

  if (!isOpen) return null;

  if (!formData) {
    return (
      <div className="fixed inset-0 z-50 flex items-center justify-center modal-backdrop animate-fade-in">
        <div ref={modalPanelRef} className={`card w-full max-w-2xl max-h-[90vh] overflow-hidden flex flex-col animate-scale-in mx-4 ${isDarkMode ? '!bg-surface-800 !border-surface-700' : ''}`}>
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

          <div className="px-6 pt-3 pb-0">
            <span className={`inline-flex px-4 py-2 text-sm font-medium rounded-lg ${isDarkMode ? 'bg-accent-900/40 text-accent-300 shadow-premium' : 'bg-accent-50 text-accent-700 shadow-premium'}`}>
              {t('settings.tab.advanced', lang)}
            </span>
          </div>

          <div className="p-6 space-y-5 overflow-y-auto">
            <FieldGroup
              label={t('settings.app_reset', lang)}
              isDarkMode={isDarkMode}
            >
              <button
                type="button"
                onClick={handleResetApp}
                disabled={isResettingApp}
                className="btn-secondary !text-base"
              >
                {isResettingApp ? <Loader2 className="animate-spin" size={14} /> : null}
                {t('settings.app_reset.button', lang)}
              </button>
              {resetMessage && (
                <p className={`mt-2 text-sm ${resetMessageIsError ? 'text-amber-600' : 'text-emerald-600'}`}>
                  {resetMessage}
                </p>
              )}
            </FieldGroup>

            <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
              <FieldGroup
                label={t('settings.system_paths', lang)}
                isDarkMode={isDarkMode}
              >
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={handleOpenSettingsJson}
                    className="btn-secondary !text-base"
                  >
                    {t('settings.system_paths.settings_json', lang)}
                  </button>
                  <button
                    type="button"
                    onClick={handleOpenDebugLog}
                    className="btn-secondary !text-base"
                  >
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
          </div>

          <div className={`sticky bottom-0 px-6 py-4 border-t flex justify-end ${isDarkMode ? 'bg-surface-900/50 border-surface-700' : 'bg-white border-surface-100'}`}>
            <button
              type="button"
              onClick={handleClose}
              className="btn-secondary !text-base"
            >
              {t('error.close', lang)}
            </button>
          </div>
        </div>
      </div>
    );
  }

  const isOllama = formData.provider === 'ollama';
  const isCustom = formData.provider === 'custom';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center modal-backdrop animate-fade-in">
      <div ref={modalPanelRef} className={`card w-full max-w-2xl max-h-[90vh] overflow-hidden flex flex-col animate-scale-in mx-4 ${isDarkMode ? '!bg-surface-800 !border-surface-700' : ''}`}>
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
                {/* Custom Prompt Presets */}
                <FieldGroup label={t('settings.prompt_presets', lang)} isDarkMode={isDarkMode}>
                  <div className={`rounded-xl border p-3 space-y-3 ${isDarkMode ? 'border-surface-700 bg-surface-800/50' : 'border-surface-200 bg-surface-50/80'}`}>
                    <div className="flex flex-wrap gap-2">
                      <button
                        type="button"
                        onClick={handleCreateCustomPromptPreset}
                        className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
                      >
                        <Plus size={12} />
                        {t('settings.prompt_presets.new', lang)}
                      </button>
                      <button
                        type="button"
                        onClick={handleDuplicateCustomPromptPreset}
                        className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
                      >
                        <Copy size={12} />
                        {t('settings.prompt_presets.duplicate', lang)}
                      </button>
                      <button
                        type="button"
                        onClick={handleRenameCustomPromptPreset}
                        className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
                      >
                        <Pencil size={12} />
                        {t('settings.prompt_presets.rename', lang)}
                      </button>
                      <button
                        type="button"
                        onClick={handleDeleteCustomPromptPreset}
                        className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
                      >
                        <Trash2 size={12} />
                        {t('settings.prompt_presets.delete', lang)}
                      </button>
                    </div>

                    <div className="space-y-1">
                      <SelectField
                        value={formData.active_custom_prompt_preset_id}
                        onChange={(nextValue) => handleSelectCustomPromptPreset(nextValue)}
                        menuBoundaryRef={modalPanelRef}
                        isDarkMode={isDarkMode}
                      >
                        {formData.custom_prompt_presets.map((preset) => (
                          <option key={preset.id} value={preset.id} className={isDarkMode ? '!bg-surface-700 !text-surface-100' : ''}>
                            {preset.name}
                          </option>
                        ))}
                      </SelectField>
                    </div>

                    <textarea
                      value={formData.custom_prompt}
                      onChange={(e) => handleCustomPromptChange(e.target.value)}
                      placeholder={t('settings.prompt.hint', lang)}
                      className={`textarea !text-base h-28 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                    />

                    {presetMessage && (
                      <p className="text-sm text-amber-600">{presetMessage}</p>
                    )}
                  </div>
                </FieldGroup>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                  {/* Provider Selection */}
                  <div className="md:col-span-1">
                    <FieldGroup label={t('settings.provider', lang)}>
                      <SelectField
                        value={formData.provider}
                        onChange={(nextValue) => handleProviderChange(nextValue as Provider)}
                        menuBoundaryRef={modalPanelRef}
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

                  {/* API Key */}
                  <div className="md:col-span-2">
                    <FieldGroup
                      label={t('settings.api_key', lang)}
                      required={!isOllama}
                      isDarkMode={isDarkMode}
                    >
                      <input
                        type="password"
                        value={formData.api_key || ''}
                        onChange={handleApiKeyChange}
                        placeholder={isOllama ? t('settings.api_key.optional', lang) : t('settings.api_key.placeholder', lang)}
                        className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                      />
                    </FieldGroup>
                  </div>
                </div>

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

                {/* Model Selection */}
                <FieldGroup label={t('settings.model', lang)} error={modelError} isDarkMode={isDarkMode}>
                  <SelectField
                    value={isLoadingModels ? (formData.model || '__loading__') : (models.length === 0 ? (formData.model || '__no_models__') : formData.model)}
                    onChange={(nextValue) => {
                      if (nextValue === '__loading__' || nextValue === '__no_models__') {
                        return;
                      }
                      setFormData({ ...formData, model: nextValue });
                    }}
                    onOpen={handleModelDropdownFocus}
                    menuBoundaryRef={modalPanelRef}
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

              </>
            ) : activeTab === 'docx' ? (
              <>
                {/* Compare Mode */}
                <FieldGroup label={t('settings.docx.compare_mode', lang)} isDarkMode={isDarkMode}>
                  <SelectField
                    value={formData.docx.compare_mode}
                    onChange={(nextValue) => handleDocxSettingChange('compare_mode', nextValue as DocxSettings['compare_mode'])}
                    menuBoundaryRef={modalPanelRef}
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
                <FieldGroup label={`${t('settings.docx.chunk_size', lang)}: ${formData.docx.chunk_size}`} isDarkMode={isDarkMode}>
                  <input
                    type="range"
                    min={SETTINGS_LIMITS.chunkSize.min}
                    max={SETTINGS_LIMITS.chunkSize.max}
                    step={SETTINGS_LIMITS.chunkSize.step}
                    value={formData.docx.chunk_size}
                    onChange={(e) => handleDocxSettingChange('chunk_size', Number(e.target.value))}
                    className="w-full mt-1"
                  />
                </FieldGroup>

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
                        type="range"
                        min={SETTINGS_LIMITS.batchMaxChars.min}
                        max={SETTINGS_LIMITS.batchMaxChars.max}
                        step={SETTINGS_LIMITS.batchMaxChars.step}
                        value={formData.docx.batch_max_chars}
                        onChange={(e) => handleDocxSettingChange('batch_max_chars', Number(e.target.value))}
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
                        type="range"
                        min={SETTINGS_LIMITS.maxParallelRequests.min}
                        max={SETTINGS_LIMITS.maxParallelRequests.max}
                        step={SETTINGS_LIMITS.maxParallelRequests.step}
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
                {/* Font Size */}
                <FieldGroup label={t('settings.font_size', lang)} isDarkMode={isDarkMode}>
                  <SelectField
                    value={formData.font_size}
                    onChange={(nextValue) => setFormData({ ...formData, font_size: nextValue as FontSize })}
                    menuBoundaryRef={modalPanelRef}
                    isDarkMode={isDarkMode}
                  >
                    {FONT_SIZES.map((size: FontSize) => (
                      <option key={size} value={size}>
                        {t(`settings.font_size.${size}`, lang)}
                      </option>
                    ))}
                  </SelectField>
                </FieldGroup>

                {/* Temperature */}
                <FieldGroup label={`${t('settings.temperature', lang)}: ${formData.temperature}`} isDarkMode={isDarkMode}>
                  <input
                    type="range"
                    min={SETTINGS_LIMITS.temperature.min}
                    max={SETTINGS_LIMITS.temperature.max}
                    step={SETTINGS_LIMITS.temperature.step}
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
                    placeholder={t('settings.system_prompt.placeholder', lang)}
                    className={`textarea !text-base h-28 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
                  />
                </FieldGroup>

                <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
                  <FieldGroup
                    label={t('settings.auto_check_updates', lang)}
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
                    label={t('settings.app_reset', lang)}
                    isDarkMode={isDarkMode}
                  >
                    <button
                      type="button"
                      onClick={handleResetApp}
                      disabled={isResettingApp}
                      className="btn-secondary !text-base"
                    >
                      {isResettingApp ? <Loader2 className="animate-spin" size={14} /> : null}
                      {t('settings.app_reset.button', lang)}
                    </button>
                    {resetMessage && (
                      <p className={`mt-2 text-sm ${resetMessageIsError ? 'text-amber-600' : 'text-emerald-600'}`}>
                        {resetMessage}
                      </p>
                    )}
                  </FieldGroup>
                </div>

                <div className={`pt-2 mt-1 border-t ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
                  <FieldGroup
                    label={t('settings.system_paths', lang)}
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
                      <button
                        type="button"
                        onClick={handleOpenDebugLog}
                        className="btn-secondary !text-base"
                      >
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

      {presetDialogMode && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 backdrop-blur-[1px] px-4">
          <div className={`w-full max-w-md rounded-xl overflow-hidden border shadow-xl ${isDarkMode ? 'bg-surface-800 border-surface-700 text-surface-100' : 'bg-white border-surface-200 text-surface-900'}`}>
            <div className={`px-5 py-4 border-b ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
              <h3 className="text-base font-semibold">
                {presetDialogMode === 'new'
                  ? t('settings.prompt_presets.new', lang)
                  : presetDialogMode === 'rename'
                    ? t('settings.prompt_presets.rename', lang)
                    : t('settings.prompt_presets.delete', lang)}
              </h3>
            </div>

            <div className="px-5 py-4 space-y-3">
              {presetDialogMode === 'delete' ? (
                <p className={`text-sm ${isDarkMode ? 'text-surface-300' : 'text-surface-700'}`}>
                  {t('settings.prompt_presets.delete_confirm', lang).replace('{name}', getActivePreset(formData)?.name ?? '')}
                </p>
              ) : (
                <>
                  <label className={`block text-sm font-medium ${isDarkMode ? 'text-surface-200' : 'text-surface-700'}`}>
                    {t('settings.prompt_presets.name_label', lang)}
                  </label>
                  <input
                    type="text"
                    value={presetDialogValue}
                    onChange={(e) => setPresetDialogValue(e.target.value)}
                    className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100' : ''}`}
                    autoFocus
                  />
                </>
              )}
            </div>

            <div className={`px-5 py-4 border-t flex justify-end gap-2 ${isDarkMode ? 'border-surface-700 bg-surface-900/50' : 'border-surface-100 bg-white'}`}>
              <button type="button" onClick={closePresetDialog} className="btn-secondary !text-base">
                {t('settings.cancel', lang)}
              </button>
              <button type="button" onClick={handleConfirmPresetDialog} className="btn-primary !text-base">
                {presetDialogMode === 'delete' ? t('settings.prompt_presets.delete', lang) : t('settings.save', lang)}
              </button>
            </div>
          </div>
        </div>
      )}
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
  onOpen,
  menuBoundaryRef,
  children,
  className = '',
  isDarkMode = false,
}: {
  value: string;
  onChange: (value: string) => void;
  onOpen?: () => void;
  menuBoundaryRef?: React.RefObject<HTMLElement | null>;
  children: React.ReactNode;
  className?: string;
  isDarkMode?: boolean;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const [menuStyle, setMenuStyle] = useState<{ left: number; top: number; width: number; maxHeight: number }>({
    left: 0,
    top: 0,
    width: 0,
    maxHeight: 240,
  });

  const options = useMemo(() => {
    return Children.toArray(children)
      .map((child) => {
        if (!isValidElement(child)) {
          return null;
        }

        const props = child.props as { value?: string; children?: React.ReactNode };
        if (typeof props.value === 'undefined') {
          return null;
        }

        const label = typeof props.children === 'string' || typeof props.children === 'number'
          ? String(props.children)
          : String(props.value);

        return {
          value: String(props.value),
          label,
        };
      })
      .filter((entry): entry is { value: string; label: string } => entry !== null);
  }, [children]);

  const selected = options.find((option) => option.value === value);

  const recalculateMenuPosition = () => {
    const trigger = containerRef.current;
    if (!trigger) {
      return;
    }

    const triggerRect = trigger.getBoundingClientRect();
    const boundaryRect = menuBoundaryRef?.current?.getBoundingClientRect() ?? {
      top: 8,
      right: window.innerWidth - 8,
      bottom: window.innerHeight - 8,
      left: 8,
      width: window.innerWidth - 16,
      height: window.innerHeight - 16,
      x: 8,
      y: 8,
      toJSON: () => ({}),
    };

    const horizontalPadding = 8;
    const verticalPadding = 8;
    const minHeight = 96;
    const preferredHeight = 256;

    const availableBelow = boundaryRect.bottom - triggerRect.bottom - verticalPadding;
    const availableAbove = triggerRect.top - boundaryRect.top - verticalPadding;
    const openBelow = availableBelow >= availableAbove;
    const availablePrimary = openBelow ? availableBelow : availableAbove;
    const maxHeight = Math.max(minHeight, Math.min(preferredHeight, availablePrimary));

    const estimatedMenuHeight = Math.min(preferredHeight, Math.max(40, options.length * 36 + 8));
    const menuHeight = Math.min(maxHeight, estimatedMenuHeight);

    const unclampedLeft = triggerRect.left;
    const maxLeft = boundaryRect.right - triggerRect.width - horizontalPadding;
    const minLeft = boundaryRect.left + horizontalPadding;
    const left = Math.max(minLeft, Math.min(unclampedLeft, maxLeft));
    const top = openBelow
      ? triggerRect.bottom + 6
      : triggerRect.top - menuHeight - 6;

    setMenuStyle({
      left,
      top,
      width: triggerRect.width,
      maxHeight,
    });
  };

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    recalculateMenuPosition();

    const handleOutsideClick = (event: MouseEvent) => {
      const target = event.target as Node | null;
      if (!target) {
        return;
      }

      if (containerRef.current?.contains(target) || menuRef.current?.contains(target)) {
        return;
      }

      setIsOpen(false);
    };

    const handleReposition = () => {
      recalculateMenuPosition();
    };

    window.addEventListener('mousedown', handleOutsideClick);
    window.addEventListener('resize', handleReposition);
    window.addEventListener('scroll', handleReposition, true);
    return () => {
      window.removeEventListener('mousedown', handleOutsideClick);
      window.removeEventListener('resize', handleReposition);
      window.removeEventListener('scroll', handleReposition, true);
    };
  }, [isOpen, menuBoundaryRef, options.length]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    if (!options.some((option) => option.value === value)) {
      setIsOpen(false);
    }
  }, [isOpen, options, value]);

  const toggleOpen = () => {
    const next = !isOpen;
    setIsOpen(next);
    if (next) {
      onOpen?.();
    }
  };

  const handleSelect = (nextValue: string) => {
    onChange(nextValue);
    setIsOpen(false);
  };

  return (
    <div ref={containerRef} className={`relative ${className}`}>
      <button
        type="button"
        onClick={toggleOpen}
        className={`input !text-base !pr-9 text-left cursor-pointer ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100' : ''}`}
      >
        <span className="block truncate">{selected?.label ?? value}</span>
      </button>
      <div className="absolute inset-y-0 right-0 flex items-center px-3 pointer-events-none">
        <ChevronDown size={16} className={isDarkMode ? 'text-surface-400' : 'text-surface-500'} />
      </div>

      {isOpen && createPortal(
        <div
          ref={menuRef}
          className={`fixed z-[70] rounded-xl border shadow-premium overflow-hidden ${isDarkMode ? 'border-surface-600 bg-surface-800' : 'border-surface-200 bg-white'}`}
          style={{
            left: `${menuStyle.left}px`,
            top: `${menuStyle.top}px`,
            width: `${menuStyle.width}px`,
          }}
        >
          <div className="overflow-y-auto p-1" style={{ maxHeight: `${menuStyle.maxHeight}px` }}>
            {options.map((option) => {
              const active = option.value === value;
              return (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => handleSelect(option.value)}
                  className={`w-full text-left px-3 py-2 rounded-lg text-sm transition-colors ${active
                    ? (isDarkMode ? 'bg-accent-900/40 text-accent-300' : 'bg-accent-50 text-accent-700')
                    : (isDarkMode ? 'text-surface-200 hover:bg-surface-700' : 'text-surface-700 hover:bg-surface-50')
                  }`}
                >
                  {option.label}
                </button>
              );
            })}
          </div>
        </div>,
        document.body,
      )}
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
