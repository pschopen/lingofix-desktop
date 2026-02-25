import { useState, useEffect, useCallback, useRef } from 'react';
import { invoke, listen } from './lib/bridge';
import { Settings as SettingsIcon, Loader2, Trash2, AlertCircle, AlertTriangle, X, FileText, CheckCircle2, FolderOpen, Sparkles, Moon, Sun, XCircle, Check, ChevronDown, ChevronUp, Terminal, ExternalLink } from 'lucide-react';
import { TextEditor } from './components/TextEditor';
import { SettingsModal } from './components/SettingsModal';
import { Settings, FontSize } from './types';
import { t, detectLanguage } from './i18n';
import { useDocxState } from './hooks/useDocxState';
import { useCorrectionState } from './hooks/useCorrectionState';

const DOCX_COMPARE_FALLBACK_MANUAL_HINT = 'Returning corrected file without generated track changes. You can run the document comparison manually in your office application.';
const UPDATE_CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000;
const UPDATE_CHECK_STORAGE_KEY = 'lingofix.last_update_check_at';
const UPDATE_CHECK_ETAG_STORAGE_KEY = 'lingofix.update_check_etag';
const UPDATE_CHECK_RELEASE_STORAGE_KEY = 'lingofix.update_check_release';
const GITHUB_LATEST_RELEASE_API = 'https://api.github.com/repos/pschopen/Lingofix/releases/latest';
const GITHUB_RELEASES_PAGE = 'https://github.com/pschopen/Lingofix/releases';

type UpdateNotice = {
  version: string;
  url: string;
};

type UpdateCheckResult = {
  status: 'update-available' | 'up-to-date' | 'error';
  message: string;
};

type CachedRelease = {
  tag_name: string;
  html_url: string;
};

function parseVersionParts(raw: string): number[] {
  const normalized = raw.trim().replace(/^v/i, '').split('-')[0];
  return normalized
    .split('.')
    .map((part) => Number.parseInt(part, 10))
    .map((part) => (Number.isFinite(part) ? part : 0));
}

function compareVersions(a: string, b: string): number {
  const left = parseVersionParts(a);
  const right = parseVersionParts(b);
  const len = Math.max(left.length, right.length);
  for (let i = 0; i < len; i += 1) {
    const l = left[i] ?? 0;
    const r = right[i] ?? 0;
    if (l > r) return 1;
    if (l < r) return -1;
  }
  return 0;
}

