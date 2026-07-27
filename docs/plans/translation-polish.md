# Plan: Feinschliff Übersetzungsmodus (Prompts, Sprachen, UI)

Umsetzungsplan für die zehn Feedback-Punkte vom 26.07.2026. Baut auf dem
aktuellen (teilweise uncommitteten) Stand von `main` auf — Arbeitskopie nicht
zurücksetzen. Backend (C#) bleibt fast vollständig unberührt; die Arbeit liegt
in `tauri/src/main.rs`, `frontend/src/i18n.ts` und den Frontend-Komponenten.

## Übersicht / Reihenfolge

| AP | Punkt(e) aus dem Feedback | Aufwand |
|----|---------------------------|---------|
| 1  | Sprach-Namenstabelle (Grundlage für AP 2 + 5) | mittel |
| 2  | Prompts in UI-Sprache; „nach Deutsch“; System-Prompt-Aufteilung | groß |
| 3  | „Andere Sprache“ persistieren + rotes X im Dropdown | mittel |
| 4  | Zielsprachen in UI-Sprache anzeigen | klein (nutzt AP 1) |
| 5  | Enter im Preset-Dialog | klein |
| 6  | „Original“ eingrauen im Übersetzungsmodus | klein |
| 7  | Datei-auswählen-Button-Position | klein |
| 8  | Chunk-Größe-Labels unterscheiden | klein |
| 9  | Prompt-Textareas ohne Scrollbalken (auto-grow) | klein |

Empfohlene Reihenfolge: AP 1 → 2 → 4 → 3, danach 5–9 in beliebiger Reihenfolge
(unabhängig voneinander).

---

## AP 1: Lokalisierte Sprachnamen-Tabelle (Grundlage)

**Problem:** Es gibt nur `LANGUAGE_DISPLAY_NAMES_DE` (deutsch) in
`frontend/src/i18n.ts:120` und `language_display_name_de` in
`tauri/src/main.rs:1305`. Für AP 2 (Prompts in UI-Sprache) und AP 4
(Dropdown-Anzeige in UI-Sprache) brauchen beide Seiten Sprachnamen für alle
24 UI-Sprachen × 24 Zielsprachen.

**Lösung:** Eine Generator-Datei einchecken, die per `Intl.DisplayNames`
(CLDR-Daten, in Node vorhanden) beide Tabellen erzeugt, damit Rust und TS
garantiert identische Strings haben:

1. Neues Skript `scripts/generate-language-names.mjs`:
   - Für jede UI-Sprache aus `EU_LANGUAGE_CODES` und jede Zielsprache
     `new Intl.DisplayNames([ui], { type: 'language' }).of(target)` aufrufen.
   - Ausgabe 1: `frontend/src/languageNames.ts` mit
     `export const LANGUAGE_DISPLAY_NAMES: Record<Language, Record<Language, string>>`
     und Helper `languageDisplayName(uiLang: Language, target: string): string`
     (unbekannter/freier Zielsprachen-Text wird unverändert zurückgegeben —
     gleiche Semantik wie bisher `translationLanguageDisplayNameDe`).
   - Ausgabe 2: `tauri/src/language_names.rs` mit einer
     `pub fn language_display_name(ui_lang: &str, target_language: &str) -> String`
     (match/Tabelle; Fallback: getrimmter Eingabetext). In `main.rs` per
     `mod language_names;` einbinden.
   - Beide Dateien mit „GENERATED — edit scripts/generate-language-names.mjs“-Header.
   - Wichtig: Erster Buchstabe je Sprache ggf. großschreiben, wo die Sprache
     das für Prompts erwartet (Deutsch: `Intl` liefert „Deutsch“ korrekt;
     Sprachen mit kleingeschriebenen Sprachnamen wie fr „allemand“ so lassen —
     CLDR-Ausgabe unverändert übernehmen, keine Sonderbehandlung).
2. `LANGUAGE_DISPLAY_NAMES_DE` + `translationLanguageDisplayNameDe` (i18n.ts)
   und `language_display_name_de` + zugehörige Tabelle (main.rs, ~Zeile
   1273–1317) durch die neuen Funktionen ersetzen. Alle Aufrufer anpassen.
3. Test (vitest): `languageDisplayName('de', 'de') === 'Deutsch'`,
   `languageDisplayName('en', 'de') === 'German'`, Freitext bleibt erhalten.
   Rust-Test analog im bestehenden Testmodul von `main.rs`.

---

## AP 2: Übersetzungs-Prompts in UI-Sprache, „nach“ statt „ins“, System-Prompt-Aufteilung

Betrifft die vier Default-Texte, die es heute nur auf Deutsch gibt:

- `DEFAULT_TRANSLATION_MAIN_PROMPT_TEMPLATE` (main.rs:1042 und i18n.ts:156)
- `DEFAULT_TRANSLATION_FOOTNOTE_PROMPT_SUFFIX` (main.rs:1043, i18n.ts:162)
- `DEFAULT_TRANSLATION_BATCH_PROMPT` (main.rs:1044, i18n.ts:175)
- `DEFAULT_TRANSLATION_SYSTEM_PROMPT` (main.rs:1050)

### 2a. Neue inhaltliche Aufteilung (erst deutsch formulieren, dann übersetzen)

Alle **allgemeinen** Regeln wandern in den System-Prompt; die Custom-Prompts
(main/footnote) bleiben schlank, damit der User dort nur Stil/Register bzw.
Fußnoten-Umfang anpasst:

- **System-Prompt (neu, allgemein):**
  „Du bist ein professioneller Übersetzer. Gib ausschließlich die Übersetzung
  aus — keine Kommentare, keine Erklärungen, keine Anführungszeichen um die
  Ausgabe. Übernimm die Absatz- und Satzstruktur so weit wie möglich. Verwende
  die Typografie-Konventionen der Zielsprache (Anführungszeichen,
  Gedankenstriche). Eigennamen, Zitate in Originalsprache und Aktenzeichen
  bleiben unübersetzt.“
- **Haupttext-Prompt (neu, schlank):**
  „Übersetze den folgenden Text vollständig nach {lang}.“
  → **„nach {lang}“, nicht „ins {lang}“** (Feedback-Punkt 3).
- **Fußnoten-Prompt (neu):** Haupttext-Prompt + „Der Text ist eine Fußnote.
  Zitate und Literaturangaben (Autor, Titel, Zeitschrift, Verlag, Auflage,
  Seitenzahlen) bleiben unverändert in der Originalsprache; übersetze nur
  erläuternden Fließtext. Folgezitat-Konventionen (z. B. „Ebd.“) in die
  übliche Form der Zielsprache übertragen.“
- **Batch-Prompt:** Wortlaut wie bisher (Absatzanzahl/Leerzeilen), nur
  lokalisieren.

### 2b. Lokalisierung (24 Sprachen)

- In `i18n.ts`: die vier Texte als `Record<Language, string>`-Tabellen anlegen
  (Muster: `defaultCustomPrompts`/`defaultSystemPrompts`, i18n.ts:47–99).
  Signaturen erweitern: `defaultTranslationMainPrompt(uiLang, targetLanguage)`,
  `defaultTranslationFootnotePrompt(uiLang, targetLanguage)`,
  `defaultTranslationBatchPrompt(uiLang)`, neu
  `defaultTranslationSystemPrompt(uiLang)`. `{lang}` wird mit
  `languageDisplayName(uiLang, targetLanguage)` (AP 1) ersetzt.
- In `main.rs`: dieselben 4×24 Strings als `match`-Funktionen mit
  `ui_lang`-Parameter (Muster: `default_custom_prompt`/`default_system_prompt`,
  main.rs:970). **Rust- und TS-Strings müssen zeichenidentisch sein** (die
  Default-Presets werden serverseitig aufgefrischt und würden sonst die
  Client-Vorschau überschreiben).
- Übersetzungsqualität: fachlich korrekt in alle 24 EU-Sprachen übertragen
  (kein Platzhalter-Englisch). Die richtige Präposition je Sprache verwenden
  (en „into {lang}“, fr „vers le {lang}“/„en {lang}“ → natürliche Formulierung
  wählen).

### 2c. Verdrahtung in `main.rs`

- `default_translation_prompt_preset(target_language)` →
  `default_translation_prompt_preset(ui_lang, target_language)`.
- `sync_translation_prompt_with_active_preset` (main.rs:2461):
  - `ui_lang` = `normalize_language(&settings.ui_language)` verwenden.
  - Der Refresh des `default-{slug}`-Presets (main.rs:2471–2477) nutzt die
    neuen lokalisierten Defaults → damit „migrieren“ bestehende
    Default-Presets automatisch auf die neue Formulierung und wechseln beim
    UI-Sprachwechsel mit. Benutzerdefinierte Presets bleiben unangetastet.
  - `settings.batch_prompt` (main.rs:2533) bekommt den lokalisierten
    Batch-Prompt.
  - **Neu:** `translation.system_prompt` auffrischen, wenn er leer ist oder
    einem bekannten Default entspricht — analog zu
    `is_default_or_legacy_system_prompt` (main.rs:1013). Neue Funktion
    `is_default_or_legacy_translation_system_prompt(value)`: matcht den alten
    deutschen String (aktueller `DEFAULT_TRANSLATION_SYSTEM_PROMPT`) sowie
    alle 24 neuen Varianten. Nur dann durch die aktuelle UI-Sprach-Variante
    ersetzen; ein vom User editierter System-Prompt bleibt stehen.
- `default_translation_system_prompt()` (serde-Default, main.rs:1076): bleibt
  ohne Locale-Parameter möglich (Default „de“ oder „en“), weil der
  Sync-Schritt ihn ohnehin sofort auf die UI-Sprache umschreibt.

### 2d. Verdrahtung im Frontend

- `SettingsModal.tsx:578–582` (Client-seitiges Default-Preset): `lang` als
  Parameter durchreichen.
- Kommentarblock i18n.ts:109–118 aktualisieren (Spiegelungs-Hinweis).

### 2e. Tests

- vitest (`i18n.test.ts`): analog zu „localizes custom and system default
  prompts“ — für alle 24 Sprachen nicht-leer; `de` enthält „nach Deutsch“
  (Ziel de) und **nicht** „ins “; `en`-Variante enthält keinen deutschen Text.
- Rust-Tests (main.rs, Testmodul ab ~5316, plus `backend.Tests` nur falls
  betroffen — Backend liest die Prompts nur durch, keine Änderung nötig):
  - Default-Preset wird in UI-Sprache erzeugt (ui=en → englischer Prompt).
  - UI-Sprachwechsel de→en refresht das `default-{slug}`-Preset auf Englisch.
  - Alter deutscher `translation.system_prompt` wird ersetzt; ein
    benutzerdefinierter nicht.
- Prüfen (nicht ändern): `backend/Documents/ParagraphProcessor.cs:39` nutzt
  `FootnotePrompt` weiterhin korrekt; der System-Prompt läuft über
  `active_system_prompt` (main.rs:1020) — Verhalten unverändert.

---

## AP 3: „Andere Sprache“ persistieren + rotes X

**Ist:** Eine frei eingegebene Zielsprache (Settings → Übersetzung → „Andere
Sprache…“, `TranslationSection.tsx:82–90`) existiert nur solange sie die
aktive `translation.target_language` ist. Wechselt man auf eine EU-Sprache,
ist sie aus beiden Dropdowns (Toolbar `App.tsx:1271–1307`, Settings
`TranslationSection.tsx:62–80`) verschwunden.

**Soll:**

1. **Rust:** `TranslationSettings` (main.rs:~1560) um
   `#[serde(default)] custom_languages: Vec<String>` erweitern. In
   `sync_translation_prompt_with_active_preset`: wenn
   `target_language` nicht leer, kein EU-Code (Abgleich mit der bestehenden
   Sprachliste, siehe `KNOWN_LANGUAGES`/`normalize_language`) und noch nicht
   enthalten (case-insensitiver Vergleich auf getrimmtem Wert) → anhängen.
   Einträge beim Laden trimmen/deduplizieren. `validate_settings` braucht
   keine neue Pflicht-Regel (leere Liste ok).
2. **Entfernen-Command:** Entfernen läuft über normales `save_settings` mit
   herausgefilterter Liste (kein neuer Tauri-Command nötig). Achtung: der
   Sync fügt die Sprache wieder hinzu, wenn sie noch `target_language` ist —
   deshalb beim Entfernen der aktiven Sprache clientseitig zugleich
   `target_language` auf einen Fallback setzen (erste EU-Sprache bzw.
   `default_target_language()`; danach `load_settings`-Refresh wie in
   `handleTargetLanguageChange`, App.tsx:835).
3. **types.ts:** `translation.custom_languages: string[]` ergänzen.
4. **Toolbar-Dropdown (App.tsx:1271–1307):** Statt nur der aktiven
   Freitext-Sprache (Zeile 1279) alle `custom_languages` als Optionen über
   der EU-Liste rendern. Jede Custom-Option bekommt rechts ein kleines rotes
   X (lucide `X`, `size={12}`, `text-red-500 hover:text-red-600`), als eigener
   Button mit `stopPropagation`, der die Sprache entfernt (Schritt 2). EU-
   Sprachen bekommen **kein** X.
5. **Settings-Dropdown (`TranslationSection.tsx`):** `SelectField`
   (`shared.tsx:39`) um optionale Props `removableValues?: string[]` und
   `onRemoveOption?: (value: string) => void` erweitern; in der Options-Zeile
   (shared.tsx:232–247) bei removable Values das rote X rechtsbündig rendern
   (Options-Button auf `flex justify-between`). `TranslationSection` listet
   `custom_languages` als Optionen zwischen EU-Liste und „Andere Sprache…“;
   `isKnownLanguage`-Logik (Zeile 56) so erweitern, dass eine gespeicherte
   Custom-Sprache als reguläre Auswahl gilt (Freitext-Input nur noch für
   *neue* unbekannte Eingaben).
6. **i18n:** neuer Key `mode.target_language.remove` (aria-label des X,
   24 Sprachen, z. B. de „{lang} entfernen“).
7. **Tests:** Rust: Freitext-Sprache landet nach Sync in `custom_languages`
   und bleibt nach Wechsel auf `de` erhalten; Duplikate werden nicht doppelt
   angelegt. Die zugehörigen Presets (`default-{slug}`) beim Entfernen
   **behalten** (schadlos, und Wiederanlegen der Sprache findet den alten
   Prompt wieder).

---

## AP 4: Zielsprachen in UI-Sprache anzeigen

**Ist:** Toolbar-Dropdown und Button-Label zeigen native Namen
(`LANGUAGE_LABELS`, i18n.ts:10; verwendet in App.tsx:921–923, 1303 und
`TranslationSection.tsx:76`).

**Soll:** Überall dort, wo **Zielsprachen** angezeigt werden, stattdessen
`languageDisplayName(lang, code)` aus AP 1 verwenden (Freitext unverändert).
Liste alphabetisch nach lokalisiertem Namen sortieren
(`localeCompare(…, lang)`), damit z. B. „Tschechisch“ im Deutschen nicht an
Position „Čeština“ steht. **Nicht** anfassen: die UI-Sprachauswahl in
`GeneralSection`/Onboarding — dort bleiben native Namen (`LANGUAGE_LABELS`)
korrekt, weil man seine eigene Sprache im nativen Namen sucht.

---

## AP 5: Enter im Preset-Dialog

`PromptPresetEditor.tsx:169–175` (Name-Input für Neu/Duplizieren/Umbenennen):

- `onKeyDown`: `Enter` → `onDialogConfirm()` (nur wenn `dialogValue.trim()`
  nicht leer), `Escape` → `onDialogCancel()`.
- Gilt automatisch für Korrektur- und Übersetzungs-Presets (gemeinsame
  Komponente).

---

## AP 6: „Original“ im Übersetzungsmodus eingrauen

**Ist:** In der Side-by-Side-Ansicht (`TextEditor.tsx:355–434`) ist die linke
Original-Textarea während einer **laufenden** Übersetzung normal schwarz und
editierbar (`disabled={readOnly}`, und `readOnly` ist erst nach Abschluss
true, App.tsx:1569), während rechts der graue „Wird übersetzt…“-Status steht.
Nach Abschluss ist Original grau (disabled), die Übersetzung schwarz —
während des Laufs ist es genau umgekehrt und wirkt uneinheitlich.

**Soll:** Im Übersetzungs-Zweig die linke Textarea auch während des Laufs
eingrauen/sperren: `disabled={readOnly || isCorrecting}`
(TextEditor.tsx:396). Die vorhandenen `disabled:`-Klassen (Zeile 403–404)
liefern das Grau. Visuell im laufenden Betrieb verifizieren (siehe
Verifikation unten): Idle = schwarz/editierbar, während Übersetzung + nach
Abschluss = beide Panes einheitlich (Original grau).

*Hinweis für den Implementierer:* Das ist die Interpretation von „Auch
‚Original‘ muss eingegraut sein“. Sollte beim visuellen Check auffallen, dass
etwas anderes gemeint sein könnte (z. B. die Pane-Überschrift), kurz beim
User rückfragen statt raten.

---

## AP 7: Datei-auswählen-Button auf gleicher Höhe wie im Korrektor

**Ist:** Im Korrektor sitzt die „Klicken, um eine Datei auszuwählen“-Pille
direkt in einem `bottom-8`-Container (TextEditor.tsx:557–577). Im
Übersetzungsmodus ist dieselbe Pille zusätzlich in eine Panel-Box mit
`p-2.5 rounded-xl` gewickelt (TextEditor.tsx:368–384, Hintergrund über der
Mittellinie der zwei Spalten) — dadurch sitzt die Pille 10px höher.

**Soll:** Wrapper-Box behalten (sie kaschiert die Spaltentrennlinie), aber
den Außenabstand kompensieren: äußeres `bottom-8` → `bottom-[22px]`
(32px − 10px Padding), sodass die **Pille selbst** in beiden Modi auf
identischer Höhe liegt. Per Screenshot in beiden Modi nachmessen/prüfen.

---

## AP 8: Chunk-Größe zweimal in den Einstellungen — Labels unterscheiden

**Befund:** Es sind tatsächlich zwei getrennte Einstellungen mit demselben
Default 7500 und identischem Label „Chunk-Größe“:
`docx.chunk_size` (Word-Dateien, `AdvancedSection.tsx:146`) und
`editor.chunk_size` (eingefügter Text im Editor, `AdvancedSection.tsx:158`).
Die Vermutung des Users stimmt also — es fehlt nur die Beschriftung.

**Soll:** Die i18n-Labels differenzieren (alle 24 Sprachen), z. B.
- `settings.docx.chunk_size` → de „Chunk-Größe (Word-Dateien)“,
  en „Chunk size (Word files)“, …
- `settings.editor.chunk_size` → de „Chunk-Größe (Text-Editor)“,
  en „Chunk size (text editor)“, …

Optional je ein `hint` über `FieldGroup` (shared.tsx:11), der in einem Satz
sagt, worauf sich die Größe auswirkt. Keine Funktionsänderung.

---

## AP 9: Prompt-Textareas ohne Scrollbalken (auto-grow)

**Ist:** Die Prompt-Felder im `PromptPresetEditor` haben fix `h-28`
(PromptPresetEditor.tsx:132) → lange Übersetzungs-Prompts scrollen.

**Soll:** Textareas wachsen mit dem Inhalt:

- Kleine Komponente `AutoGrowTextarea` (in `shared.tsx`): `ref` +
  `useLayoutEffect` auf `value`: `style.height = 'auto'` →
  `style.height = `${scrollHeight}px``; Klassen `overflow-hidden
  resize-none min-h-28` (Mindesthöhe wie bisher).
- **Kein** CSS `field-sizing: content` — Tauri auf macOS rendert mit
  WKWebView, das das nicht unterstützt.
- Im `PromptPresetEditor` (beide Felder → gilt für Korrektur- und
  Übersetzungs-Prompts einheitlich) und sinnvollerweise auch für die beiden
  System-Prompt-Textareas in `AdvancedSection.tsx:78/92` verwenden.
- Der Modal-Body scrollt bereits (menuBoundaryRef-Container) — lange Prompts
  verlängern also einfach die Settings-Seite, kein eigener Scrollbalken.

---

## Verifikation (am Ende, gesamthaft)

1. `cd frontend && npx vitest run` (i18n-Tests inkl. neuer Prompt-/Namens-Tests).
2. `cd tauri && cargo test` (Settings-Sync-Tests).
3. Backend unverändert; nur falls doch etwas in `backend/` angefasst wurde:
   dotnet liegt unter `~/.dotnet` (nicht im PATH) — `~/.dotnet/dotnet test backend.Tests`.
4. Visueller Smoke-Test über die Dev-Preview (launch.json/`preview_start`,
   nicht per Bash starten):
   - Übersetzungsmodus, UI-Sprache Deutsch: Default-Prompt enthält
     „… nach Deutsch“; UI auf Englisch umstellen → Default-Preset englisch.
   - Freitext-Sprache anlegen → taucht nach Sprachwechsel weiter im Dropdown
     auf, rotes X entfernt sie (nur bei Custom-Sprachen sichtbar).
   - Dropdown-Namen in UI-Sprache, alphabetisch.
   - Enter speichert neuen Preset-Namen; Escape bricht ab.
   - Während laufender Übersetzung: Original-Pane grau.
   - Datei-Button-Höhe in beiden Modi vergleichen (Screenshots).
   - Settings: zwei unterscheidbare Chunk-Größen-Labels; Prompt-Felder ohne
     Scrollbalken, volle Höhe.

## Explizit außer Scope

- Keine Änderungen an Korrektur-Prompts/-Presets über das Nötigste hinaus.
- Keine Backend-Prompt-Logik (`ParagraphProcessor`, `LlmClient`) — die liest
  nur, was Rust in die Settings schreibt.
- UI-Sprachauswahl (GeneralSection/Onboarding) bleibt bei nativen Namen.
