import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Loader2,
  Cpu,
  Cloud,
  Check,
  AlertTriangle,
  Download,
  ExternalLink,
  ArrowLeft,
  ArrowRight,
  KeyRound,
  Sparkles,
} from 'lucide-react';
import { invoke, listen } from '../lib/bridge';
import {
  Settings,
  Provider,
  PROVIDER_DEFAULT_URLS,
  PROVIDER_LABELS,
  PROVIDER_API_KEY_URLS,
  ONBOARDING_PROVIDERS,
  ONBOARDING_MODELS,
  DEFAULT_MODELS_FALLBACK,
  PROVIDER_SENTINEL_UNCONFIGURED,
} from '../types';
import { Language, t } from '../i18n';

interface OnboardingProps {
  settings: Settings;
  lang: Language;
  isDarkMode: boolean;
  startStep?: Step;
  onComplete: (settings: Settings) => Promise<void> | void;
  onOpenExternalUrl: (url: string) => Promise<void> | void;
}

type Step = 'welcome' | 'ollama' | 'cloud' | 'done';
type WizardOutcome = 'ollama' | 'cloud' | 'skip' | null;

interface OllamaStatus {
  managedUrl: string;
  ollamaUrl: string;
  ollamaSource: 'system' | 'managed';
  installedAvailable: boolean;
  bundledAvailable: boolean;
  systemAvailable: boolean;
  runningManaged: boolean;
  runningAtDefault: boolean;
  downloadUrl: string;
  registryUrl: string;
  models: { tag: string; label: string; recommended: boolean }[];
  recommendedModel: string;
  totalRamBytes: number;
  installedModels: string[];
}

interface PullProgress {
  model: string;
  raw?: string;
  status?: string;
  total?: number;
  completed?: number;
  percent?: number;
  blobPercent?: number;
  modelTotal?: number;
  modelCompleted?: number;
}

const MODEL_DOWNLOAD_GB: Record<string, number> = {
  'ministral-3:3b': 2,
  'ministral-3:8b': 5,
  'ministral-3:14b': 9,
};

function formatGiB(bytes: number): string {
  const gib = bytes / 1024 / 1024 / 1024;
  if (gib >= 1) {
    return `${gib.toFixed(0)} GB`;
  }
  return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
}

interface CloudProviderState {
  apiKey: string;
  models: string[];
  selectedModel: string;
  testing: boolean;
  error: string | null;
  loaded: boolean;
}

