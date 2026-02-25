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
  'error.load_settings_reset': {
    de: 'Einstellungen konnten nicht geladen werden. Öffnen Sie Einstellungen > Erweitert und wählen Sie "App zurücksetzen".',
    en: 'Could not load settings. Open Settings > Advanced and choose "Reset app".',
  },
  'error.empty_result': {
    de: 'Die Korrektur lieferte keinen Text. Bitte prüfen Sie API-Key, Modell und Provider in den Einstellungen.',
    en: 'The correction returned no text. Please verify API key, model, and provider settings.',
  },
  'error.no_changes': {
    de: 'Das Modell hat keine Änderungen vorgenommen. Der Text ist möglicherweise bereits fehlerfrei.',
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
  'settings.prompt_presets': {
    de: 'Prompt',
    en: 'Prompt',
  },
  'settings.prompt_presets.select_label': {
    de: 'Vorlage auswählen',
    en: 'Select preset',
  },
  'settings.prompt_presets.editor_label': {
    de: 'Inhalt der aktiven Vorlage',
    en: 'Content of active preset',
  },
  'settings.prompt_presets.name_label': {
    de: 'Name',
    en: 'Name',
  },
  'settings.prompt_presets.new': {
    de: 'Neu',
    en: 'New',
  },
  'settings.prompt_presets.duplicate': {
    de: 'Duplizieren',
    en: 'Duplicate',
  },
  'settings.prompt_presets.rename': {
    de: 'Umbenennen',
    en: 'Rename',
  },
  'settings.prompt_presets.delete': {
    de: 'Löschen',
    en: 'Delete',
  },
  'settings.prompt_presets.new_name_prompt': {
    de: 'Name der neuen Vorlage:',
    en: 'Name of the new preset:',
  },
  'settings.prompt_presets.new_name_default': {
    de: 'Neue Vorlage',
    en: 'New preset',
  },
  'settings.prompt_presets.rename_prompt': {
    de: 'Neuer Name für die Vorlage:',
    en: 'New name for the preset:',
  },
  'settings.prompt_presets.delete_confirm': {
    de: 'Vorlage "{name}" wirklich löschen?',
    en: 'Delete preset "{name}"?',
  },
  'settings.prompt_presets.keep_one': {
    de: 'Mindestens eine Vorlage muss erhalten bleiben.',
    en: 'At least one preset must remain.',
  },
  'settings.prompt_presets.empty_name': {
    de: 'Bitte einen Namen für die Vorlage eingeben.',
    en: 'Please enter a preset name.',
  },
  'settings.prompt_presets.copy_suffix': {
    de: 'Kopie',
    en: 'Copy',
  },
  'settings.prompt.placeholder': {
    de: 'Geben Sie Ihren Prompt ein...',
    en: 'Enter your prompt...',
  },
  'settings.prompt.hint': {
    de: 'Der Text, den Sie korrigieren möchten, wird automatisch an den Prompt angehängt.',
    en: 'The text you want to correct will be automatically appended to the prompt.',
  },
  'settings.system_prompt': {
    de: 'System-Prompt',
    en: 'System Prompt',
  },
  'settings.system_prompt.hint': {
    de: 'Wird automatisch unter den Prompt angehängt und gilt für Editor und DOCX.',
    en: 'Appended to the prompt and used for both editor and DOCX.',
  },
  'settings.system_prompt.placeholder': {
    de: 'Geben Sie einen System-Prompt ein...',
    en: 'Enter a system prompt...',
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
    de: 'Klicken Sie auf "Korrigieren" um die DOCX/ODT-Datei zu korrigieren',
    en: 'Click "Correct" to correct the DOCX/ODT file',
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
    de: 'DOCX/ODT-Datei hier ablegen',
    en: 'Drop DOCX/ODT file here',
  },
  'editor.dropDocxHint': {
    de: 'Ziehen Sie eine .docx- oder .odt-Datei in diesen Bereich',
    en: 'Drag a .docx or .odt file into this area',
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
  'docx.logs.compare_fallback_manual_hint': {
    de: 'Korrigierte Datei ohne Track Changes bereitgestellt. Sie können den Vergleich manuell in Ihrer Office-Anwendung durchführen.',
    en: 'Returned corrected file without track changes. You can run the comparison manually in your office application.',
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
    de: 'Vorhandene Änderungen gefunden',
    en: 'Existing changes detected',
  },
  'docx.diff_mode.accept_existing.message': {
    de: 'Im Diff-Modus kann die Korrektur nur fortgesetzt werden, wenn Lingofix vorab alle vorhandenen Track Changes automatisch akzeptiert.\n\nMöchten Sie fortfahren?',
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
    de: 'Dokumente',
    en: 'Documents',
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
  'settings.docx.compare_mode.openxml': {
    de: 'OpenXML (integriert)',
    en: 'OpenXML (built-in)',
  },
  'settings.docx.compare_mode.word_native': {
    de: 'Word (nativ, MS Word erforderlich)',
    en: 'Word (native, requires MS Word)',
  },
  'settings.docx.compare_mode.libreoffice_uno': {
    de: 'LibreOffice UNO (nativ, soffice)',
    en: 'LibreOffice UNO (native, soffice)',
  },
  'settings.docx.openxml.warning': {
    de: 'OpenXML bietet keine odt.-Kompatibilität. Auch bei docx.-Dateien kann es zu Veränderungen im Layout kommen. Word wird für docx.-Dateien, LibreOffice für odt.-Dateien empfohlen.',
    en: 'OpenXML does not offer odt compatibility. Even with docx files, there may be changes in layout. Word is recommended for docx files, LibreOffice for odt files.',
  },
  'settings.docx.word_check.hint': {
    de: 'Prüfen Sie den Word-Zugriff einmalig, damit macOS die Berechtigungen für diesen Modus setzen kann.',
    en: 'Run this one-time Word access check so macOS can grant permissions for this mode.',
  },
  'settings.docx.word_check.hint_non_macos': {
    de: 'Prüfen Sie die Word-Verfügbarkeit für diesen Modus.',
    en: 'Check Word availability for this mode.',
  },
  'settings.docx.word_check.button': {
    de: 'Word-Zugriff prüfen',
    en: 'Check Word access',
  },
  'settings.docx.libreoffice_check.hint': {
    de: 'Prüfen Sie, ob LibreOffice (soffice) für diesen Modus verfügbar ist.',
    en: 'Check whether LibreOffice (soffice) is available for this mode.',
  },
  'settings.docx.libreoffice_check.button': {
    de: 'LibreOffice-Zugriff prüfen',
    en: 'Check LibreOffice access',
  },
  'settings.docx.word_check.failed': {
    de: 'Word-Zugriff konnte nicht geprüft werden.',
    en: 'Could not verify Word access.',
  },
  'settings.docx.compare_check.failed': {
    de: 'Vergleichsmodus konnte nicht geprüft werden.',
    en: 'Could not verify compare mode access.',
  },
  'settings.temperature': {
    de: 'Temperature',
    en: 'Temperature',
  },
  'settings.docx.batching': {
    de: 'Batching (experimentell)',
    en: 'Batching (experimental)',
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
  'settings.auto_check_updates': {
    de: 'Update-Prüfung',
    en: 'Update checks',
  },
  'settings.auto_check_updates.hint': {
    de: 'Beim Start und danach einmal täglich nach neuen Versionen suchen.',
    en: 'Check for new versions at startup and then once per day.',
  },
  'settings.auto_check_updates.toggle': {
    de: 'Automatisch nach Updates suchen',
    en: 'Automatically check for updates',
  },
  'settings.check_updates': {
    de: 'Auf Updates prüfen',
    en: 'Check for updates',
  },
  'settings.system_paths': {
    de: 'Systempfade',
    en: 'System paths',
  },
  'settings.app_reset': {
    de: 'App zurücksetzen',
    en: 'Reset app',
  },
  'settings.app_reset.hint': {
    de: 'Erstellt die settings.json neu mit Standardwerten. Verwenden Sie dies bei beschädigter Konfiguration.',
    en: 'Recreates settings.json with default values. Use this if your configuration is broken.',
  },
  'settings.app_reset.button': {
    de: 'App zurücksetzen',
    en: 'Reset app',
  },
  'settings.app_reset.success': {
    de: 'Die App wurde zurückgesetzt. Die Standard-Einstellungen sind wiederhergestellt.',
    en: 'App reset complete. Default settings were restored.',
  },
  'settings.app_reset.failed': {
    de: 'App konnte nicht zurückgesetzt werden',
    en: 'Could not reset app',
  },
  'settings.system_paths.hint': {
    de: 'Temp-Ordner und settings.json direkt im Dateiexplorer öffnen.',
    en: 'Open the temp folder and settings.json directly in your file explorer.',
  },
  'settings.system_paths.temp_folder': {
    de: 'Temp-Ordner öffnen',
    en: 'Open temp folder',
  },
  'settings.system_paths.settings_json': {
    de: 'settings.json öffnen',
    en: 'Open settings.json',
  },
  'settings.system_paths.open_failed': {
    de: 'Konnte Pfad nicht öffnen',
    en: 'Could not open path',
  },
  'update.available': {
    de: 'Neue Version verfügbar',
    en: 'New version available',
  },
  'update.available.message': {
    de: 'Version {version} ist verfügbar. Laden Sie das Update auf GitHub herunter.',
    en: 'Version {version} is available. Download the update on GitHub.',
  },
  'update.download': {
    de: 'Update herunterladen',
    en: 'Download update',
  },
  'update.none': {
    de: 'Keine neue Version verfügbar.',
    en: 'No new version available.',
  },
  'update.check_failed': {
    de: 'Update-Prüfung derzeit nicht möglich.',
    en: 'Update check is currently unavailable.',
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