function loadCachedRelease(): CachedRelease | null {
  try {
    const raw = window.localStorage.getItem(UPDATE_CHECK_RELEASE_STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<CachedRelease>;
    const tagName = (parsed.tag_name ?? '').toString().trim();
    const htmlUrl = (parsed.html_url ?? '').toString().trim();
    if (!tagName) {
      return null;
    }

    return {
      tag_name: tagName,
      html_url: htmlUrl || GITHUB_RELEASES_PAGE,
    };
  } catch {
    return null;
  }
}

function saveCachedRelease(release: CachedRelease): void {
  try {
    window.localStorage.setItem(UPDATE_CHECK_RELEASE_STORAGE_KEY, JSON.stringify(release));
  } catch {
    // ignore localStorage write errors
  }
}

function App() {
  const [lang] = useState(detectLanguage());
  const [infoMessage, setInfoMessage] = useState<string | null>(null);
  const [showDiffModeConsent, setShowDiffModeConsent] = useState(false);
  const {
    text,
    setText,
    correctedText,
    setCorrectedText,
    isCorrecting,
    setIsCorrecting,
    isStreaming,
    setIsStreaming,
    showDiff,
    setShowDiff,
    error,
    setError,
    clearDiff,
    applyDiff,
    clearAll,
  } = useCorrectionState();
  const {
    docxFile,
    docxProgress,
    docxResult,
    docxWarning,
    docxLogs,
    showLogs,
    setShowLogs,
    setDocxSelection,
    setDocxProgress,
    setDocxResult,
    setDocxWarning,
    setDocxLogs,
    appendDocxLog,
    resetDocxRunState,
  } = useDocxState();
  const [isSettingsOpen, setIsSettingsOpen] = useState(false);
  const [isDarkMode, setIsDarkMode] = useState(false);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [updateNotice, setUpdateNotice] = useState<UpdateNotice | null>(null);
  const shownUpdateVersionRef = useRef<string | null>(null);

  // Apply font-size CSS custom property to document root
  useEffect(() => {
    const FONT_SIZE_MAP: Record<FontSize, number> = {
      small: 14,
      default: 16,
      large: 18,
      xl: 20,
      xxl: 22,
    };
    
    const fontSize = (settings?.font_size as FontSize | undefined) || 'default';
    const px = FONT_SIZE_MAP[fontSize] || 16;
    document.documentElement.style.setProperty('--fs-base', `${px}px`);
    // Set font-size on html element so Tailwind's rem-based classes respond correctly
    document.documentElement.style.fontSize = `${px}px`;
  }, [settings?.font_size]);
  const logsEndRef = useRef<HTMLDivElement>(null);
  const textRef = useRef(text);

  useEffect(() => {
    textRef.current = text;
  }, [text]);

  const loadSettings = useCallback(async () => {
    try {
      const loaded = await invoke<Settings>('load_settings');
      setSettings(loaded);
    } catch (error) {
      console.error('Failed to load settings:', error);
      try {
        const defaults = await invoke<Settings>('get_default_settings');
        setSettings(defaults);
      } catch {
        setSettings(null);
      }
      setError(t('error.load_settings_reset', lang));
    }
  }, [lang, setError]);

  const runUpdateCheck = useCallback(async ({ manual = false, force = false }: { manual?: boolean; force?: boolean } = {}): Promise<UpdateCheckResult> => {
    if (!settings) {
      return { status: 'error', message: t('error.load_settings_reset', lang) };
    }

    if (!manual && !settings.auto_check_updates) {
      return { status: 'up-to-date', message: t('update.none', lang) };
    }

    if (!force) {
      try {
        const lastCheckRaw = window.localStorage.getItem(UPDATE_CHECK_STORAGE_KEY);
        const lastCheck = lastCheckRaw ? Number.parseInt(lastCheckRaw, 10) : 0;
        if (Number.isFinite(lastCheck) && lastCheck > 0 && Date.now() - lastCheck < UPDATE_CHECK_INTERVAL_MS) {
          return { status: 'up-to-date', message: t('update.none', lang) };
        }
      } catch {
        // ignore localStorage read errors
      }
    }

    try {
      window.localStorage.setItem(UPDATE_CHECK_STORAGE_KEY, String(Date.now()));
    } catch {
      // ignore localStorage write errors
    }

    try {
      const currentVersion = await invoke<string>('get_app_version');
      const cachedEtag = (() => {
        try {
          return window.localStorage.getItem(UPDATE_CHECK_ETAG_STORAGE_KEY)?.trim() || '';
        } catch {
          return '';
        }
      })();
      const headers: HeadersInit = { Accept: 'application/vnd.github+json' };
      if (cachedEtag) {
        headers['If-None-Match'] = cachedEtag;
      }
      let response = await fetch(GITHUB_LATEST_RELEASE_API, {
        headers,
        cache: 'no-store',
      });

      if (response.status === 304) {
        const cachedRelease = loadCachedRelease();
        if (!cachedRelease) {
          response = await fetch(GITHUB_LATEST_RELEASE_API, {
            headers: { Accept: 'application/vnd.github+json' },
            cache: 'no-store',
          });
        } else {
          const remoteVersion = cachedRelease.tag_name;
          const releaseUrl = cachedRelease.html_url || GITHUB_RELEASES_PAGE;
          const hasUpdate = compareVersions(remoteVersion, currentVersion) > 0;
          if (hasUpdate) {
            if (shownUpdateVersionRef.current !== remoteVersion || manual) {
              shownUpdateVersionRef.current = remoteVersion;
              setUpdateNotice({ version: remoteVersion, url: releaseUrl });
            }

            return {
              status: 'update-available',
              message: t('update.available.message', lang).replace('{version}', remoteVersion),
            };
          }

          return {
            status: 'up-to-date',
            message: t('update.none', lang),
          };
        }
      }

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const responseEtag = response.headers.get('etag')?.trim() || '';
      if (responseEtag) {
        try {
          window.localStorage.setItem(UPDATE_CHECK_ETAG_STORAGE_KEY, responseEtag);
        } catch {
          // ignore localStorage write errors
        }
      }

      const release = await response.json() as { tag_name?: string; html_url?: string };
      const remoteVersion = release.tag_name?.trim() || '';
      const releaseUrl = release.html_url?.trim() || GITHUB_RELEASES_PAGE;

      if (!remoteVersion) {
        throw new Error('Missing release version');
      }

      saveCachedRelease({
        tag_name: remoteVersion,
        html_url: releaseUrl,
      });

      const hasUpdate = compareVersions(remoteVersion, currentVersion) > 0;
      if (hasUpdate) {
        if (shownUpdateVersionRef.current !== remoteVersion || manual) {
          shownUpdateVersionRef.current = remoteVersion;
          setUpdateNotice({ version: remoteVersion, url: releaseUrl });
        }

        return {
          status: 'update-available',
          message: t('update.available.message', lang).replace('{version}', remoteVersion),
        };
      }

      return {
        status: 'up-to-date',
        message: t('update.none', lang),
      };
    } catch (error) {
      console.warn('Update check failed:', error);
      return {
        status: 'error',
        message: t('update.check_failed', lang),
      };
    }
  }, [lang, settings]);

  const handleManualUpdateCheck = useCallback(async (): Promise<UpdateCheckResult> => {
    const result = await runUpdateCheck({ manual: true, force: true });
    if (result.status !== 'update-available') {
      setInfoMessage(result.message);
    }
    return result;
  }, [runUpdateCheck]);

  const handleOpenUpdateUrl = useCallback(async (url: string) => {
    try {
      await invoke('open_external_url', { url });
    } catch (error) {
      setInfoMessage(String(error));
    }
  }, []);

  useEffect(() => {
    loadSettings();
    
    const unlistenStarted = listen('correction_started', () => {
      setError(null);
      setInfoMessage(null);
      setIsStreaming(true);
      setIsCorrecting(true);
      setShowDiff(true);
      setCorrectedText('');
    });
    
    const unlistenChunk = listen<string>('correction_chunk', (event) => {
      setCorrectedText(event.payload);
    });
    
    const unlistenComplete = listen<string>('correction_complete', (event) => {
      const payload = (event.payload ?? '').toString();
      if (!payload.trim()) {
        setInfoMessage(null);
        setError(t('error.empty_result', lang));
        setIsStreaming(false);
        setIsCorrecting(false);
        setShowDiff(false);
        return;
      }

      if (payload === textRef.current) {
        setError(null);
        setInfoMessage(t('error.no_changes', lang));
        setIsStreaming(false);
        setIsCorrecting(false);
        setShowDiff(false);
        return;
      }

      setInfoMessage(null);
      setCorrectedText(payload);
      setIsStreaming(false);
      setIsCorrecting(false);
    });
    
    const unlistenError = listen<string>('correction_error', (event) => {
      console.error('Correction error:', event.payload);
      setInfoMessage(null);
      setError(event.payload);
      setIsStreaming(false);
      setIsCorrecting(false);
      setShowDiff(false);
    });

    const unlistenDocxProgress = listen<{ percent: number; message: string }>('docx_progress', (event) => {
      setDocxProgress(event.payload);
    });

    const unlistenDocxComplete = listen<{ outputPath: string; trackChanges: boolean }>('docx_complete', (event) => {
      setDocxResult(event.payload);
      setIsCorrecting(false);
      setDocxProgress(null);
    });

    const unlistenDocxError = listen<string>('docx_error', (event) => {
      setInfoMessage(null);
      setError(event.payload);
      setIsCorrecting(false);
      setDocxProgress(null);
    });

    const unlistenDocxLog = listen<{ level: string; message: string }>('docx_log', (event) => {
      appendDocxLog(event.payload.level, event.payload.message);
      if (event.payload.level === 'warning') {
        setDocxWarning(event.payload.message);
      }
    });
    
    return () => {
      unlistenStarted.then(fn => fn()).catch(() => {});
      unlistenChunk.then(fn => fn()).catch(() => {});
      unlistenComplete.then(fn => fn()).catch(() => {});
      unlistenError.then(fn => fn()).catch(() => {});
      unlistenDocxProgress.then(fn => fn()).catch(() => {});
      unlistenDocxComplete.then(fn => fn()).catch(() => {});
      unlistenDocxError.then(fn => fn()).catch(() => {});
      unlistenDocxLog.then(fn => fn()).catch(() => {});
    };
  }, [
    appendDocxLog,
    lang,
    loadSettings,
    setCorrectedText,
    setDocxProgress,
    setDocxResult,
    setDocxWarning,
    setError,
    setInfoMessage,
    setIsCorrecting,
    setIsStreaming,
    setShowDiff,
  ]);

  useEffect(() => {
    if (!settings || !settings.auto_check_updates) {
      return;
    }

    void runUpdateCheck();
    const intervalId = window.setInterval(() => {
      void runUpdateCheck();
    }, UPDATE_CHECK_INTERVAL_MS);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [runUpdateCheck, settings]);

  // Auto-scroll to bottom of logs
  useEffect(() => {
    if (showLogs && logsEndRef.current) {
      logsEndRef.current.scrollIntoView({ behavior: 'smooth' });
    }
  }, [docxLogs, showLogs]);

  const handleTextChange = (newText: string) => {
    setText(newText);
    if (infoMessage) {
      setInfoMessage(null);
    }
    if (newText.trim() && docxFile) {
      setDocxSelection(null);
    }
  };

  const handleDocxFile = useCallback((file: { name: string; path: string; size: number; originalPath?: string } | null) => {
    setDocxSelection(file);
    setShowDiffModeConsent(false);
  }, [setDocxSelection]);

  const startDocxCorrection = useCallback(async (acceptExistingTrackChanges: boolean) => {
    if (!docxFile?.path || !settings) {
      return;
    }

    setIsCorrecting(true);
    setDocxProgress({ percent: 0, message: t('docx.processing', lang) });
    resetDocxRunState();
    setError(null);
    setInfoMessage(null);

    try {
      await invoke('correct_docx', {
        filePath: docxFile.path,
        originalPath: docxFile.originalPath,
        acceptExistingTrackChanges,
        settings,
      });
    } catch (error) {
      console.error('DOCX correction failed:', error);
      setError(String(error));
      setIsCorrecting(false);
      setDocxProgress(null);
    }
  }, [docxFile, lang, resetDocxRunState, setDocxProgress, setError, setInfoMessage, setIsCorrecting, settings]);

  const handleCorrect = useCallback(async () => {
    if (!settings) {
      setError(t('error.load_settings_reset', lang));
      return;
    }

    if (docxFile) {
      if (!docxFile.path) return;

      try {
        if (settings.docx.compare_mode === 'openxml') {
          const inspection = await invoke<{ hasTrackChanges: boolean }>('inspect_docx_track_changes', {
            filePath: docxFile.path,
          });

          if (inspection.hasTrackChanges) {
            setShowDiffModeConsent(true);
            return;
          }
        }

        await startDocxCorrection(false);
      } catch (error) {
        console.error('DOCX pre-check failed:', error);
        setError(String(error));
      }
      return;
    }
    
    if (!text.trim()) return;
    if (text.trim().length < 3) {
      setInfoMessage(null);
      setError(t('error.text_too_short', lang));
      return;
    }
    
    try {
      await invoke('correct_text_streaming', {
        text,
        settings,
      });
    } catch (error) {
      console.error('Correction failed:', error);
      setInfoMessage(null);
      setError(String(error));
      setIsCorrecting(false);
      setIsStreaming(false);
      setShowDiff(false);
    }
  }, [
    docxFile,
    lang,
    setError,
    setInfoMessage,
    setIsStreaming,
    setShowDiff,
    settings,
    startDocxCorrection,
    text,
  ]);

  const handleDiffModeConsentContinue = useCallback(async () => {
    setShowDiffModeConsent(false);
    await startDocxCorrection(true);
  }, [startDocxCorrection]);

  const handleDiffModeConsentCancel = useCallback(() => {
    setShowDiffModeConsent(false);
    setInfoMessage(t('docx.diff_mode.accept_existing.cancelled', lang));
  }, [lang]);

  const handleNew = () => {
    clearAll();
    setDocxSelection(null);
    setShowDiffModeConsent(false);
  };

  const handleReject = () => {
    clearDiff();
  };

  const handleApply = () => {
    applyDiff();
  };

  const handleStop = async () => {
    try {
      if (docxFile) {
        await invoke('cancel_docx');
        setIsCorrecting(false);
        setDocxProgress(null);
        appendDocxLog('info', t('docx.cancelled', lang));
      } else {
        await invoke('cancel_correction');
      }
    } catch (error) {
      console.error('Failed to stop correction:', error);
    }
  };

  const handleSaveSettings = async (newSettings: Settings) => {
    try {
      await invoke('save_settings', { settings: newSettings });
      setSettings(newSettings);
      setIsSettingsOpen(false);
    } catch (error) {
      console.error('Failed to save settings:', error);
      setError(String(error));
    }
  };

  const handleResetSettings = useCallback(async (): Promise<Settings> => {
    const reset = await invoke<Settings>('reset_settings');
    setSettings(reset);
    setError(null);
    setInfoMessage(t('settings.app_reset.success', lang));
    return reset;
  }, [lang, setError]);

  const isDocxMode = !!docxFile;
  const hasText = text.trim().length > 0;

  const handleToggleDarkMode = () => {
    document.documentElement.classList.add('theme-switch-no-transition');
    setIsDarkMode((v) => !v);
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        document.documentElement.classList.remove('theme-switch-no-transition');
      });
    });
  };

  const localizeDocxLogMessage = useCallback((message: string) => {
    if (message === DOCX_COMPARE_FALLBACK_MANUAL_HINT) {
      return t('docx.logs.compare_fallback_manual_hint', lang);
    }
    return message;
  }, [lang]);

  return (
    <div className={`h-screen flex flex-col transition-colors duration-200 ${isDarkMode ? 'bg-surface-900 text-surface-50' : 'bg-surface-50 text-surface-900'}`}>
      {/* === Header === */}
      <header className={`flex-shrink-0 border-b transition-colors duration-200 ${isDarkMode ? 'border-surface-700 bg-surface-800' : 'border-surface-200 bg-white'}`}>
        <div className="px-6 h-14 flex items-center justify-between">
            <div className="flex items-center gap-3">
              <div className="w-10 h-10 rounded-[10px] overflow-hidden shadow-sm">
                <svg viewBox="0 0 1024 1024" className="w-full h-full">
                  <defs>
                    <linearGradient id="bgGradient" x1="0%" y1="0%" x2="100%" y2="100%">
                      <stop offset="0%" stopColor="#5B8EF4" />
                      <stop offset="50%" stopColor="#4171E8" />
                      <stop offset="100%" stopColor="#3059D6" />
                    </linearGradient>
                  </defs>
                  <rect width="1024" height="1024" rx="230" fill="url(#bgGradient)"/>
                  <g transform="translate(512, 512)">
                    <path d="M 0 -300 L 50 -75 L 300 0 L 50 75 L 0 300 L -50 75 L -300 0 L -50 -75 Z" 
                          fill="white" 
                          stroke="white" 
                          strokeWidth="14" 
                          strokeLinecap="round" 
                          strokeLinejoin="round"/>
                    <line x1="240" y1="-280" x2="240" y2="-180" stroke="white" strokeWidth="28" strokeLinecap="round"/>
                    <line x1="190" y1="-230" x2="290" y2="-230" stroke="white" strokeWidth="28" strokeLinecap="round"/>
                    <line x1="-240" y1="240" x2="-240" y2="180" stroke="white" strokeWidth="28" strokeLinecap="round"/>
                    <line x1="-290" y1="210" x2="-190" y2="210" stroke="white" strokeWidth="28" strokeLinecap="round"/>
                  </g>
                </svg>
              </div>
              <h1 className={`text-lg font-semibold tracking-tight ${isDarkMode ? 'text-surface-50' : 'text-surface-900'}`}>
                Lingofix
              </h1>
            </div>
            <div className="flex items-center gap-1">
            {/* Dark mode toggle */}
            <button
              onClick={handleToggleDarkMode}
              aria-label="Toggle dark mode"
              className={`p-2 rounded-lg transition-all duration-200 ${
                isDarkMode 
                  ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-700' 
                  : 'text-surface-500 hover:text-surface-700 hover:bg-surface-100'
              }`}
            >
              {isDarkMode ? <Sun size={18} strokeWidth={2} /> : <Moon size={18} strokeWidth={2} />}
            </button>

            <div className={`w-px h-5 mx-1 ${isDarkMode ? 'bg-surface-700' : 'bg-surface-200'}`} />

            <button
              onClick={() => setIsSettingsOpen(true)}
              className={`p-2 rounded-lg transition-all duration-200 ${
                isDarkMode 
                  ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-700' 
                  : 'text-surface-500 hover:text-surface-700 hover:bg-surface-100'
              }`}
              aria-label={t('settings.button', lang)}
            >
              <SettingsIcon size={18} strokeWidth={2} />
            </button>
          </div>
        </div>
      </header>

      {/* === Main Content === */}
      <main className="flex-1 overflow-hidden flex flex-col p-4">
        <div className={`card flex-1 flex flex-col animate-fade-in transition-colors duration-200 ${isDarkMode ? '!bg-surface-800 !border-surface-700' : ''}`}>
          
          {/* --- Toolbar --- */}
          <div className={`flex items-center justify-between h-14 px-5 border-b transition-colors duration-200 ${isDarkMode ? 'border-surface-700 bg-surface-800/50' : 'border-surface-100 bg-surface-50/50'}`}>
            <div className={`text-base ${isDarkMode ? 'text-surface-400' : 'text-surface-600'} flex items-center min-w-0`}>
              {docxProgress ? (
                <div className="flex items-center gap-3 w-full flex-1">
                  <Loader2 className={`animate-spin flex-shrink-0 ${isDarkMode ? 'text-accent-400' : 'text-accent-500'}`} size={16} />
                  <div className="flex-1 flex items-center gap-3 min-w-0">
                    <div className={`flex-1 max-w-[200px] rounded-full h-1.5 overflow-hidden ${isDarkMode ? 'bg-surface-700' : 'bg-surface-200'}`}>
                      <div 
                        className={`h-full rounded-full transition-all duration-500 ease-out relative ${isDarkMode ? 'bg-accent-500' : 'bg-accent-500'}`}
                        style={{ width: `${docxProgress.percent}%` }}
                      >
                        <div className="absolute inset-0 progress-shimmer rounded-full" />
                      </div>
                    </div>
                    <span className={`text-base font-medium tabular-nums ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`}>{docxProgress.percent}%</span>
                    <span className={`text-base truncate ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>{docxProgress.message}</span>
                  </div>
                  
                  {/* Log Toggle Button */}
                  <button
                    onClick={() => setShowLogs(!showLogs)}
                    className={`flex items-center gap-1.5 px-2.5 py-1 rounded-md text-sm font-medium transition-colors ${
                      showLogs 
                        ? (isDarkMode ? 'bg-surface-700 text-accent-400' : 'bg-surface-200 text-accent-600')
                        : (isDarkMode ? 'text-surface-400 hover:text-surface-200 hover:bg-surface-800' : 'text-surface-500 hover:text-surface-700 hover:bg-surface-100')
                    }`}
                  >
                    <Terminal size={14} />
                    <span>{docxLogs.length}</span>
                    {showLogs ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
                  </button>
                </div>
              ) : showDiff ? (
                <div className="flex items-center gap-3">
                  <span className="inline-flex items-center gap-1.5 text-base">
                    <span className={`w-2.5 h-2.5 rounded-sm ${isDarkMode ? 'bg-red-900/40 border-red-800/60' : 'bg-red-100 border-red-200'} border`}></span>
                    <span className={isDarkMode ? 'text-surface-300' : 'text-surface-600'}>{t('toolbar.deleted', lang)}</span>
                  </span>
                  <span className="inline-flex items-center gap-1.5 text-base">
                    <span className={`w-2.5 h-2.5 rounded-sm ${isDarkMode ? 'bg-green-900/40 border-green-800/60' : 'bg-green-100 border-green-200'} border`}></span>
                    <span className={isDarkMode ? 'text-surface-300' : 'text-surface-600'}>{t('toolbar.added', lang)}</span>
                  </span>
                  {isStreaming && (
                    <span className={`inline-flex items-center gap-1.5 text-base ml-1 ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`}>
                      <Loader2 className="animate-spin" size={14} />
                      {t('toolbar.streaming', lang)}
                    </span>
                  )}
                </div>
              ) : isDocxMode ? (
                <span className={`inline-flex items-center gap-2 text-base font-medium ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`}>
                  <FileText size={16} />
                  {docxFile?.name}
                </span>
              ) : hasText ? (
                <span className={`text-base ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>
                  {text.trim().split(/\s+/).filter(w => w.length > 0).length} {t('stats.words', lang)}, {text.length} {t('stats.chars', lang)}
                </span>
              ) : (
                <span className={`text-base ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>{t('toolbar.placeholder', lang)}</span>
              )}
            </div>

            {/* Action buttons */}
            <div className="flex items-center gap-2 ml-4 flex-shrink-0">
              {isStreaming || (isCorrecting && docxProgress) ? (
                <>
                  <button onClick={handleStop} className="btn-danger !py-2 !text-base">
                    <X size={16} strokeWidth={2.5} />
                    {t('button.stop', lang)}
                  </button>
                </>
              ) : showDiff ? (
                <>
                  <button onClick={handleNew} className="btn-secondary !py-2 !text-base">
                    <Trash2 size={16} />
                    {t('button.clear', lang)}
                  </button>
                  <button onClick={handleReject} className="btn-secondary !py-2 !text-base">
                    <XCircle size={16} />
                    {t('button.reject', lang)}
                  </button>
                  <button onClick={handleApply} className="btn-success !py-2 !text-base">
                    <Check size={16} strokeWidth={2.5} />
                    {t('button.apply', lang)}
                  </button>
                </>
              ) : (
                <>
                  {(hasText || docxFile) && (
                  <button onClick={handleNew} className="btn-secondary !py-2 !text-base">
                      <Trash2 size={16} />
                      {t('button.clear', lang)}
                    </button>
                  )}
                  <button
                    onClick={handleCorrect}
                    disabled={isCorrecting || (!hasText && !docxFile)}
                    className="btn-primary !py-2 !text-base"
                  >
                    {isCorrecting ? (
                      <>
                        <Loader2 className="animate-spin" size={16} />
                        {t('button.starting', lang)}
                      </>
                    ) : (
                      <>
                        <Sparkles size={16} />
                        {t('button.correct', lang)}
                      </>
                    )}
                  </button>
                </>
              )}
            </div>
          </div>

          {/* --- Log Window --- */}
          {showLogs && (
            <div className={`border-b max-h-48 overflow-hidden flex flex-col ${isDarkMode ? 'bg-surface-900 border-surface-700' : 'bg-surface-50 border-surface-200'}`}>
              <div className={`flex items-center justify-between px-4 py-2 border-b ${isDarkMode ? 'border-surface-800 bg-surface-800/50' : 'border-surface-200 bg-surface-100/50'}`}>
                <span className={`text-sm font-medium ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>
                  {t('docx.logs', lang)}
                </span>
                <button 
                  onClick={() => {
                    setDocxLogs([]);
                    setShowLogs(false);
                  }}
                  className={`text-xs px-2 py-1 rounded ${isDarkMode ? 'text-surface-400 hover:bg-surface-700' : 'text-surface-500 hover:bg-surface-200'}`}
                >
                  {t('docx.clearLogs', lang)}
                </button>
              </div>
              <div className="flex-1 overflow-y-auto p-3 space-y-1.5 font-mono text-sm">
                {docxLogs.length === 0 && (
                  <div className={`${isDarkMode ? 'text-surface-500' : 'text-surface-400'} text-sm`}>{t('docx.logs.empty', lang)}</div>
                )}
                {docxLogs.map((log, index) => {
                  const isBatchingProfile = log.message.startsWith('Batching profile:');
                  return (
                    <div 
                      key={index} 
                      className={`flex items-start gap-2 ${
                        isBatchingProfile
                          ? (isDarkMode ? 'text-cyan-300 bg-cyan-900/20 border border-cyan-800/40 rounded px-2 py-1' : 'text-cyan-800 bg-cyan-50 border border-cyan-200 rounded px-2 py-1')
                          : log.level === 'error' 
                          ? (isDarkMode ? 'text-red-400' : 'text-red-600')
                          : log.level === 'warning'
                          ? (isDarkMode ? 'text-amber-300' : 'text-amber-700')
                          : log.level === 'info'
                          ? (isDarkMode ? 'text-emerald-400' : 'text-emerald-600')
                          : (isDarkMode ? 'text-surface-400' : 'text-surface-600')
                      }`}
                    >
                      <span className={`text-xs opacity-60 flex-shrink-0 mt-0.5`}>
                        {new Date(log.timestamp).toLocaleTimeString(undefined, { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                      </span>
                      <span className={`text-xs px-1.5 py-0.5 rounded flex-shrink-0 uppercase font-bold ${
                        isBatchingProfile
                          ? (isDarkMode ? 'bg-cyan-900/40 text-cyan-200' : 'bg-cyan-100 text-cyan-700')
                          : log.level === 'error'
                          ? (isDarkMode ? 'bg-red-900/30 text-red-300' : 'bg-red-100 text-red-700')
                          : log.level === 'warning'
                          ? (isDarkMode ? 'bg-amber-900/30 text-amber-300' : 'bg-amber-100 text-amber-700')
                          : log.level === 'info'
                          ? (isDarkMode ? 'bg-emerald-900/30 text-emerald-300' : 'bg-emerald-100 text-emerald-700')
                          : (isDarkMode ? 'bg-surface-700 text-surface-300' : 'bg-surface-200 text-surface-600')
                      }`}>
                        {isBatchingProfile ? 'batch' : log.level}
                      </span>
                      <span className="break-all">{localizeDocxLogMessage(log.message)}</span>
                    </div>
                  );
                })}
                <div ref={logsEndRef} />
              </div>
            </div>
          )}

          {/* --- DOCX Result Banner --- */}
          {docxResult && (
            <div className={`px-5 py-3 border-b flex items-center justify-between animate-slide-up ${
              isDarkMode 
                ? 'bg-emerald-900/20 border-emerald-800/40' 
                : 'bg-emerald-50/80 border-emerald-100'
            }`}>
              <div className="flex items-center gap-3 min-w-0">
                <div className={`w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 ${
                  isDarkMode ? 'bg-emerald-900/30' : 'bg-emerald-100'
                }`}>
                  <CheckCircle2 size={16} className={isDarkMode ? 'text-emerald-400' : 'text-emerald-600'} />
                </div>
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className={`text-sm font-medium ${isDarkMode ? 'text-emerald-300' : 'text-emerald-800'}`}>
                      {t('docx.complete', lang)}
                    </span>
                    {docxResult.trackChanges && (
                      <span className={`text-xs font-medium px-1.5 py-0.5 rounded-md ${
                        isDarkMode 
                          ? 'bg-emerald-900/40 text-emerald-300' 
                          : 'bg-emerald-200/60 text-emerald-700'
                      }`}>
                        {t('docx.trackChanges.created', lang)}
                      </span>
                    )}
                  </div>
                  <p className={`text-base truncate mt-0.5 ${
                    isDarkMode ? 'text-emerald-400/80' : 'text-emerald-600/80'
                  }`} title={docxResult.outputPath}>
                    {docxResult.outputPath}
                  </p>
                </div>
              </div>
              <button
                onClick={() => invoke('open_folder', { path: docxResult.outputPath })}
                className={`btn-ghost !py-2 !px-3 !text-base flex-shrink-0 ${
                  isDarkMode 
                    ? '!text-emerald-300 hover:!bg-emerald-900/30' 
                    : '!text-emerald-700 hover:!bg-emerald-100'
                }`}
              >
                <FolderOpen size={14} />
                {t('docx.openFolder', lang)}
              </button>
            </div>
          )}

          {docxWarning && (
            <div className={`px-5 py-3 border-b flex items-start gap-3 animate-slide-up ${
              isDarkMode
                ? 'bg-amber-900/20 border-amber-800/40'
                : 'bg-amber-50/90 border-amber-100'
            }`}>
              <div className={`w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 ${
                isDarkMode ? 'bg-amber-900/30' : 'bg-amber-100'
              }`}>
                <AlertTriangle size={16} className={isDarkMode ? 'text-amber-300' : 'text-amber-700'} />
              </div>
              <div className="min-w-0">
                <span className={`text-sm font-medium ${isDarkMode ? 'text-amber-200' : 'text-amber-900'}`}>
                  {t('docx.warning', lang)}
                </span>
                <p className={`text-sm mt-0.5 break-words ${isDarkMode ? 'text-amber-300/90' : 'text-amber-800/90'}`}>
                  {docxWarning}
                </p>
              </div>
            </div>
          )}

          {updateNotice && (
            <div className={`px-5 py-3 border-b flex items-start gap-3 animate-slide-up ${
              isDarkMode
                ? 'bg-sky-900/20 border-sky-800/40'
                : 'bg-sky-50/90 border-sky-100'
            }`}>
              <div className={`w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 ${
                isDarkMode ? 'bg-sky-900/30' : 'bg-sky-100'
              }`}>
                <AlertCircle size={16} className={isDarkMode ? 'text-sky-300' : 'text-sky-700'} />
              </div>
              <div className="min-w-0 flex-1">
                <span className={`text-sm font-medium ${isDarkMode ? 'text-sky-200' : 'text-sky-900'}`}>
                  {t('update.available', lang)}
                </span>
                <p className={`text-sm mt-0.5 break-words ${isDarkMode ? 'text-sky-300/90' : 'text-sky-800/90'}`}>
                  {t('update.available.message', lang).replace('{version}', updateNotice.version)}
                </p>
                <button
                  onClick={() => handleOpenUpdateUrl(updateNotice.url)}
                  className={`btn-ghost !px-2.5 !py-1.5 !mt-2 !text-sm ${
                    isDarkMode
                      ? '!text-sky-300 hover:!bg-sky-900/30'
                      : '!text-sky-700 hover:!bg-sky-100'
                  }`}
                >
                  <ExternalLink size={14} />
                  {t('update.download', lang)}
                </button>
              </div>
              <button
                onClick={() => setUpdateNotice(null)}
                className="btn-ghost !p-1.5 !rounded-lg flex-shrink-0"
              >
                <X size={16} />
              </button>
            </div>
          )}

          {infoMessage && (
            <div className={`px-5 py-3 border-b flex items-start gap-3 animate-slide-up ${
              isDarkMode
                ? 'bg-blue-900/20 border-blue-800/40'
                : 'bg-blue-50/90 border-blue-100'
            }`}>
              <div className={`w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 ${
                isDarkMode ? 'bg-blue-900/30' : 'bg-blue-100'
              }`}>
                <AlertCircle size={16} className={isDarkMode ? 'text-blue-300' : 'text-blue-700'} />
              </div>
              <div className="min-w-0 flex-1">
                <span className={`text-sm font-medium ${isDarkMode ? 'text-blue-200' : 'text-blue-900'}`}>
                  {t('info.notice', lang)}
                </span>
                <p className={`text-sm mt-0.5 break-words ${isDarkMode ? 'text-blue-300/90' : 'text-blue-800/90'}`}>
                  {infoMessage}
                </p>
              </div>
              <button
                onClick={() => setInfoMessage(null)}
                className="btn-ghost !p-1.5 !rounded-lg flex-shrink-0"
              >
                <X size={16} />
              </button>
            </div>
          )}

          {/* --- Content Area --- */}
          <div className="flex-1 overflow-hidden">
            <TextEditor
              text={text}
              onChange={handleTextChange}
              correctedText={correctedText}
              showDiff={showDiff}
              readOnly={showDiff || !!docxResult}
              isStreaming={isStreaming}
              lang={lang}
              isDarkMode={isDarkMode}
              docxFile={docxFile}
              onDocxFile={handleDocxFile}
              isCorrecting={isCorrecting}
            />
          </div>
        </div>
      </main>

      {/* === Settings Modal === */}
      {settings && (
        <SettingsModal
          isOpen={isSettingsOpen}
          onClose={() => setIsSettingsOpen(false)}
          settings={settings}
          onSave={handleSaveSettings}
          onResetSettings={handleResetSettings}
          onCheckUpdates={handleManualUpdateCheck}
          lang={lang}
          isDarkMode={isDarkMode}
        />
      )}

      {/* === Diff Mode Consent Modal === */}
      {showDiffModeConsent && (
        <div className="fixed inset-0 z-50 flex items-center justify-center modal-backdrop animate-fade-in">
          <div className={`card w-full max-w-xl mx-4 animate-scale-in ${
            isDarkMode ? '!bg-surface-800 !border-surface-700' : ''
          }`}>
            <div className="flex items-start gap-4 p-6">
              <div className={`w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0 ${
                isDarkMode ? 'bg-amber-900/30' : 'bg-amber-50'
              }`}>
                <AlertTriangle className={`w-5 h-5 ${isDarkMode ? 'text-amber-300' : 'text-amber-600'}`} />
              </div>
              <div className="flex-1 min-w-0">
                <h3 className={`text-base font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                  {t('docx.diff_mode.accept_existing.title', lang)}
                </h3>
                <p className={`mt-2 text-sm whitespace-pre-wrap leading-relaxed ${
                  isDarkMode ? 'text-surface-300' : 'text-surface-600'
                }`}>
                  {t('docx.diff_mode.accept_existing.message', lang)}
                </p>
              </div>
            </div>
            <div className={`px-6 py-4 border-t rounded-b-2xl flex justify-end gap-2 ${
              isDarkMode
                ? 'bg-surface-900/50 border-surface-700'
                : 'bg-surface-50 border-surface-100'
            }`}>
              <button
                onClick={handleDiffModeConsentCancel}
                className="btn-secondary !text-base"
              >
                {t('docx.diff_mode.accept_existing.cancel', lang)}
              </button>
              <button
                onClick={handleDiffModeConsentContinue}
                className="btn-primary !text-base"
              >
                {t('docx.diff_mode.accept_existing.continue', lang)}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* === Error Modal === */}
      {error && (
        <div className="fixed inset-0 z-50 flex items-center justify-center modal-backdrop animate-fade-in">
          <div className={`card w-full max-w-lg mx-4 max-h-[80vh] flex flex-col animate-scale-in ${
            isDarkMode ? '!bg-surface-800 !border-surface-700' : ''
          }`}>
            <div className="flex items-start gap-4 p-6">
              <div className={`w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0 ${
                isDarkMode ? 'bg-red-900/30' : 'bg-red-50'
              }`}>
                <AlertCircle className={`w-5 h-5 ${isDarkMode ? 'text-red-400' : 'text-red-500'}`} />
              </div>
              <div className="flex-1 min-w-0">
                <h3 className={`text-base font-semibold ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                  {isDocxMode ? t('docx.error', lang) : t('error.correction', lang)}
                </h3>
                <p className={`mt-2 text-sm whitespace-pre-wrap overflow-y-auto max-h-[40vh] leading-relaxed ${
                  isDarkMode ? 'text-surface-300' : 'text-surface-600'
                }`}>
                  {error}
                </p>
              </div>
              <button
                onClick={() => setError(null)}
                className="btn-ghost !p-1.5 !rounded-lg flex-shrink-0"
              >
                <X size={16} />
              </button>
            </div>
            <div className={`px-6 py-4 border-t rounded-b-2xl flex justify-end ${
              isDarkMode 
                ? 'bg-surface-900/50 border-surface-700' 
                : 'bg-surface-50 border-surface-100'
            }`}>
              <button
                onClick={() => setError(null)}
                className="btn-primary !text-base"
              >
                {t('error.close', lang)}
              </button>
            </div>
          </div>
        </div>
      )}

    </div>
  );
}

export default App;