function Onboarding({ settings, lang, isDarkMode, startStep, onComplete, onOpenExternalUrl }: OnboardingProps) {
  const [step, setStep] = useState<Step>(startStep ?? 'welcome');
  const [wizardOutcome, setWizardOutcome] = useState<WizardOutcome>(null);
  const [error, setError] = useState<string | null>(null);
  const [isFinishing, setIsFinishing] = useState(false);

  // Ollama state
  const [ollamaStatus, setOllamaStatus] = useState<OllamaStatus | null>(null);
  const [ollamaLoading, setOllamaLoading] = useState(false);
  const [selectedModel, setSelectedModel] = useState<string>('');
  const [pulling, setPulling] = useState(false);
  const [pullPercent, setPullPercent] = useState<number | null>(null);
  const [pullStatus, setPullStatus] = useState<string>('');
  const [pullModelBytes, setPullModelBytes] = useState<{ completed: number; total: number } | null>(null);
  const [installing, setInstalling] = useState(false);
  const [installPhase, setInstallPhase] = useState<string>('');
  const [installPercent, setInstallPercent] = useState<number | null>(null);

  // Cloud state
  const [selectedProviders, setSelectedProviders] = useState<Provider[]>([]);
  const [providerStates, setProviderStates] = useState<Partial<Record<Provider, CloudProviderState>>>({});
  const [primaryProvider, setPrimaryProvider] = useState<Provider | null>(null);

  const refreshOllamaStatus = useCallback(async () => {
    setOllamaLoading(true);
    try {
      const status = await invoke<OllamaStatus>('ollama_status');
      setOllamaStatus(status);
      setSelectedModel((prev) => prev || status.recommendedModel);
    } catch (err) {
      setError(String(err));
    } finally {
      setOllamaLoading(false);
    }
  }, []);

  const handleInstallOllama = useCallback(async () => {
    setError(null);
    setInstalling(true);
    setInstallPhase('downloading');
    setInstallPercent(0);
    try {
      await invoke('install_ollama');
      await refreshOllamaStatus();
      setInstalling(false);
      setInstallPhase('');
      setInstallPercent(null);
    } catch (err) {
      setError(String(err));
      setInstalling(false);
      setInstallPhase('');
      setInstallPercent(null);
    }
  }, [refreshOllamaStatus]);

  useEffect(() => {
    if (step === 'ollama' && !ollamaStatus) {
      void refreshOllamaStatus();
    }
  }, [step, ollamaStatus, refreshOllamaStatus]);

  useEffect(() => {
    if (!pulling) {
      return;
    }
    let unlistenPull: (() => void) | null = null;
    let unlistenComplete: (() => void) | null = null;
    let unlistenError: (() => void) | null = null;

    (async () => {
      unlistenPull = await listen<PullProgress>('ollama_pull_progress', (event) => {
        const p = event.payload;
        if (typeof p.percent === 'number') {
          setPullPercent(p.percent);
        }
        if (typeof p.modelTotal === 'number' && typeof p.modelCompleted === 'number') {
          setPullModelBytes({ completed: p.modelCompleted, total: p.modelTotal });
        }
        if (p.status) {
          setPullStatus(p.status);
        }
      });
      unlistenComplete = await listen<string>('ollama_pull_complete', () => {
        setPulling(false);
        setPullPercent(100);
        setStep('done');
      });
      unlistenError = await listen<string>('ollama_error', (event) => {
        setError(event.payload);
        setPulling(false);
      });
    })();

    return () => {
      unlistenPull?.();
      unlistenComplete?.();
      unlistenError?.();
    };
  }, [pulling]);

  const ollamaAvailable = useMemo(
    () => Boolean(ollamaStatus?.installedAvailable || ollamaStatus?.bundledAvailable || ollamaStatus?.systemAvailable),
    [ollamaStatus],
  );

  useEffect(() => {
    if (!installing) {
      return;
    }
    let unlistenProgress: (() => void) | null = null;
    let unlistenComplete: (() => void) | null = null;

    (async () => {
      unlistenProgress = await listen<{ phase: string; percent: number }>('ollama_install_progress', (event) => {
        setInstallPhase(event.payload.phase);
        if (typeof event.payload.percent === 'number') {
          setInstallPercent(event.payload.percent);
        }
      });
      unlistenComplete = await listen('ollama_install_complete', () => {
        setInstallPhase('verifying');
      });
    })();

    return () => {
      unlistenProgress?.();
      unlistenComplete?.();
    };
  }, [installing]);

  const handleStartPull = useCallback(async () => {
    if (!selectedModel) {
      return;
    }
    setError(null);
    setPulling(true);
    setPullPercent(null);
    setPullStatus('');
    setPullModelBytes(null);
    try {
      await invoke('ollama_pull_model', {
        model: selectedModel,
        url: ollamaStatus?.ollamaUrl,
      });
    } catch (err) {
      setError(String(err));
      setPulling(false);
      setPullModelBytes(null);
    }
  }, [selectedModel, ollamaStatus?.ollamaUrl]);

  const isModelInstalled = useCallback(
    (tag: string) => {
      if (!ollamaStatus?.installedModels) return false;
      const normalized = tag.toLowerCase();
      return ollamaStatus.installedModels.some((m) => {
        const ml = m.toLowerCase();
        return ml === normalized || ml.startsWith(`${normalized}:`);
      });
    },
    [ollamaStatus],
  );

  const selectedModelInstalled = useMemo(
    () => Boolean(selectedModel && isModelInstalled(selectedModel)),
    [selectedModel, isModelInstalled],
  );

  const handleStartOrContinue = useCallback(async () => {
    setWizardOutcome('ollama');
    if (selectedModelInstalled) {
      setStep('done');
      return;
    }
    await handleStartPull();
  }, [selectedModelInstalled, handleStartPull]);

  const handleOpenUrl = useCallback(
    (url: string) => {
      void onOpenExternalUrl(url);
    },
    [onOpenExternalUrl],
  );

  const toggleProvider = useCallback((provider: Provider) => {
    setSelectedProviders((prev) => {
      if (prev.includes(provider)) {
        setProviderStates((states) => {
          const next = { ...states };
          delete next[provider];
          return next;
        });
        setPrimaryProvider((p) => (p === provider ? prev.find((x) => x !== provider) ?? null : p));
        return prev.filter((x) => x !== provider);
      }
      setProviderStates((states) => ({
        ...states,
        [provider]: { apiKey: '', models: [], selectedModel: '', testing: false, error: null, loaded: false },
      }));
      setPrimaryProvider((p) => p ?? provider);
      return [...prev, provider];
    });
  }, []);

  const updateProviderKey = useCallback((provider: Provider, apiKey: string) => {
    setProviderStates((states) => ({
      ...states,
      [provider]: {
        ...(states[provider] as CloudProviderState),
        apiKey,
        loaded: false,
        error: null,
      },
    }));
  }, []);

  const testProvider = useCallback(async (provider: Provider) => {
    const state = providerStates[provider];
    if (!state || !state.apiKey.trim()) {
      setProviderStates((states) => ({
        ...states,
        [provider]: { ...(states[provider] as CloudProviderState), error: 'API key required' },
      }));
      return;
    }
    setProviderStates((states) => ({
      ...states,
      [provider]: { ...(states[provider] as CloudProviderState), testing: true, error: null },
    }));
    try {
      const models = await invoke<string[]>('fetch_models', {
        apiUrl: PROVIDER_DEFAULT_URLS[provider],
        apiKey: state.apiKey,
        provider,
      });
      const fallback = DEFAULT_MODELS_FALLBACK[provider];
      const preferred = fallback && models.includes(fallback) ? fallback : models[0] ?? '';
      setProviderStates((states) => ({
        ...states,
        [provider]: {
          ...(states[provider] as CloudProviderState),
          models,
          selectedModel: preferred,
          testing: false,
          loaded: true,
          error: models.length === 0 ? 'No models returned' : null,
        },
      }));
    } catch (err) {
      setProviderStates((states) => ({
        ...states,
        [provider]: { ...(states[provider] as CloudProviderState), testing: false, loaded: false, error: String(err) },
      }));
    }
  }, [providerStates]);

  const cloudCanFinish = useMemo(() => {
    if (!primaryProvider) return false;
    const state = providerStates[primaryProvider];
    if (!state) return false;
    return Boolean(state.apiKey.trim() && state.selectedModel);
  }, [primaryProvider, providerStates]);

  const handleFinish = useCallback(async () => {
    if (isFinishing) return;
    setIsFinishing(true);
    setError(null);
    try {
      const updated: Settings = {
        ...settings,
        provider_keys: { ...settings.provider_keys },
      };

      if (wizardOutcome === 'ollama' && ollamaStatus && selectedModel) {
        // Wizard choice wins: override any previously configured cloud provider/model
        updated.provider = 'ollama';
        updated.api_url = ollamaStatus.ollamaUrl;
        updated.api_key = null;
        updated.model = selectedModel;
        updated.provider_keys = { ...updated.provider_keys, ollama: null };
      } else if (wizardOutcome === 'cloud' && primaryProvider) {
        const state = providerStates[primaryProvider];
        if (!state) {
          throw new Error(t('onboarding.cloud.need_one', lang));
        }
        // Wizard choice wins: override any previously configured provider/model
        updated.provider = primaryProvider;
        updated.api_url = PROVIDER_DEFAULT_URLS[primaryProvider];
        updated.api_key = state.apiKey;
        updated.model = state.selectedModel;
        const keys = { ...updated.provider_keys };
        for (const p of selectedProviders) {
          const ps = providerStates[p];
          keys[p] = ps?.apiKey.trim() ? ps.apiKey : null;
        }
        updated.provider_keys = keys;
      } else if (wizardOutcome === 'skip') {
        // Skip path: clear any previously configured active provider so the app
        // shows a "configure a provider" hint instead of attempting to call
        // a provider that has no key.
        updated.provider = PROVIDER_SENTINEL_UNCONFIGURED;
        updated.api_url = '';
        updated.api_key = null;
        updated.model = '';
      }

      updated.setup_completed = true;
      await onComplete(updated);
    } catch (err) {
      setError(String(err));
      setIsFinishing(false);
    }
  }, [isFinishing, settings, wizardOutcome, ollamaStatus, selectedModel, primaryProvider, selectedProviders, providerStates, lang, onComplete]);

  const subTextClass = isDarkMode ? 'text-surface-400' : 'text-surface-600';
  const mutedClass = isDarkMode ? 'text-surface-500' : 'text-surface-400';

  return (
    <div className={`h-screen w-screen flex items-center justify-center p-6 transition-colors duration-200 ${isDarkMode ? 'bg-surface-900' : 'bg-surface-50'}`}>
      <div className={`w-full max-w-2xl card animate-scale-in transition-colors duration-200 ${isDarkMode ? '!bg-surface-800 !border-surface-700' : ''}`}>
        <div className="p-8">
          {/* Header */}
          <div className="flex items-center gap-3 mb-6">
            <div className="w-10 h-10 rounded-[10px] overflow-hidden shadow-sm flex-shrink-0">
              <svg viewBox="0 0 1024 1024" className="w-full h-full">
                <defs>
                  <linearGradient id="bgGradient" x1="0%" y1="0%" x2="100%" y2="100%">
                    <stop offset="0%" stopColor="#5B8EF4" />
                    <stop offset="50%" stopColor="#4171E8" />
                    <stop offset="100%" stopColor="#3059D6" />
                  </linearGradient>
                </defs>
                <rect width="1024" height="1024" rx="230" fill="url(#bgGradient)" />
                <g transform="translate(512, 512)">
                  <path d="M 0 -300 L 50 -75 L 300 0 L 50 75 L 0 300 L -50 75 L -300 0 L -50 -75 Z"
                    fill="white" stroke="white" strokeWidth="14" strokeLinecap="round" strokeLinejoin="round" />
                  <line x1="240" y1="-280" x2="240" y2="-180" stroke="white" strokeWidth="28" strokeLinecap="round" />
                  <line x1="190" y1="-230" x2="290" y2="-230" stroke="white" strokeWidth="28" strokeLinecap="round" />
                  <line x1="-240" y1="240" x2="-240" y2="180" stroke="white" strokeWidth="28" strokeLinecap="round" />
                  <line x1="-290" y1="210" x2="-190" y2="210" stroke="white" strokeWidth="28" strokeLinecap="round" />
                </g>
              </svg>
            </div>
            <div className="flex-1 min-w-0">
              <h1 className={`text-xl font-semibold tracking-tight ${isDarkMode ? 'text-surface-50' : 'text-surface-900'}`}>
                {t('onboarding.title', lang)}
              </h1>
              <p className={`text-sm ${subTextClass}`}>{t('onboarding.subtitle', lang)}</p>
            </div>
          </div>

          {error && (
            <div className={`mb-4 px-4 py-3 rounded-xl flex items-start gap-3 ${
              isDarkMode ? 'bg-red-900/20 border border-red-800/40' : 'bg-red-50 border border-red-200'
            }`}>
              <AlertTriangle className={`w-5 h-5 flex-shrink-0 mt-0.5 ${isDarkMode ? 'text-red-400' : 'text-red-600'}`} />
              <p className={`text-sm ${isDarkMode ? 'text-red-300' : 'text-red-700'}`}>{error}</p>
            </div>
          )}

          {/* WELCOME */}
          {step === 'welcome' && (
            <div className="space-y-4">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <button
                  onClick={() => { setError(null); setStep('ollama'); }}
                  className={`text-left rounded-2xl p-5 border-2 transition-all duration-200 hover:-translate-y-0.5 ${
                    isDarkMode
                      ? 'bg-surface-900/40 border-surface-700 hover:border-accent-500'
                      : 'bg-surface-50 border-surface-200 hover:border-accent-500'
                  }`}
                >
                  <Cpu className={`w-7 h-7 mb-3 ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`} />
                  <h3 className={`text-base font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                    {t('onboarding.choose.local', lang)}
                  </h3>
                  <p className={`text-sm mt-1 ${subTextClass}`}>{t('onboarding.choose.local.desc', lang)}</p>
                </button>
                <button
                  onClick={() => { setError(null); setStep('cloud'); }}
                  className={`text-left rounded-2xl p-5 border-2 transition-all duration-200 hover:-translate-y-0.5 ${
                    isDarkMode
                      ? 'bg-surface-900/40 border-surface-700 hover:border-accent-500'
                      : 'bg-surface-50 border-surface-200 hover:border-accent-500'
                  }`}
                >
                  <Cloud className={`w-7 h-7 mb-3 ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`} />
                  <h3 className={`text-base font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                    {t('onboarding.choose.cloud', lang)}
                  </h3>
                  <p className={`text-sm mt-1 ${subTextClass}`}>{t('onboarding.choose.cloud.desc', lang)}</p>
                </button>
              </div>
              <div className={`flex items-center gap-3 ${mutedClass}`}>
                <div className={`flex-1 h-px ${isDarkMode ? 'bg-surface-700' : 'bg-surface-200'}`} />
                <button
                  onClick={() => { setWizardOutcome('skip'); void handleFinish(); }}
                  disabled={isFinishing}
                  className={`inline-flex items-center gap-2 rounded-full px-4 py-1.5 border transition-colors duration-150 disabled:opacity-50 disabled:cursor-not-allowed ${
                    isDarkMode
                      ? 'bg-surface-900/40 border-surface-700 hover:border-surface-500 text-surface-300 hover:text-surface-100'
                      : 'bg-surface-50 border-surface-200 hover:border-surface-300 text-surface-600 hover:text-surface-800'
                  }`}
                >
                  {isFinishing ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <ArrowRight className="w-3.5 h-3.5" />
                  )}
                  <span className="text-sm font-medium">
                    {t('onboarding.choose.skip', lang)}
                  </span>
                </button>
                <div className={`flex-1 h-px ${isDarkMode ? 'bg-surface-700' : 'bg-surface-200'}`} />
              </div>
              <p className={`text-xs text-center ${mutedClass}`}>
                {t('onboarding.choose.skip.desc', lang)}
              </p>
            </div>
          )}

          {/* OLLAMA */}
          {step === 'ollama' && (
            <div className="space-y-5">
              {ollamaLoading && !ollamaStatus && (
                <div className="flex items-center gap-2 text-sm">
                  <Loader2 className="w-4 h-4 animate-spin" />
                  <span className={subTextClass}>…</span>
                </div>
              )}

              {ollamaStatus && !ollamaAvailable && !installing && (
                <div className={`rounded-xl p-4 border ${
                  isDarkMode ? 'bg-amber-900/20 border-amber-800/40' : 'bg-amber-50 border-amber-200'
                }`}>
                  <div className="flex items-center gap-2 mb-2">
                    <AlertTriangle className={`w-5 h-5 ${isDarkMode ? 'text-amber-300' : 'text-amber-600'}`} />
                    <span className={`text-sm font-semibold ${isDarkMode ? 'text-amber-200' : 'text-amber-900'}`}>
                      {t('onboarding.ollama.not_installed.title', lang)}
                    </span>
                  </div>
                  <p className={`text-sm mb-3 ${isDarkMode ? 'text-amber-300/90' : 'text-amber-800/90'}`}>
                    {t('onboarding.ollama.not_installed.desc', lang)}
                  </p>
                  <div className="flex flex-wrap gap-2">
                    <button className="btn-primary !text-sm" onClick={() => void handleInstallOllama()}>
                      <Download size={15} />
                      {t('onboarding.ollama.install_auto', lang)}
                    </button>
                    <button className="btn-secondary !text-sm" onClick={() => handleOpenUrl(ollamaStatus.downloadUrl)}>
                      <ExternalLink size={15} />
                      {t('onboarding.ollama.install_manual', lang)}
                    </button>
                    <button className="btn-secondary !text-sm" onClick={() => void refreshOllamaStatus()}>
                      {t('onboarding.ollama.retry', lang)}
                    </button>
                  </div>
                </div>
              )}

              {ollamaStatus && !ollamaAvailable && installing && (
                <div className="space-y-3">
                  <div className={`flex items-center gap-2 text-sm ${subTextClass}`}>
                    <Loader2 className="w-4 h-4 animate-spin" />
                    <span>
                      {installPhase === 'extracting'
                        ? t('onboarding.ollama.installing.extracting', lang)
                        : installPhase === 'verifying'
                        ? t('onboarding.ollama.installing.verifying', lang)
                        : t('onboarding.ollama.installing.downloading', lang)}
                    </span>
                    {installPhase === 'downloading' && installPercent != null && (
                      <span className="tabular-nums font-medium ml-auto">{installPercent}%</span>
                    )}
                  </div>
                  {installPhase === 'downloading' && (
                    <div className={`h-2 rounded-full overflow-hidden ${isDarkMode ? 'bg-surface-700' : 'bg-surface-200'}`}>
                      <div
                        className="h-full bg-accent-500 transition-all duration-300"
                        style={{ width: `${installPercent ?? 0}%` }}
                      />
                    </div>
                  )}
                  {(installPhase === 'extracting' || installPhase === 'verifying') && (
                    <p className={`text-xs ${mutedClass}`}>
                      <Loader2 className="w-3 h-3 animate-spin inline mr-1" />
                      {installPhase === 'extracting'
                        ? t('onboarding.ollama.installing.extracting', lang)
                        : t('onboarding.ollama.installing.verifying', lang)}
                    </p>
                  )}
                </div>
              )}

              {ollamaStatus && ollamaAvailable && (
                <>
                  <div className={`flex items-center gap-2 text-sm ${subTextClass}`}>
                    <Cpu size={16} />
                    <span>{t('onboarding.ollama.ram.detected', lang)} {formatGiB(ollamaStatus.totalRamBytes)}</span>
                  </div>

                  <div className={`flex items-center gap-2 text-xs px-3 py-1.5 rounded-lg ${
                    isDarkMode ? 'bg-surface-900/40 text-surface-300' : 'bg-surface-50 text-surface-600'
                  }`}>
                    <span className={`w-1.5 h-1.5 rounded-full ${
                      ollamaStatus.ollamaSource === 'system' ? 'bg-emerald-500' : 'bg-accent-500'
                    }`} />
                    {ollamaStatus.ollamaSource === 'system'
                      ? t('onboarding.ollama.using_system', lang)
                      : t('onboarding.ollama.using_managed', lang)}
                  </div>

                  <div>
                    <label className={`block text-sm font-medium mb-1 ${isDarkMode ? 'text-surface-200' : 'text-surface-700'}`}>
                      {t('onboarding.ollama.recommended_section', lang)}
                    </label>
                    <p className={`text-xs mb-2 ${mutedClass}`}>
                      {t('onboarding.ollama.recommended_section.hint', lang)}
                    </p>
                    <div className="space-y-2">
                      {ONBOARDING_MODELS.map((m) => {
                        const active = selectedModel === m.tag;
                        const recommended = ollamaStatus.recommendedModel === m.tag;
                        const installed = isModelInstalled(m.tag);
                        return (
                          <button
                            key={m.tag}
                            disabled={pulling}
                            onClick={() => setSelectedModel(m.tag)}
                            className={`w-full text-left rounded-xl px-4 py-3 border-2 flex items-center justify-between transition-all ${
                              active
                                ? (isDarkMode ? 'border-accent-500 bg-accent-500/10' : 'border-accent-500 bg-accent-50')
                                : (isDarkMode ? 'border-surface-700 hover:border-surface-500' : 'border-surface-200 hover:border-surface-300')
                            }`}
                          >
                            <div className="flex items-center gap-3">
                              <span className={`w-5 h-5 rounded-full border-2 flex items-center justify-center ${
                                active ? 'border-accent-500' : (isDarkMode ? 'border-surface-600' : 'border-surface-300')
                              }`}>
                                {active && <Check size={12} className="text-accent-500" strokeWidth={3} />}
                              </span>
                              <span className={`text-sm font-medium ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>{m.label}</span>
                              {installed && (
                                <span className={`text-xs px-2 py-0.5 rounded-md font-medium ${
                                  isDarkMode ? 'bg-emerald-500/20 text-emerald-300' : 'bg-emerald-100 text-emerald-700'
                                }`}>
                                  ✓
                                </span>
                              )}
                            </div>
                            <span className="flex items-center gap-2">
                              {recommended && (
                                <span className={`text-xs px-2 py-0.5 rounded-md font-medium ${
                                  isDarkMode ? 'bg-accent-500/20 text-accent-300' : 'bg-accent-100 text-accent-700'
                                }`}>
                                  {t('onboarding.ollama.recommended', lang)}
                                </span>
                              )}
                              {MODEL_DOWNLOAD_GB[m.tag] && (
                                <span className={`text-xs ${mutedClass}`}>~{MODEL_DOWNLOAD_GB[m.tag]} GB</span>
                              )}
                            </span>
                          </button>
                        );
                      })}
                    </div>
                  </div>

                  {ollamaStatus.installedModels.length > 0 && (
                    <div>
                      <label className={`block text-sm font-medium mb-2 ${isDarkMode ? 'text-surface-200' : 'text-surface-700'}`}>
                        {t('onboarding.ollama.installed_models', lang)}
                      </label>
                      <select
                        value={ollamaStatus.installedModels.includes(selectedModel) ? selectedModel : ''}
                        onChange={(e) => setSelectedModel(e.target.value)}
                        disabled={pulling}
                        className={`w-full rounded-xl px-3 py-2.5 text-sm font-medium border transition-colors ${
                          isDarkMode
                            ? 'bg-surface-900/40 border-surface-700 text-surface-100 focus:border-accent-500'
                            : 'bg-surface-50 border-surface-200 text-surface-900 focus:border-accent-500'
                        }`}
                      >
                        <option value="" disabled>
                          {t('onboarding.ollama.installed_models.placeholder', lang)}
                        </option>
                        {ollamaStatus.installedModels.map((tag) => (
                          <option key={tag} value={tag}>
                            {tag}
                          </option>
                        ))}
                      </select>
                    </div>
                  )}

                  {pulling ? (
                    <div className="space-y-2">
                      <div className={`flex items-center justify-between text-sm ${subTextClass}`}>
                        <span className="flex items-center gap-2">
                          <Loader2 className="w-4 h-4 animate-spin" />
                          {t('onboarding.ollama.installing', lang)}
                        </span>
                        <span className="flex items-center gap-2 tabular-nums">
                          {pullModelBytes && pullModelBytes.total > 0 && (
                            <span className={mutedClass}>
                              {formatGiB(pullModelBytes.completed)} / {formatGiB(pullModelBytes.total)}
                            </span>
                          )}
                          {pullPercent != null && <span className="font-medium">{pullPercent}%</span>}
                        </span>
                      </div>
                      <div className={`h-2 rounded-full overflow-hidden ${isDarkMode ? 'bg-surface-700' : 'bg-surface-200'}`}>
                        <div
                          className="h-full bg-accent-500 transition-all duration-300"
                          style={{ width: `${pullPercent ?? 0}%` }}
                        />
                      </div>
                      {pullStatus && <p className={`text-xs ${mutedClass}`}>{pullStatus}</p>}
                    </div>
                  ) : (
                    <button
                      className="btn-primary w-full"
                      disabled={!selectedModel}
                      onClick={() => void handleStartOrContinue()}
                    >
                      {selectedModelInstalled ? (
                        <Check size={16} />
                      ) : (
                        <Sparkles size={16} />
                      )}
                      {selectedModelInstalled
                        ? t('onboarding.ollama.continue', lang)
                        : t('onboarding.ollama.install', lang)}
                    </button>
                  )}

                  <p className={`text-xs ${mutedClass}`}>{t('onboarding.ollama.note', lang)}</p>
                </>
              )}
            </div>
          )}

          {/* CLOUD */}
          {step === 'cloud' && (
            <div className="space-y-4">
              <p className={`text-sm ${subTextClass}`}>{t('onboarding.cloud.desc', lang)}</p>

              <div className="space-y-2">
                {ONBOARDING_PROVIDERS.map((provider) => {
                  const active = selectedProviders.includes(provider);
                  const state = providerStates[provider];
                  const keyUrl = PROVIDER_API_KEY_URLS[provider];
                  return (
                    <div key={provider} className={`rounded-xl border-2 transition-all ${
                      active
                        ? (isDarkMode ? 'border-accent-500 bg-accent-500/5' : 'border-accent-500 bg-accent-50')
                        : (isDarkMode ? 'border-surface-700' : 'border-surface-200')
                    }`}>
                      <div className="flex items-center justify-between px-4 py-3">
                        <button
                          className="flex items-center gap-3 flex-1 min-w-0 text-left"
                          onClick={() => toggleProvider(provider)}
                        >
                          <span className={`w-5 h-5 rounded border-2 flex items-center justify-center flex-shrink-0 ${
                            active ? 'border-accent-500 bg-accent-500' : (isDarkMode ? 'border-surface-600' : 'border-surface-300')
                          }`}>
                            {active && <Check size={12} className="text-white" strokeWidth={3} />}
                          </span>
                          <span className={`text-sm font-medium ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                            {PROVIDER_LABELS[provider]}
                          </span>
                          {primaryProvider === provider && (
                            <span className={`text-xs px-2 py-0.5 rounded-md font-medium ${
                              isDarkMode ? 'bg-accent-500/20 text-accent-300' : 'bg-accent-100 text-accent-700'
                            }`}>
                              {t('onboarding.cloud.select', lang)}
                            </span>
                          )}
                        </button>
                        {keyUrl && (
                          <button
                            className={`btn-ghost !py-1.5 !px-3 !text-xs flex-shrink-0 ${
                              isDarkMode ? '!text-accent-300 hover:!bg-surface-700' : '!text-accent-600 hover:!bg-accent-50'
                            }`}
                            onClick={() => handleOpenUrl(keyUrl)}
                          >
                            <KeyRound size={13} />
                            {t('onboarding.cloud.get_key', lang)}
                            <ExternalLink size={11} />
                          </button>
                        )}
                      </div>

                      {active && state && (
                        <div className="px-4 pb-4 space-y-3">
                          <div className="relative">
                            <input
                              type="password"
                              className="input !py-2 !text-sm"
                              placeholder={t('onboarding.cloud.api_key', lang)}
                              value={state.apiKey}
                              onChange={(e) => updateProviderKey(provider, e.target.value)}
                              disabled={state.testing}
                              autoComplete="off"
                              spellCheck={false}
                            />
                          </div>

                          {!state.loaded && (
                            <button
                              className="btn-secondary !text-sm w-full"
                              disabled={state.testing || !state.apiKey.trim()}
                              onClick={() => void testProvider(provider)}
                            >
                              {state.testing ? (
                                <>
                                  <Loader2 className="w-4 h-4 animate-spin" />
                                  {t('onboarding.cloud.testing', lang)}
                                </>
                              ) : (
                                <>
                                  <Cloud size={15} />
                                  {t('onboarding.cloud.test', lang)}
                                </>
                              )}
                            </button>
                          )}

                          {state.error && (
                            <p className={`text-xs ${isDarkMode ? 'text-red-400' : 'text-red-600'}`}>{state.error}</p>
                          )}

                          {state.loaded && state.models.length > 0 && (
                            <div className="space-y-2">
                              <div className="flex items-center gap-2">
                                <Check size={14} className={isDarkMode ? 'text-emerald-400' : 'text-emerald-600'} />
                                <span className={`text-xs font-medium ${isDarkMode ? 'text-emerald-300' : 'text-emerald-700'}`}>
                                  {t('onboarding.cloud.models_loaded', lang)} ({state.models.length})
                                </span>
                              </div>
                              <label className={`block text-xs font-medium ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>
                                {t('onboarding.ollama.model', lang)}
                              </label>
                              <select
                                className="input !py-2 !text-sm"
                                value={state.selectedModel}
                                onChange={(e) => {
                                  const val = e.target.value;
                                  setProviderStates((s) => ({
                                    ...s,
                                    [provider]: { ...(s[provider] as CloudProviderState), selectedModel: val },
                                  }));
                                }}
                              >
                                {state.models.map((m) => (
                                  <option key={m} value={m}>{m}</option>
                                ))}
                              </select>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>

              <button
                className="btn-primary w-full"
                disabled={!cloudCanFinish || isFinishing}
                onClick={() => { setWizardOutcome('cloud'); void handleFinish(); }}
              >
                {isFinishing ? <Loader2 className="w-4 h-4 animate-spin" /> : <Check size={16} />}
                {t('onboarding.done.finish', lang)}
              </button>
            </div>
          )}

          {/* DONE */}
          {step === 'done' && (
            <div className="text-center py-6">
              <div className={`w-14 h-14 rounded-full mx-auto flex items-center justify-center mb-4 ${
                isDarkMode ? 'bg-emerald-900/30' : 'bg-emerald-100'
              }`}>
                <Check size={28} className={isDarkMode ? 'text-emerald-400' : 'text-emerald-600'} strokeWidth={2.5} />
              </div>
              <h2 className={`text-lg font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                {t('onboarding.done.title', lang)}
              </h2>
              <p className={`text-sm mt-2 mb-6 ${subTextClass}`}>{t('onboarding.done.desc', lang)}</p>
              <button
                className="btn-primary w-full"
                disabled={isFinishing}
                onClick={() => void handleFinish()}
              >
                {isFinishing ? <Loader2 className="w-4 h-4 animate-spin" /> : <Sparkles size={16} />}
                {t('onboarding.done.finish', lang)}
              </button>
            </div>
          )}
        </div>

        {/* Footer nav */}
        {step !== 'welcome' && step !== 'done' && (
          <div className={`px-8 py-4 border-t flex justify-between items-center ${isDarkMode ? 'border-surface-700 bg-surface-900/40' : 'border-surface-100 bg-surface-50/50'}`}>
            <button
              className="btn-ghost !text-sm"
              onClick={() => setStep('welcome')}
            >
              <ArrowLeft size={15} />
              {t('onboarding.back', lang)}
            </button>
            {step === 'ollama' && ollamaAvailable && !pulling && (
              <span className={`text-xs ${mutedClass}`}>{selectedModel}</span>
            )}
            {step === 'cloud' && cloudCanFinish && (
              <span className={`flex items-center gap-1 text-xs ${isDarkMode ? 'text-emerald-300' : 'text-emerald-700'}`}>
                <Check size={12} /> {PROVIDER_LABELS[primaryProvider as Provider]}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

export default Onboarding;