import { useEffect, useRef, useState } from 'react';
import { X, Loader2 } from 'lucide-react';
import { invoke } from '../lib/bridge';
import {
  Settings,
  CustomPromptPreset,
  TranslationPromptPreset,
  Provider,
  DocxSettings,
  EditorSettings,
  PROVIDER_DEFAULT_URLS,
  DocxBatchingPart,
  DocxCorrectionScopePart,
} from '../types';
import {
  EU_LANGUAGE_CODES,
  Language,
  defaultCustomPrompt,
  defaultSystemPrompt,
  defaultTranslationMainPrompt,
  defaultTranslationFootnotePrompt,
  normalizeLanguage,
  targetLanguageSlug,
  t,
} from '../i18n';
import { FieldGroup } from './settings/shared';
import { PresetDialogMode } from './settings/PromptPresetEditor';
import { GeneralSection } from './settings/GeneralSection';
import { AdvancedSection } from './settings/AdvancedSection';
import { CorrectionSection } from './settings/CorrectionSection';
import { TranslationSection } from './settings/TranslationSection';

interface SettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
  settings: Settings | null;
  onSave: (settings: Settings) => void;
  onPreviewUiLanguageChange: (language: Language) => void;
  onResetSettings: () => Promise<Settings>;
  onRerunWizard: () => Promise<void> | void;
  onConfigureOllama: () => Promise<void> | void;
  onCheckUpdates: () => Promise<{ status: 'update-available' | 'up-to-date' | 'error'; message: string }>;
  lang: Language;
  isDarkMode?: boolean;
}

