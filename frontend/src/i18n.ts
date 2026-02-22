// Internationalization service

export type Language = 'de' | 'en';

// Detect system language
export function detectLanguage(): Language {
  const systemLang = navigator.language.toLowerCase();
  if (systemLang.startsWith('de')) {
    return 'de';
  }
  return 'en';
}

// Translation dictionary
const translations: Record<string, Record<Language, string>> = {
  // App.tsx
  'app.title': {
    de: 'Lingofix',
    en: 'Lingofix',
  },
  'settings.button': {
    de: 'Einstellungen',
    en: 'Settings',
  },
  'toolbar.placeholder': {
    de: 'Geben Sie Ihren Text ein und klicken Sie auf "Korrigieren"',
    en: 'Enter your text and click "Correct"',
  },
  'toolbar.deleted': {
    de: 'Gelöscht',
    en: 'Deleted',
  },
  'toolbar.added': {
    de: 'Hinzugefügt',
    en: 'Added',
  },
  'toolbar.streaming': {
    de: 'Streaming...',
    en: 'Streaming...',
  },
  'button.stop': {
    de: 'Stopp',
    en: 'Stop',
  },
  'button.clear': {
    de: 'Leeren',
    en: 'Clear',
  },
  'button.reject': {
    de: 'Ablehnen',
    en: 'Reject',
  },
  'button.apply': {
    de: 'Übernehmen',
    en: 'Apply',
  },
  'button.correct': {
    de: 'Korrigieren',
    en: 'Correct',
  },
  'button.starting': {
    de: 'Starte...',
    en: 'Starting...',
  },
  'stats.chars': {
    de: 'Zeichen',
    en: 'Characters',
  },
  'stats.words': {
    de: 'Wörter',
    en: 'Words',
  },
  'stats.corrected': {
    de: 'Korrigierter Text',
    en: 'Corrected text',
  },
  'stats.live': {
    de: 'live',
    en: 'live',
  },
  'error.correction': {
    de: 'Fehler bei der Korrektur',
    en: 'Correction error',
  },
  'error.save_settings': {
    de: 'Fehler beim Speichern der Einstellungen',
    en: 'Error saving settings',
  },
  'error.load_settings': {
    de: 'Einstellungen konnten nicht geladen werden.',
    en: 'Could not load settings.',
  },
  'error.empty_result': {
    de: 'Die Korrektur lieferte keinen Text. Bitte pruefen Sie API-Key, Modell und Provider in den Einstellungen.',
    en: 'The correction returned no text. Please verify API key, model, and provider settings.',
  },
  'error.no_changes': {
    de: 'Das Modell hat keine Aenderungen vorgenommen. Der Text ist moeglicherweise bereits fehlerfrei.',
    en: 'The model made no changes. The text may already be error-free.',
  },
  'error.text_too_short': {
    de: 'Bitte geben Sie mindestens 3 Zeichen ein.',
    en: 'Please enter at least 3 characters.',
  },
  'error.close': {
    de: 'Schließen',
    en: 'Close',
  },
  'info.notice': {
    de: 'Hinweis',
    en: 'Notice',
  },
  
  // SettingsModal.tsx
  'settings.title': {
    de: 'Einstellungen',
    en: 'Settings',
  },
  'settings.provider': {
    de: 'Anbieter',
    en: 'Provider',
  },
  'settings.api_url': {
    de: 'API URL',
    en: 'API URL',
  },
  'settings.api_url.placeholder': {
    de: 'https://ihre-api-url.com/v1',
    en: 'https://your-api-url.com/v1',
  },
  'settings.api_url.hint': {
    de: 'Geben Sie Ihre benutzerdefinierte API-URL ein',
    en: 'Enter your custom API URL',
  },
  'settings.api_key': {
    de: 'API Key',
    en: 'API Key',
  },
  'settings.api_key.required': {
    de: 'Erforderlich',
    en: 'Required',
  },
  'settings.api_key.placeholder': {
    de: 'sk-...',
    en: 'sk-...',
  },
  'settings.api_key.optional': {
    de: 'Optional für Ollama',
    en: 'Optional for Ollama',
  },
  'settings.model': {
    de: 'Modell',
    en: 'Model',
  },
  'settings.model.load': {
    de: 'Modelle laden',
    en: 'Load models',
  },
  'settings.model.error': {
    de: 'Fehler beim Laden der Modelle',
    en: 'Error loading models',
  },
  'settings.prompt': {
    de: 'Prompt',
    en: 'Prompt',
  },
  'settings.prompt.placeholder': {
    de: 'Geben Sie Ihren Prompt ein...',
    en: 'Enter your prompt...',
  },
  'settings.prompt.hint': {
    de: 'Der Text, den Sie korrigieren möchten, wird automatisch an den Prompt angehängt.',
    en: 'The text you want to correct will be automatically appended to the prompt.',
  },
  'settings.prompt.default': {
    de: 'Korrigiere den folgenden Text auf Fehler.',
    en: 'Correct the following text for mistakes.',
  },
  'settings.system_prompt': {
    de: 'System-Prompt',
    en: 'System Prompt',
  },
  'settings.system_prompt.hint': {
    de: 'Wird automatisch unter den Prompt angehängt und gilt für Editor und DOCX.',
    en: 'Appended to the prompt and used for both editor and DOCX.',
  },
  'settings.system_prompt.value': {
    de: 'Wichtig: Antworte nur mit dem korrigierten Text. Keine Erklärungen, keine Notizen, keine zusätzlichen Sätze.',
    en: 'Important: Respond with the corrected text only. No explanations, no notes, no extra sentences.',
  },
  'settings.cancel': {
    de: 'Abbrechen',
    en: 'Cancel',
  },
  'settings.save': {
    de: 'Speichern',
    en: 'Save',
  },
  'settings.url_required': {
    de: 'Bitte geben Sie eine API-URL ein',
    en: 'Please enter an API URL',
  },
  
  // TextEditor.tsx
  'editor.placeholder': {
    de: 'Fügen Sie hier Ihren Text ein...',
    en: 'Paste your text here...',
  },
  'editor.removeFile': {
    de: 'Datei entfernen',
    en: 'Remove file',
  },
  'editor.docxHint': {
    de: 'Klicken Sie auf "Korrigieren" um die DOCX-Datei zu korrigieren',
    en: 'Click "Correct" to correct the DOCX file',
  },
  'editor.browse': {
    de: 'Oder klicken, um eine Datei auszuwählen',
    en: 'Or click to choose a file',
  },
  'editor.dropHere': {
    de: 'Hier ablegen',
    en: 'Drop here',
  },
  'editor.dropDocx': {
    de: 'DOCX-Datei hier ablegen',
    en: 'Drop DOCX file here',
  },
  'editor.dropDocxHint': {
    de: 'Ziehen Sie eine .docx-Datei in diesen Bereich',
    en: 'Drag a .docx file into this area',
  },

  // App.tsx - Docx
  'docx.processing': {
    de: 'Verarbeite...',
    en: 'Processing...',
  },
  'docx.complete': {
    de: 'Korrektur abgeschlossen',
    en: 'Correction complete',
  },
  'docx.trackChanges': {
    de: 'Änderungen verfolgen',
    en: 'Track Changes',
  },
  'docx.trackChanges.created': {
    de: 'Erstellt',
    en: 'Created',
  },
  'docx.trackChanges.none': {
    de: 'Nicht verfügbar',
    en: 'Not available',
  },
  'docx.outputPath': {
    de: 'Ausgabedatei:',
    en: 'Output file:',
  },
  'docx.error': {
    de: 'Fehler bei der DOCX-Korrektur',
    en: 'Error during DOCX correction',
  },
  'docx.openFolder': {
    de: 'Ordner öffnen',
    en: 'Open folder',
  },
  'docx.logs': {
    de: 'Logs',
    en: 'Logs',
  },
  'docx.clearLogs': {
    de: 'Leeren',
    en: 'Clear',
  },
  'docx.logs.empty': {
    de: 'Noch keine Logs.',
    en: 'No logs yet.',
  },
  'docx.cancelled': {
    de: 'DOCX-Verarbeitung wurde gestoppt.',
    en: 'DOCX processing was stopped.',
  },
  'docx.warning': {
    de: 'Hinweis',
    en: 'Notice',
  },
  'docx.diff_mode.accept_existing.title': {
    de: 'Vorhandene Aenderungen gefunden',
    en: 'Existing changes detected',
  },
  'docx.diff_mode.accept_existing.message': {
    de: 'Im Diff-Modus kann die Korrektur nur fortgesetzt werden, wenn Lingofix vorab alle vorhandenen Track Changes automatisch akzeptiert.\n\nMoechten Sie fortfahren?',
    en: 'In diff mode, correction can continue only if Lingofix automatically accepts all existing track changes before correction.\n\nDo you want to continue?',
  },
  'docx.diff_mode.accept_existing.cancel': {
    de: 'Abbrechen',
    en: 'Cancel',
  },
  'docx.diff_mode.accept_existing.continue': {
    de: 'Fortfahren',
    en: 'Continue',
  },
  'docx.diff_mode.accept_existing.cancelled': {
    de: 'DOCX-Korrektur im Diff-Modus wurde abgebrochen.',
    en: 'DOCX correction in diff mode was cancelled.',
  },

  // Settings Modal - Tabs
  'settings.tab.general': {
    de: 'Allgemein',
    en: 'General',
  },
  'settings.tab.docx': {
    de: 'Docx',
    en: 'Docx',
  },
  'settings.tab.advanced': {
    de: 'Erweitert',
    en: 'Advanced',
  },
  
  // Settings Modal - Docx
  'settings.docx.compare_mode': {
    de: 'Vergleichsmodus',
    en: 'Compare mode',
  },
  'settings.docx.compare_mode.diff': {
    de: 'Diff-Engine (integriert)',
    en: 'Diff-Engine (built-in)',
  },
  'settings.docx.compare_mode.word': {
    de: 'Word (MS Word erforderlich)',
    en: 'Word (requires MS Word)',
  },
  'settings.docx.word_check.hint': {
    de: 'Pruefen Sie den Word-Zugriff einmalig, damit macOS die Berechtigungen fuer diesen Modus setzen kann.',
    en: 'Run this one-time Word access check so macOS can grant permissions for this mode.',
  },
  'settings.docx.word_check.button': {
    de: 'Word-Zugriff pruefen',
    en: 'Check Word access',
  },
  'settings.docx.word_check.failed': {
    de: 'Word-Zugriff konnte nicht geprueft werden.',
    en: 'Could not verify Word access.',
  },
  'settings.temperature': {
    de: 'Temperature',
    en: 'Temperature',
  },
  'settings.docx.batching': {
    de: 'Batching',
    en: 'Batching',
  },
  'settings.docx.batch_prompt': {
    de: 'Korrigiere nur den Text innerhalb der Tags. Gib die Antwort mit exakt denselben Tags und IDs zurück.\nKeine zusätzlichen Zeilen außerhalb der Tags.',
    en: 'Correct only the text inside the tags. Return the response with the exact same tags and IDs.\nNo extra lines outside the tags.',
  },
  'settings.docx.batch_max_chars': {
    de: 'Max. Zeichen pro Batch',
    en: 'Max chars per batch',
  },
  'settings.docx.batch_max_paragraphs': {
    de: 'Max. Absätze pro Batch',
    en: 'Max paragraphs per batch',
  },
  'settings.docx.cache': {
    de: 'Cache',
    en: 'Cache',
  },
  'settings.docx.parallelization': {
    de: 'Parallelisierung',
    en: 'Parallelization',
  },
  'settings.docx.max_parallel_requests': {
    de: 'Max. parallele Anfragen',
    en: 'Max parallel requests',
  },

  // Settings - Font size
  'settings.font_size': {
    de: 'Schriftgröße',
    en: 'Font size',
  },
  'settings.font_size.small': {
    de: 'Klein',
    en: 'Small',
  },
  'settings.font_size.default': {
    de: 'Standard',
    en: 'Default',
  },
  'settings.font_size.large': {
    de: 'Groß',
    en: 'Large',
  },
  'settings.font_size.xl': {
    de: 'Sehr groß',
    en: 'Extra large',
  },
  'settings.font_size.xxl': {
    de: 'Riesig',
    en: 'Huge',
  },
};

// Translate function
export function t(key: string, lang: Language = detectLanguage()): string {
  const translation = translations[key];
  if (!translation) {
    console.warn(`Missing translation for key: ${key}`);
    return key;
  }
  return translation[lang] || translation['en'];
}