type SectionType = 'general' | 'correction' | 'translation' | 'advanced';
const LEGACY_DEFAULT_PROMPTS = [
  'Correct the following text while maintaining the style and tone.',
  'Korrigiere den folgenden Text nach den Duden-Regeln. Korrigiere nur Fehler, alles andere lässt Du unverändert!',
  'Korrigiere den folgenden Text nach den offiziellen Regeln. Korrigiere nur Fehler, alles andere lässt Du unverändert!',
];

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
  onPreviewUiLanguageChange,
  onResetSettings,
  onRerunWizard,
  onConfigureOllama,
  onCheckUpdates,
  lang,
  isDarkMode = false,
}: SettingsModalProps) {
  const modalPanelRef = useRef<HTMLDivElement | null>(null);
  const [formData, setFormDataRaw] = useState<Settings | null>(settings);
  const [activeSection, setActiveSection] = useState<SectionType>('general');
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

  // Correction prompt preset UI state
  const [presetMessage, setPresetMessage] = useState('');
  const [presetDialogMode, setPresetDialogMode] = useState<PresetDialogMode>(null);
  const [presetDialogValue, setPresetDialogValue] = useState('');

  // Translation prompt preset UI state (own namespace, own dialog)
  const [translationPresetMessage, setTranslationPresetMessage] = useState('');
  const [translationPresetDialogMode, setTranslationPresetDialogMode] = useState<PresetDialogMode>(null);
  const [translationPresetDialogValue, setTranslationPresetDialogValue] = useState('');

  const setFormData = (updater: Settings | ((prev: Settings) => Settings)) => {
    setFormDataRaw((prev) => {
      if (!prev) {
        return prev;
      }
      return typeof updater === 'function' ? (updater as (prev: Settings) => Settings)(prev) : updater;
    });
  };

  useEffect(() => {
    if (isOpen) {
      setFormDataRaw(settings);
      setCompareAccessStatus(null);
      setUpdateCheckMessage('');
      setSystemPathMessage('');
      setResetMessage('');
      setResetMessageIsError(false);
      setPresetMessage('');
      setPresetDialogMode(null);
      setPresetDialogValue('');
      setTranslationPresetMessage('');
      setTranslationPresetDialogMode(null);
      setTranslationPresetDialogValue('');
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
        setFormData((prev) => ({ ...prev, model: fetchedModels[0] }));
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
    setFormData((prev) => ({
      ...prev,
      docx: {
        ...prev.docx,
        [key]: value,
      },
    }));
  };

  const handleEditorSettingChange = <K extends keyof EditorSettings>(key: K, value: EditorSettings[K]) => {
    setFormData((prev) => ({
      ...prev,
      editor: {
        ...prev.editor,
        [key]: value,
      },
    }));
  };

  const handleBatchingPartToggle = (part: DocxBatchingPart) => {
    if (!formData) {
      return;
    }

    const current = formData.docx.batching_parts;
    const hasPart = current.includes(part);
    const next = hasPart
      ? current.filter((item) => item !== part)
      : [...current, part];

    if (next.length === 0) {
      return;
    }

    handleDocxSettingChange('batching_parts', next);
  };

  const handleCorrectionScopePartToggle = (part: DocxCorrectionScopePart) => {
    if (!formData) {
      return;
    }

    const current = formData.docx.correction_scope_parts ?? [];
    const hasPart = current.includes(part);
    const next = hasPart
      ? current.filter((item) => item !== part)
      : [...current, part];

    if (next.length === 0) {
      return;
    }

    handleDocxSettingChange('correction_scope_parts', next);
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

  // ================================================================
  // Correction prompt presets. custom_prompt is the field the backend
  // reads for the active mode's main prompt, so writes to it are
  // guarded: while mode === 'translation', translation prompts own
  // that field (see handleMainPromptChange etc. below) — mirrors the
  // guard in tauri/src/main.rs sync_custom_prompt_with_active_preset.
  // ================================================================

  const createDefaultPreset = (language: Language): CustomPromptPreset => ({
    id: `default-${language}`,
    name: language === 'de' ? 'Standard' : 'Default',
    value: defaultCustomPrompt(language),
    locale: language,
  });

  const normalizeDefaultPreset = (preset: CustomPromptPreset, language: Language): CustomPromptPreset => {
    const defaultValue = defaultCustomPrompt(language);
    const isDefaultPreset = preset.id === `default-${language}`;
    const isLegacyDefaultValue = LEGACY_DEFAULT_PROMPTS.includes(preset.value.trim());
    if (!isDefaultPreset && !isLegacyDefaultValue) {
      return preset;
    }

    return {
      ...preset,
      id: isDefaultPreset ? preset.id : `default-${language}`,
      name: language === 'de' ? 'Standard' : 'Default',
      value: defaultValue,
      locale: language,
    };
  };

  const presetsForLanguage = (current: Settings, language = current.correction_language): CustomPromptPreset[] => {
    return current.custom_prompt_presets.filter((preset) => normalizeLanguage(preset.locale) === language);
  };

  const ensureLanguagePreset = (current: Settings, language: Language): Settings => {
    let normalizedPresets = current.custom_prompt_presets.map((preset) =>
      normalizeLanguage(preset.locale) === language ? normalizeDefaultPreset(preset, language) : preset,
    );
    const defaultPreset = createDefaultPreset(language);
    normalizedPresets = normalizedPresets.filter(
      (preset) => !(preset.id === defaultPreset.id && normalizeLanguage(preset.locale) === language),
    );
    normalizedPresets = [...normalizedPresets, defaultPreset];
    const nextPresetIds = {
      ...current.active_custom_prompt_preset_ids,
      [language]: defaultPreset.id,
    };

    return {
      ...current,
      correction_language: language,
      custom_prompt_presets: normalizedPresets,
      active_custom_prompt_preset_id: defaultPreset.id,
      active_custom_prompt_preset_ids: nextPresetIds,
      custom_prompt: current.mode === 'translation' ? current.custom_prompt : defaultPreset.value,
      system_prompt: defaultSystemPrompt(language),
    };
  };

  const getActivePreset = (current: Settings): CustomPromptPreset | undefined => {
    const language = current.correction_language;
    const activeId = current.active_custom_prompt_preset_ids?.[language] ?? current.active_custom_prompt_preset_id;
    return presetsForLanguage(current).find((preset) => preset.id === activeId)
      ?? presetsForLanguage(current)[0];
  };

  const handleCorrectionLanguageChange = (language: Language) => {
    if (!formData) {
      return;
    }

    setPresetMessage('');
    setFormData(ensureLanguagePreset(formData, language));
  };

  const handleUiLanguageChange = (language: Language) => {
    if (!formData) {
      return;
    }

    onPreviewUiLanguageChange(language);
    setFormData({
      ...formData,
      ui_language: language,
    });
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
      active_custom_prompt_preset_ids: {
        ...formData.active_custom_prompt_preset_ids,
        [formData.correction_language]: selected.id,
      },
      custom_prompt: formData.mode === 'translation' ? formData.custom_prompt : selected.value,
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
      active_custom_prompt_preset_ids: {
        ...formData.active_custom_prompt_preset_ids,
        [formData.correction_language]: duplicated.id,
      },
      custom_prompt: formData.mode === 'translation' ? formData.custom_prompt : duplicated.value,
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

    const languagePresets = presetsForLanguage(formData);
    if (languagePresets.length <= 1) {
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
      const nextActive = remaining.find((preset) => normalizeLanguage(preset.locale) === formData.correction_language);
      if (!nextActive) {
        closePresetDialog();
        return;
      }

      const nextPresetIds = {
        ...formData.active_custom_prompt_preset_ids,
        [formData.correction_language]: nextActive.id,
      };

      setPresetMessage('');
      setFormData({
        ...formData,
        custom_prompt_presets: remaining,
        active_custom_prompt_preset_id: nextActive.id,
        active_custom_prompt_preset_ids: nextPresetIds,
        custom_prompt: formData.mode === 'translation' ? formData.custom_prompt : nextActive.value,
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
      const activeNow = getActivePreset(formData);
      const newPreset: CustomPromptPreset = {
        id: createPresetId(),
        name: trimmedName,
        value: activeNow?.value ?? defaultCustomPrompt(formData.correction_language),
        locale: formData.correction_language,
      };

      setPresetMessage('');
      setFormData({
        ...formData,
        custom_prompt_presets: [...formData.custom_prompt_presets, newPreset],
        active_custom_prompt_preset_id: newPreset.id,
        active_custom_prompt_preset_ids: {
          ...formData.active_custom_prompt_preset_ids,
          [formData.correction_language]: newPreset.id,
        },
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
      custom_prompt: formData.mode === 'translation' ? formData.custom_prompt : value,
      custom_prompt_presets: formData.custom_prompt_presets.map((preset) =>
        preset.id === activeId ? { ...preset, value } : preset,
      ),
    });
  };

  // ================================================================
  // Translation prompt presets — own namespace, keyed by target-language
  // slug rather than correction_language. custom_prompt /
  // translation.footnote_prompt are only written here while
  // mode === 'translation', mirroring the guard above.
  // ================================================================

  const createDefaultTranslationPreset = (targetLanguage: string): TranslationPromptPreset => ({
    id: `default-${targetLanguageSlug(targetLanguage)}`,
    name: 'Standard',
    locale: targetLanguageSlug(targetLanguage),
    main_prompt: defaultTranslationMainPrompt(lang, targetLanguage),
    footnote_prompt: defaultTranslationFootnotePrompt(lang, targetLanguage),
  });

  const translationPresetsForLanguage = (
    current: Settings,
    targetLanguage = current.translation.target_language,
  ): TranslationPromptPreset[] => {
    const slug = targetLanguageSlug(targetLanguage);
    return current.translation.prompt_presets.filter((preset) => preset.locale === slug);
  };

  const getActiveTranslationPreset = (current: Settings): TranslationPromptPreset | undefined => {
    const slug = targetLanguageSlug(current.translation.target_language);
    const activeId = current.translation.active_preset_ids?.[slug];
    const presets = translationPresetsForLanguage(current);
    return presets.find((preset) => preset.id === activeId) ?? presets[0];
  };

  const ensureTranslationLanguagePreset = (current: Settings, targetLanguage: string): Settings => {
    const slug = targetLanguageSlug(targetLanguage);
    const defaultPreset = createDefaultTranslationPreset(targetLanguage);
    let nextPresets = current.translation.prompt_presets.filter(
      (preset) => !(preset.id === defaultPreset.id && preset.locale === slug),
    );
    nextPresets = [...nextPresets, defaultPreset];

    return {
      ...current,
      translation: {
        ...current.translation,
        target_language: targetLanguage,
        prompt_presets: nextPresets,
        active_preset_ids: { ...current.translation.active_preset_ids, [slug]: defaultPreset.id },
        footnote_prompt: defaultPreset.footnote_prompt,
      },
      custom_prompt: current.mode === 'translation' ? defaultPreset.main_prompt : current.custom_prompt,
    };
  };

  // Flips the experimental-feature gate; turning it off also forces mode back to
  // "correction" so the (now-hidden) main-window mode switch can't leave the app stuck
  // showing translation UI (mirrors the Rust-side safeguard in
  // sync_translation_prompt_with_active_preset).
  const handleToggleTranslationEnabled = () => {
    if (!formData) {
      return;
    }

    const nextEnabled = !formData.translation_enabled;
    setFormData({
      ...formData,
      translation_enabled: nextEnabled,
      mode: nextEnabled ? formData.mode : 'correction',
    });
  };

  const handleTargetLanguageChange = (targetLanguage: string) => {
    if (!formData) {
      return;
    }

    setTranslationPresetMessage('');
    setFormData(ensureTranslationLanguagePreset(formData, targetLanguage));
  };

  // Permanently remembers a free-text "other language" and makes it the active target
  // language. Adding a custom language is an explicit action here in Settings, not a
  // side effect of typing — the main window can only select an already-added language,
  // never add or remove one.
  const handleAddTranslationLanguage = (rawLanguage: string) => {
    if (!formData) {
      return;
    }

    const trimmed = rawLanguage.trim();
    if (!trimmed) {
      return;
    }

    const isKnownCode = EU_LANGUAGE_CODES.includes(trimmed.toLowerCase() as Language);
    const targetLanguage = isKnownCode ? trimmed.toLowerCase() : trimmed;
    const nextCustomLanguages = isKnownCode || formData.translation.custom_languages.some(
      (existing) => existing.trim().toLowerCase() === trimmed.toLowerCase(),
    )
      ? formData.translation.custom_languages
      : [...formData.translation.custom_languages, trimmed];

    setTranslationPresetMessage('');
    setFormData(ensureTranslationLanguagePreset(
      { ...formData, translation: { ...formData.translation, custom_languages: nextCustomLanguages } },
      targetLanguage,
    ));
  };

  // Removes a remembered "other language" entry.
  // If it's the currently selected target language, falls back to English first so the
  // Rust-side sync doesn't just re-add it on save.
  const handleRemoveTranslationLanguage = (language: string) => {
    if (!formData) {
      return;
    }

    const nextCustomLanguages = formData.translation.custom_languages.filter(
      (existing) => existing.trim().toLowerCase() !== language.trim().toLowerCase(),
    );
    const withoutLanguage: Settings = {
      ...formData,
      translation: { ...formData.translation, custom_languages: nextCustomLanguages },
    };

    const isActive = formData.translation.target_language.trim().toLowerCase() === language.trim().toLowerCase();
    if (isActive) {
      setTranslationPresetMessage('');
      setFormData(ensureTranslationLanguagePreset(withoutLanguage, 'en'));
    } else {
      setFormData(withoutLanguage);
    }
  };

  const handleSelectTranslationPreset = (presetId: string) => {
    if (!formData) {
      return;
    }

    const selected = formData.translation.prompt_presets.find((preset) => preset.id === presetId);
    if (!selected) {
      return;
    }

    const slug = targetLanguageSlug(formData.translation.target_language);
    setTranslationPresetMessage('');
    setFormData({
      ...formData,
      translation: {
        ...formData.translation,
        active_preset_ids: { ...formData.translation.active_preset_ids, [slug]: selected.id },
        footnote_prompt: selected.footnote_prompt,
      },
      custom_prompt: formData.mode === 'translation' ? selected.main_prompt : formData.custom_prompt,
    });
  };

  const handleCreateTranslationPreset = () => {
    if (!formData) {
      return;
    }

    setTranslationPresetMessage('');
    setTranslationPresetDialogValue(t('settings.prompt_presets.new_name_default', lang));
    setTranslationPresetDialogMode('new');
  };

  const handleDuplicateTranslationPreset = () => {
    if (!formData) {
      return;
    }

    const active = getActiveTranslationPreset(formData);
    if (!active) {
      return;
    }

    const duplicated: TranslationPromptPreset = {
      ...active,
      id: createPresetId(),
      name: `${active.name} (${t('settings.prompt_presets.copy_suffix', lang)})`,
    };

    const slug = targetLanguageSlug(formData.translation.target_language);
    setTranslationPresetMessage('');
    setFormData({
      ...formData,
      translation: {
        ...formData.translation,
        prompt_presets: [...formData.translation.prompt_presets, duplicated],
        active_preset_ids: { ...formData.translation.active_preset_ids, [slug]: duplicated.id },
        footnote_prompt: duplicated.footnote_prompt,
      },
      custom_prompt: formData.mode === 'translation' ? duplicated.main_prompt : formData.custom_prompt,
    });
  };

  const handleRenameTranslationPreset = () => {
    if (!formData) {
      return;
    }

    const active = getActiveTranslationPreset(formData);
    if (!active) {
      return;
    }

    setTranslationPresetMessage('');
    setTranslationPresetDialogValue(active.name);
    setTranslationPresetDialogMode('rename');
  };

  const handleDeleteTranslationPreset = () => {
    if (!formData) {
      return;
    }

    const languagePresets = translationPresetsForLanguage(formData);
    if (languagePresets.length <= 1) {
      setTranslationPresetMessage(t('settings.prompt_presets.keep_one', lang));
      return;
    }

    const active = getActiveTranslationPreset(formData);
    if (!active) {
      return;
    }

    setTranslationPresetMessage('');
    setTranslationPresetDialogMode('delete');
  };

  const closeTranslationPresetDialog = () => {
    setTranslationPresetDialogMode(null);
    setTranslationPresetDialogValue('');
  };

  const handleConfirmTranslationPresetDialog = () => {
    if (!formData || !translationPresetDialogMode) {
      return;
    }

    const slug = targetLanguageSlug(formData.translation.target_language);

    if (translationPresetDialogMode === 'delete') {
      const active = getActiveTranslationPreset(formData);
      if (!active) {
        closeTranslationPresetDialog();
        return;
      }

      const remaining = formData.translation.prompt_presets.filter((preset) => preset.id !== active.id);
      const nextActive = remaining.find((preset) => preset.locale === slug);
      if (!nextActive) {
        closeTranslationPresetDialog();
        return;
      }

      setTranslationPresetMessage('');
      setFormData({
        ...formData,
        translation: {
          ...formData.translation,
          prompt_presets: remaining,
          active_preset_ids: { ...formData.translation.active_preset_ids, [slug]: nextActive.id },
          footnote_prompt: nextActive.footnote_prompt,
        },
        custom_prompt: formData.mode === 'translation' ? nextActive.main_prompt : formData.custom_prompt,
      });
      closeTranslationPresetDialog();
      return;
    }

    const trimmedName = translationPresetDialogValue.trim();
    if (!trimmedName) {
      setTranslationPresetMessage(t('settings.prompt_presets.empty_name', lang));
      return;
    }

    if (translationPresetDialogMode === 'new') {
      const activeNow = getActiveTranslationPreset(formData);
      const newPreset: TranslationPromptPreset = {
        id: createPresetId(),
        name: trimmedName,
        locale: slug,
        main_prompt: activeNow?.main_prompt ?? defaultTranslationMainPrompt(lang, formData.translation.target_language),
        footnote_prompt: activeNow?.footnote_prompt ?? defaultTranslationFootnotePrompt(lang, formData.translation.target_language),
      };

      setTranslationPresetMessage('');
      setFormData({
        ...formData,
        translation: {
          ...formData.translation,
          prompt_presets: [...formData.translation.prompt_presets, newPreset],
          active_preset_ids: { ...formData.translation.active_preset_ids, [slug]: newPreset.id },
        },
      });
      closeTranslationPresetDialog();
      return;
    }

    const active = getActiveTranslationPreset(formData);
    if (!active) {
      closeTranslationPresetDialog();
      return;
    }

    setTranslationPresetMessage('');
    setFormData({
      ...formData,
      translation: {
        ...formData.translation,
        prompt_presets: formData.translation.prompt_presets.map((preset) =>
          preset.id === active.id ? { ...preset, name: trimmedName } : preset,
        ),
      },
    });
    closeTranslationPresetDialog();
  };

  const handleMainPromptChange = (value: string) => {
    if (!formData) {
      return;
    }

    const slug = targetLanguageSlug(formData.translation.target_language);
    const activeId = formData.translation.active_preset_ids[slug];
    setTranslationPresetMessage('');
    setFormData({
      ...formData,
      custom_prompt: formData.mode === 'translation' ? value : formData.custom_prompt,
      translation: {
        ...formData.translation,
        prompt_presets: formData.translation.prompt_presets.map((preset) =>
          preset.id === activeId ? { ...preset, main_prompt: value } : preset,
        ),
      },
    });
  };

  const handleFootnotePromptChange = (value: string) => {
    if (!formData) {
      return;
    }

    const slug = targetLanguageSlug(formData.translation.target_language);
    const activeId = formData.translation.active_preset_ids[slug];
    setTranslationPresetMessage('');
    setFormData({
      ...formData,
      translation: {
        ...formData.translation,
        footnote_prompt: value,
        prompt_presets: formData.translation.prompt_presets.map((preset) =>
          preset.id === activeId ? { ...preset, footnote_prompt: value } : preset,
        ),
      },
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
      setFormDataRaw(resetSettings);
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
            <button onClick={handleClose} className="btn-ghost !p-1.5 !rounded-lg">
              <X size={16} />
            </button>
          </div>

          <div className="p-6 space-y-5 overflow-y-auto">
            <FieldGroup label={t('settings.app_reset', lang)} isDarkMode={isDarkMode}>
              <button type="button" onClick={handleResetApp} disabled={isResettingApp} className="btn-secondary !text-base">
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

          <div className={`sticky bottom-0 px-6 py-4 border-t flex justify-end ${isDarkMode ? 'bg-surface-900/50 border-surface-700' : 'bg-white border-surface-100'}`}>
            <button type="button" onClick={handleClose} className="btn-secondary !text-base">
              {t('error.close', lang)}
            </button>
          </div>
        </div>
      </div>
    );
  }

  const isCustom = formData.provider === 'custom';
  const visibleCorrectionPresets = presetsForLanguage(formData);
  const visibleTranslationPresets = translationPresetsForLanguage(formData);
  const activeTranslationPresetId = formData.translation.active_preset_ids[targetLanguageSlug(formData.translation.target_language)]
    ?? visibleTranslationPresets[0]?.id
    ?? '';

  const sections: { id: SectionType; label: string }[] = [
    { id: 'general', label: t('settings.section.general', lang) },
    { id: 'correction', label: t('settings.section.correction', lang) },
    { id: 'translation', label: t('settings.section.translation', lang) },
    { id: 'advanced', label: t('settings.tab.advanced', lang) },
  ];

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center modal-backdrop animate-fade-in">
      {/* Fixed height (not just max-height) so switching between sections of very different
          length (e.g. General vs. Advanced) doesn't resize the dialog — it always uses the
          height that was previously only the cap. */}
      <div ref={modalPanelRef} className={`card w-full max-w-4xl h-[90vh] overflow-hidden flex flex-col animate-scale-in mx-4 ${isDarkMode ? '!bg-surface-800 !border-surface-700' : ''}`}>
        {/* Header */}
        <div className={`flex items-center justify-between px-6 py-4 border-b ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
          <h2 className={`text-base font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
            {t('settings.title', lang)}
          </h2>
          <button onClick={handleClose} className="btn-ghost !p-1.5 !rounded-lg">
            <X size={16} />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="flex-1 flex min-h-0 overflow-hidden">
          {/* Sidebar */}
          <nav className={`w-40 flex-shrink-0 overflow-y-auto py-3 px-2 space-y-1 border-r ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
            {sections.map((section) => (
              <button
                key={section.id}
                type="button"
                onClick={() => setActiveSection(section.id)}
                className={`w-full text-left px-3 py-2 text-sm font-medium rounded-lg transition-all duration-200 ${
                  activeSection === section.id
                    ? (isDarkMode ? 'bg-accent-900/40 text-accent-300 shadow-premium' : 'bg-accent-50 text-accent-700 shadow-premium')
                    : (isDarkMode ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-700' : 'text-surface-600 hover:text-surface-700 hover:bg-surface-50')
                }`}
              >
                {section.label}
              </button>
            ))}
          </nav>

          {/* Content */}
          <div className="flex-1 flex flex-col min-h-0">
            <div className="flex-1 overflow-y-auto p-6 space-y-5">
              {activeSection === 'general' ? (
                <GeneralSection
                  formData={formData}
                  setFormData={setFormData}
                  isDarkMode={isDarkMode}
                  lang={lang}
                  menuBoundaryRef={modalPanelRef}
                  models={models}
                  isLoadingModels={isLoadingModels}
                  modelError={modelError}
                  onModelDropdownFocus={handleModelDropdownFocus}
                  onProviderChange={handleProviderChange}
                  onApiKeyChange={handleApiKeyChange}
                  onConfigureOllama={onConfigureOllama}
                  onScopePartToggle={handleCorrectionScopePartToggle}
                />
              ) : activeSection === 'correction' ? (
                <CorrectionSection
                  formData={formData}
                  isDarkMode={isDarkMode}
                  lang={lang}
                  isMac={isMac}
                  menuBoundaryRef={modalPanelRef}
                  onCorrectionLanguageChange={handleCorrectionLanguageChange}
                  onDocxSettingChange={handleDocxSettingChange}
                  isCheckingCompareAccess={isCheckingCompareAccess}
                  compareAccessStatus={compareAccessStatus}
                  onCompareAccessCheck={handleCompareAccessCheck}
                  visiblePresets={visibleCorrectionPresets}
                  presetMessage={presetMessage}
                  presetDialogMode={presetDialogMode}
                  presetDialogValue={presetDialogValue}
                  onPresetDialogValueChange={setPresetDialogValue}
                  onPresetSelect={handleSelectCustomPromptPreset}
                  onPresetCreate={handleCreateCustomPromptPreset}
                  onPresetDuplicate={handleDuplicateCustomPromptPreset}
                  onPresetRename={handleRenameCustomPromptPreset}
                  onPresetDelete={handleDeleteCustomPromptPreset}
                  onPresetDialogConfirm={handleConfirmPresetDialog}
                  onPresetDialogCancel={closePresetDialog}
                  onCustomPromptChange={handleCustomPromptChange}
                  activePresetName={getActivePreset(formData)?.name ?? ''}
                />
              ) : activeSection === 'translation' ? (
                <TranslationSection
                  formData={formData}
                  isDarkMode={isDarkMode}
                  lang={lang}
                  menuBoundaryRef={modalPanelRef}
                  onToggleEnabled={handleToggleTranslationEnabled}
                  onTargetLanguageChange={handleTargetLanguageChange}
                  onAddLanguage={handleAddTranslationLanguage}
                  onRemoveLanguage={handleRemoveTranslationLanguage}
                  visiblePresets={visibleTranslationPresets}
                  activePresetId={activeTranslationPresetId}
                  activePresetName={getActiveTranslationPreset(formData)?.name ?? ''}
                  presetMessage={translationPresetMessage}
                  presetDialogMode={translationPresetDialogMode}
                  presetDialogValue={translationPresetDialogValue}
                  onPresetDialogValueChange={setTranslationPresetDialogValue}
                  onPresetSelect={handleSelectTranslationPreset}
                  onPresetCreate={handleCreateTranslationPreset}
                  onPresetDuplicate={handleDuplicateTranslationPreset}
                  onPresetRename={handleRenameTranslationPreset}
                  onPresetDelete={handleDeleteTranslationPreset}
                  onPresetDialogConfirm={handleConfirmTranslationPresetDialog}
                  onPresetDialogCancel={closeTranslationPresetDialog}
                  onMainPromptChange={handleMainPromptChange}
                  onFootnotePromptChange={handleFootnotePromptChange}
                />
              ) : (
                <AdvancedSection
                  formData={formData}
                  setFormData={setFormData}
                  isDarkMode={isDarkMode}
                  lang={lang}
                  menuBoundaryRef={modalPanelRef}
                  onDocxSettingChange={handleDocxSettingChange}
                  onEditorSettingChange={handleEditorSettingChange}
                  onBatchingPartToggle={handleBatchingPartToggle}
                  onUiLanguageChange={handleUiLanguageChange}
                  isCheckingUpdates={isCheckingUpdates}
                  updateCheckMessage={updateCheckMessage}
                  onCheckUpdates={handleCheckUpdates}
                  isResettingApp={isResettingApp}
                  resetMessage={resetMessage}
                  resetMessageIsError={resetMessageIsError}
                  onResetApp={handleResetApp}
                  onRerunWizard={onRerunWizard}
                  systemPathMessage={systemPathMessage}
                  onOpenTempFolder={handleOpenTempFolder}
                  onOpenSettingsJson={handleOpenSettingsJson}
                  onOpenDebugLog={handleOpenDebugLog}
                />
              )}
            </div>

            {/* Footer */}
            <div className={`px-6 py-4 border-t flex justify-end gap-2 ${isDarkMode ? 'bg-surface-900/50 border-surface-700' : 'bg-white border-surface-100'}`}>
              <button type="button" onClick={handleClose} className="btn-secondary !text-base">
                {t('settings.cancel', lang)}
              </button>
              <button type="submit" className="btn-primary !text-base">
                {t('settings.save', lang)}
              </button>
            </div>
          </div>
        </form>
      </div>
    </div>
  );
}
