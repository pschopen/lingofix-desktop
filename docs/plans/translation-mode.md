# Plan: Übersetzungsmodus für Lingofix

Ziel: Lingofix bekommt neben der Korrektur einen zweiten Betriebsmodus **Übersetzen**,
wählbar per Dropdown im Header, mit Zielsprachen-Auswahl. Die bestehende
DOCX-Pipeline wird wiederverwendet; Ausgabe ist eine `_translated_<lang>`-Datei
**ohne** Track-Changes-Vergleich. Die Einstellungen werden auf ein
Seitenleisten-Layout umgebaut mit klarer Trennung Allgemein / Korrektur / Übersetzung.

## Harte Vorgaben (nicht verhandelbar)

1. **Das LLM führt niemals Marker mit.** Es bekommt reinen Text und liefert reinen
   Text. Die Zuordnung der Übersetzung auf Formatierungs-Runs geschieht
   ausschließlich deterministisch im Backend. Das bestehende Batch-Protokoll ist
   damit vereinbar: Es trennt Absätze nur durch Leerzeilen (`\n\n`,
   `ParagraphProcessor.BuildBatchRequest`/`TryParseBatchResponse`) — keine Marker,
   bleibt unverändert.
2. **Inline-Formatierung darf in der Übersetzung verloren gehen.** Fett/kursiv
   mitten im Absatz wird nicht positionsgenau übertragen; stattdessen wird pro
   Segment die dominante Formatierung auf den gesamten Segmenttext angewandt
   (Phase 2). Absatz-Formatierung (Überschriften-Styles, Einzüge etc.) bleibt
   vollständig erhalten, da nur `w:t`-Inhalte geschrieben werden.
3. **Absätze bleiben ganz.** Ein Absatz geht immer als Ganzes in eine
   LLM-Anfrage; nur wenn er das `chunk_size`-Limit aus den Einstellungen
   überschreitet, wird er (wie heute, an Satzgrenzen) geteilt. Das ist im
   Ist-Zustand bereits so (`CorrectWithChunkingAsync`) und ist als Invariante zu
   erhalten — keine neue Split-Logik einführen.
4. **Kontext bei Übersetzung:** Jede Übersetzungsanfrage bekommt den
   **vorherigen Absatz im Quelltext** als Kontext mitgegeben (Details Phase 3).
   Kontext ist immer der *Original*-Vorabsatz, nie der übersetzte — sonst
   entsteht eine sequentielle Abhängigkeit, die die Parallelisierung zerstört.
5. **Getrennte Prompts für Haupttext und Fußnoten** im Übersetzungsmodus.
   Fußnoten-Prompt gilt für Fußnoten und Endnoten; der Haupttext-Prompt für
   alles andere (Main, Kopf-/Fußzeilen, Glossar).
6. Migrationssicher: Bestehende `settings.json` ohne neue Felder müssen ohne
   Fehler laden und im Modus „Korrektur" starten (Vorbild: `speed_mode`-Migration).

## Toolchain / Kommandos

- .NET liegt unter `~/.dotnet` (nicht auf PATH):
  - Backend-Tests: `~/.dotnet/dotnet test backend.Tests/Lingofix.Backend.Tests.csproj`
  - Build: `~/.dotnet/dotnet build backend/Lingofix.Backend.csproj`
- Frontend: `cd frontend && npm test` (vitest), `npm run build` (tsc + vite).
- Rust/Tauri: `cd tauri && cargo check` genügt als Kompiliertest.
- Commit-Konvention im Repo: eine Phase = ein Commit, Präfix `Phase N: …`
  (siehe `git log`). Nach jeder Phase alle drei Testläufe grün.

## Architektur-Überblick (Ist-Zustand, relevant für alle Phasen)

- Settings-Fluss: Frontend (`frontend/src/types.ts` → `Settings`) → Tauri/Rust
  (`tauri/src/main.rs`, `FrontendSettings`: Persistenz, Defaults, Validierung,
  Preset-Sync) → als JSON-Datei an den .NET-Backend-Prozess
  (`--settings-path`, geparst in `backend/Documents/Settings.cs::FromFrontendJson`).
- DOCX-Lauf: `backend/Documents/LingofixRunner.cs` orchestriert; erzeugt
  `_corrected` (reine LLM-Ausgabe) und `_lingofix` (Track-Changes via
  OpenXML/Word/LibreOffice-Compare). `ParagraphProcessor` iteriert Absätze
  je Dokumentteil (`ProcessorWorkItemKind`: Main, Footnotes, Endnotes, Headers,
  Footers, Glossary), `ParagraphTextMapper.ApplyCorrection` schreibt die
  LLM-Antwort per Char-Span-/Token-Diff zurück in die Runs.
- Batching: Absätze desselben Dokumentteils werden bis `batch_max_chars`/
  `batch_max_paragraphs` zu einer Anfrage gebündelt, getrennt durch Leerzeilen;
  die Antwort wird per Leerzeilen-Split zurückgeordnet; bei Zählfehlern gibt es
  einen Einzelanfragen-Fallback.
- Prompt-Presets existieren bereits pro Korrektursprache
  (`custom_prompt_presets`, `active_custom_prompt_preset_ids` je Locale,
  Sync in `main.rs::sync_custom_prompt_with_active_preset`). Der Backend-Prozess
  bekommt immer nur die *aufgelösten* Prompts.

---

## Phase 1 — Backend: Modus-Plumbing und Pipeline-Weichen

### 1a. `backend/Documents/Settings.cs`

- Neues Enum `OperationMode { Correction, Translation }`.
- Neue Properties: `OperationMode Mode` (Default `Correction`),
  `string TargetLanguage` (ISO-Kürzel), `string FootnotePrompt` (nur im
  Translation-Modus befüllt; Fallback: leer ⇒ Haupt-Prompt gilt auch für
  Fußnoten).
- `FrontendSettingsPayload`: neue Felder `[JsonPropertyName("mode")]` und
  `[JsonPropertyName("translation")]` (mit `target_language`,
  `footnote_prompt`). Parsing analog `ParseSpeedMode`: unbekannt/fehlend →
  `Correction`. Im Translation-Modus ist `target_language` Pflicht
  (sonst `InvalidSettings`), im Correction-Modus werden die Felder ignoriert.
- Der Haupt-Übersetzungsprompt kommt weiterhin als `custom_prompt` an
  (Auflösung in Rust, Phase 4) — das Backend braucht keine Preset-Kenntnis.

### 1b. `backend/Documents/LingofixRunner.cs`

Alle Weichen an einer Stelle bündeln (lokale `isTranslation`-Variable direkt
nach dem Settings-Laden):

- **Ausgabepfade** (Zeilen ~67–68): im Translation-Modus
  `PathUtils.BuildOutputPath(input, $"_translated_{slug}")` als *einziges*
  Endprodukt. Kein `_lingofix`-Pfad, keine `_corrected`-Kopie.
  `slug` = dateisicherer Bezeichner aus der Zielsprache (lowercase, ASCII,
  Nicht-Alphanumerisches → `-`, max. ~24 Zeichen) — nötig, weil die Zielsprache
  auch Freitext sein kann („Schweizer Hochdeutsch" → `schweizer-hochdeutsch`).
- **Compare komplett überspringen**: Der gesamte Track-Changes-Zweig
  (`TrackChangesGenerator.GenerateWithWord` / `GenerateWithLibreOffice` /
  `GenerateParagraphCompare`, Zeilen ~330–405) entfällt; der „corrected"-
  Arbeitsstand wird direkt zum Zielpfad promoted (der Fallback-Pfad „corrected
  ohne Track-Changes" existiert schon und zeigt das Muster).
  `CompareMode` im Translation-Modus gar nicht erst auswerten — aber die
  Vorprüfung auf vorhandene Track-Changes bleibt aktiv (vorhandene Änderungen
  müssen weiterhin akzeptiert werden, sonst übersetzt man Lösch-Reste mit).
- **Zitat-Normalisierung aus**: bei `ResolveStyle`-Aufruf (~Zeile 173) im
  Translation-Modus `null` durchreichen (deutsche Zitierstil-Heuristik ist im
  Zieltext falsch).
- **`NonBreakingSpaceRestorer.Restore` überspringen** (~Zeile 420): stellt NBSPs
  anhand des deutschen Originals wieder her — im Zieltext kontraproduktiv
  (z. B. andere NBSP-Regeln im Französischen). `TrailingWhitespaceRejector`
  bleibt aktiv (sprachneutral).

### 1c. Checkpoint-Fingerprint (`ProcessingCheckpointStore.cs`)

Heute wird ein Checkpoint nur über den Eingabepfad identifiziert. Gefahr:
abgebrochener Korrekturlauf + Neustart im Übersetzungsmodus ⇒ halb korrigiertes,
halb übersetztes Dokument.

- `ProcessingCheckpoint` bekommt `string? Fingerprint`.
- Fingerprint = SHA-256 über `mode|target_language|prompt|footnote_prompt|model`
  (Werte mit `\n` getrennt).
- `Load`: Fingerprint-Mismatch **oder** altes Checkpoint-Format ohne Fingerprint
  → `null` zurückgeben (frisch starten) und Log-Zeile schreiben.
- `Save`: Fingerprint immer mitschreiben. Aufrufer in `LingofixRunner` anpassen.

### 1d. Tests (backend.Tests)

- `SettingsModeTests`: mode fehlt → Correction; `"translation"` + Zielsprache →
  Translation; Translation ohne Zielsprache → Fehler; `footnote_prompt` wird
  durchgereicht.
- `ProcessingCheckpointStoreTests`: Fingerprint-Match resumed, Mismatch und
  Alt-Format starten frisch.

---

## Phase 2 — Backend: Marker-freies Zurückschreiben der Übersetzung

Neuer Anwendungsmodus in `backend/Documents/ParagraphTextMapper.cs`
(neue Methode `ApplyTranslation(Paragraph, original, translated)`;
`ParagraphProcessor` ruft je nach `settings.Mode` die passende Methode).
Das bestehende Char-Span-/Token-Diff-Mapping ist für Übersetzungen ungeeignet
(nahezu nur Replace-Ops, Formatierung wird quasi zufällig verteilt).

### Algorithmus (deterministisch, ohne LLM-Beteiligung)

1. **Editable Runs bauen** wie bisher (`BuildEditableRuns`); Nicht-Text-Anker
   (Fußnoten-/Endnotenreferenzen, Felder, Grafiken, Breaks) bleiben unangetastet
   an ihrer Position.
2. **Segmentierung**: Absatzkinder in Dokumentreihenfolge durchlaufen;
   aufeinanderfolgende editierbare Text-Runs bilden ein Segment. Jedes
   „signifikante" Nicht-Text-Element dazwischen (Fußnotenreferenz, Feld,
   Drawing, Break) schließt das Segment. Ein Absatz ohne Anker = genau ein
   Segment (der häufigste Fall).
3. **Textverteilung auf Segmente** (nur bei ≥ 2 Segmenten): Übersetzten Text
   proportional zum Zeichenanteil der Original-Segmente aufteilen, Schnittpunkte
   auf die nächste Wortgrenze snappen. Segmente mit Original-Anteil 0 (z. B.
   leerer Rest nach schließender Fußnote) bekommen leeren Text. Damit bleiben
   Fußnotenanker *ungefähr* an ihrer relativen Position — exakt geht ohne Marker
   prinzipbedingt nicht, und das ist akzeptiert.
4. **Innerhalb eines Segments**: kompletten Segmenttext in den **ersten**
   Text-Node schreiben, alle weiteren Text-Nodes des Segments leeren.
   Formatierung: die `w:rPr` des **dominanten Runs** (meiste Originalzeichen im
   Segment) auf den Run des ersten Text-Nodes klonen. Damit gilt: ein Absatz,
   der überwiegend kursiv war, bleibt kursiv; ein einzelnes fettes Wort in
   normalem Text verliert sein Fett — gewollter Trade-off.
   `Space`-Attribut wie bisher über `NeedsPreserveSpace` setzen.
5. **Guards anpassen**: `IsLengthChangeSafe` bleibt für lange Absätze
   (Faktoren 4.0 / 0.2 decken reale Sprachpaare locker ab). Für kurze Originale
   (< 40 Zeichen, Überschriften wie „Inhaltsübersicht" → „Contents") den
   Ratio-Guard durch eine absolute Obergrenze ersetzen (übersetzter Text
   ≤ 400 Zeichen), sonst bleiben Überschriften still unübersetzt.
   `HasUnsafeStructure`, `XmlTextSanitizer`, Extraction-Gap-Tripwire
   (`CountVisibleTextChars`) unverändert übernehmen.

### Tests (`ParagraphTextMapperTests` erweitern)

Mindestfälle:
- Ein Run, reiner Text → vollständig ersetzt.
- Mehrere Runs, ein fettes Wort in der Mitte → gesamter Text im ersten Node,
  dominante (nicht-fette) Formatierung, übrige Nodes leer.
- Überwiegend kursiver Absatz → Ergebnis kursiv.
- Fußnotenreferenz mitten im Absatz → zwei Segmente, proportionale Aufteilung,
  Anker unverändert zwischen den Segmenten.
- Fußnotenreferenz am Absatzende → gesamter Text vor dem Anker.
- Kurze Überschrift mit Expansion > 4× → wird trotzdem angewendet.
- Leere/whitespace LLM-Antwort → Absatz unverändert (wie bisher).

---

## Phase 3 — Backend: Kontext-Absatz und Prompt-Routing

Betrifft `ParagraphProcessor.cs` und `LlmClient.cs`. Ziel: im Translation-Modus
bekommt jede Anfrage (a) den passenden Prompt je Dokumentteil und (b) den
vorherigen Quell-Absatz als Kontext.

### 3a. `LlmClient` refactoring

- `CorrectAsync`/`CorrectBatchAsync` nehmen künftig `prompt` und optional
  `context` als Parameter, statt den Prompt nur im Konstruktor zu halten
  (Konstruktor-Prompt bleibt als Default für den Korrekturpfad, damit der
  bestehende Aufrufercode minimal ändert).
- `BuildSimplePrompt` bekommt einen optionalen Kontextblock. Aufbau der
  User-Message im Translation-Modus (nur Eingabeseite strukturiert — die
  Antwort bleibt reiner Text, keine Marker):

  ```
  {prompt}

  Kontext (NUR zum Verständnis — NICHT übersetzen, NICHT in die Antwort aufnehmen):
  {vorheriger Quell-Absatz}

  Zu übersetzender Text:
  {absatz}
  ```

  Die Beschriftungen der beiden Blöcke kommen aus dem Default-Prompt-Fundus in
  Rust (mitgeliefert als Teil des aufgelösten Prompts oder als feste Backend-
  Strings — Entscheidung des Agenten, aber konsistent für Einzel- und
  Batch-Pfad).

### 3b. Kontext-Ermittlung in `ParagraphProcessor`

- **Einzelanfrage**: Kontext = Originaltext des vorherigen Work-Items desselben
  Dokumentteils (erster Absatz eines Teils: kein Kontextblock).
- **Batch**: Der Batch enthält die Nachbar-Absätze bereits als impliziten
  Kontext. Zusätzlich bekommt der Batch den Original-Vorabsatz des *ersten*
  Batch-Items als expliziten Kontextblock in den Prompt-Teil (niemals in die
  Leerzeilen-Payload — das würde die Zählung verschieben).
- **Chunking** (Absatz > `chunk_size`): Chunk N erhält Chunk N−1 (Quelltext)
  als Kontext; der erste Chunk den vorherigen Absatz. Die Absatz-Invariante
  (Vorgabe 3) bleibt unberührt: Chunking greift ausschließlich oberhalb des
  Limits.
- **Fußnoten**: Kontext = vorherige Fußnote. Das ist fachlich gewollt —
  Folgezitate („Ebd.", „a.a.O.") beziehen sich auf die vorhergehende Fußnote.
- **Cache** (`ParagraphProcessor`, Key ist heute der Originaltext): im
  Translation-Modus Composite-Key `context + "\0" + original`, sonst liefert
  der Cache bei gleichem Text mit anderem Kontext falsche Treffer.
  (Treffer werden dadurch selten — akzeptiert, Korrektheit vor Cache-Quote.)

### 3c. Prompt-Routing je Dokumentteil

- Work-Items kennen ihren Teil (`ProcessorWorkItemKind` / Label). Beim Bau der
  Anfrage: `Footnotes`/`Endnotes` → `settings.FootnotePrompt` (Fallback
  Haupt-Prompt, wenn leer), alle anderen Teile → Haupt-Prompt
  (`settings.Prompt`).
- Batches sind bereits pro Dokumentteil homogen — pro Batch genügt ein Prompt.

### 3d. Tests

- `LlmClient`-Prompt-Assembly: mit/ohne Kontextblock, Batch-Variante.
- `ParagraphProcessor`: erster Absatz ohne Kontext, Folge-Absätze mit korrektem
  Vorabsatz; Fußnoten-Items bekommen Fußnoten-Prompt; Cache-Key enthält Kontext
  im Translation-Modus (Test über ein Fake-`LlmClient`-Interface bzw. das
  vorhandene Testmuster in `ParagraphProcessorTests`).

---

## Phase 4 — Rust/Tauri: Settings, Defaults, Prompt-Auflösung

In `tauri/src/main.rs`:

- `FrontendSettings`: neue Felder mit `#[serde(default)]`:
  - `mode: String` (Default `"correction"`),
  - `translation: TranslationSettings` mit `target_language: String`
    (Default aus UI-Locale sinnvoll wählen, z. B. `"en"`),
    `prompt_presets: Vec<TranslationPromptPreset>` und
    `active_preset_ids: HashMap<String, String>` — **eigener Namespace**, die
    bestehenden Korrektur-Presets bleiben unberührt.
- `TranslationPromptPreset` = `{ id, name, locale (Zielsprache), main_prompt,
  footnote_prompt }` — **ein** Preset trägt beide Prompts, damit Haupttext- und
  Fußnoten-Prompt immer als konsistentes Paar gewechselt werden.
- **Zielsprache = bekannter Code ODER Freitext.** Bekannte Sprachen laufen über
  `KNOWN_LANGUAGES`/`normalize_language`; zusätzlich ist ein freier
  Sprachname erlaubt („Latein", „Schweizer Hochdeutsch", „Norwegisch (Bokmål)")
  — LLMs kommen mit freien Sprachnamen besser zurecht als eine starre
  Code-Liste. Konsequenzen: (a) `active_preset_ids` wird über einen
  normalisierten Slug der Zielsprache geschlüsselt (gleiche Slug-Regel wie beim
  Dateinamen, Phase 1b), nicht über den Locale-Enum; (b) in die Default-Prompts
  wird der Sprach-*Anzeigename* eingesetzt (bei bekannten Codes der übersetzte
  Sprachname, bei Freitext die Nutzereingabe unverändert).
- **Default-Prompts** `default_translation_prompts(target: &str)` analog
  `default_custom_prompt`, liefert beide Texte. Vorschlag (Deutsch, mit
  eingesetztem Zielsprachennamen):
  - Haupttext: *„Übersetze den folgenden Text vollständig ins {Zielsprache}.
    Gib ausschließlich die Übersetzung aus — keine Kommentare, keine
    Erklärungen, keine Anführungszeichen um die Ausgabe. Übernimm die Absatz-
    und Satzstruktur so weit wie möglich. Verwende die Typografie-Konventionen
    der Zielsprache (Anführungszeichen, Gedankenstriche). Eigennamen, Zitate in
    Originalsprache und Aktenzeichen bleiben unübersetzt."*
  - Fußnoten: zusätzlich *„Der Text ist eine Fußnote. Zitate und
    Literaturangaben (Autor, Titel, Zeitschrift, Verlag, Auflage, Seitenzahlen)
    bleiben unverändert in der Originalsprache; übersetze nur erläuternden
    Fließtext. Folgezitat-Konventionen (z. B. „Ebd.") in die übliche Form der
    Zielsprache übertragen."*
- **Batch-Prompt**: Übersetzungs-Variante des `batch_prompt` anlegen, mit
  derselben Kerninstruktion wie heute bei der Korrektur: gleiche Anzahl
  Absätze zurückgeben, exakt durch Leerzeilen getrennt, keine zusätzlichen
  Leerzeilen innerhalb eines Absatzes. (Das ist das bestehende
  Leerzeilen-Protokoll, keine neuen Marker.)
- **Prompt-Auflösung**: `sync_custom_prompt_with_active_preset` um den
  Translation-Fall erweitern — bei `mode == "translation"` gehen als
  `custom_prompt`/`batch_prompt` die Werte des aktiven Übersetzungs-Presets der
  Zielsprache an das Backend, plus `translation.footnote_prompt`, `mode` und
  `translation.target_language` im Settings-JSON (Phase 1a/3 erwarten sie).
- **Validierung** analog zu den bestehenden Preset-Checks (IDs eindeutig,
  Werte nicht leer, Locale bekannt) — aber tolerant migrieren: fehlender
  `translation`-Block wird mit Defaults befüllt, niemals `reset_hint`-Fehler
  für Alt-Installationen.
- `cargo check` + gezielter Blick auf die Settings-Roundtrip-Stellen
  (laden → validieren → speichern), damit die neuen Felder nicht beim
  Speichern verworfen werden.

---

## Phase 5 — Frontend: Dropdown, Zielsprache, Settings-Seitenleiste, Ansichten

### 5a. Typen und Settings (`frontend/src/types.ts`)

- `export const OPERATION_MODES = ['correction', 'translation'] as const;`
  `mode: OperationMode` in `Settings`, plus `translation`-Block
  (`target_language`, Presets mit `main_prompt` + `footnote_prompt`).
- **Zielsprachen-Auswahl**: kuratierte Liste der gängigen Sprachen (aus den
  vorhandenen 24 i18n-Sprachen ableiten, Anzeige über bestehende
  Sprachnamen-Übersetzungen) **plus Eintrag „Andere Sprache …"** mit freiem
  Texteingabefeld (siehe Phase 4: Freitext-Zielsprache ist voll unterstützt).

### 5b. Header-Dropdown (`frontend/src/App.tsx`, Header ~Zeile 882–942)

- Links neben Dark-Mode-Toggle: `<select>` Korrigieren/Übersetzen (Werte aus
  `OPERATION_MODES`) plus — nur im Übersetzungsmodus — ein zweites `<select>`
  für die Zielsprache.
- Änderungen sofort persistieren (gleicher `save_settings`-Weg wie das
  SettingsModal).
- Abhängige UI-Texte modusabhängig schalten: Haupt-Button
  (`button.correct` → neuer Key `button.translate`), Fortschritts- und
  Fehlertexte, Dropzone-Beschriftung.

### 5c. SettingsModal: Umbau auf Seitenleiste

`frontend/src/components/SettingsModal.tsx` (aktuell ~1800 Zeilen, eine lange
Liste) wird auf ein zweispaltiges Layout umgebaut: links eine Seitenleiste mit
Abschnitten, rechts der Inhalt des gewählten Abschnitts.

- **Seitenleisten-Abschnitte und Zuordnung:**
  - **Allgemein**: Provider, API-URL/-Key, Modell, Temperature, Reasoning,
    Geschwindigkeit (Auto/Manuell), Batching + Cache, zu verarbeitende
    Dokumentteile (bisher „correction_scope_parts" — Label generalisieren, gilt
    für beide Modi), UI-Sprache, Schriftgröße, Update-Check, Advanced/Reset.
  - **Korrektur**: Korrektursprache, Korrektur-Prompt-Presets (bestehende UI),
    Compare-Modus (Word/LibreOffice/OpenXML — nur für Korrektur relevant),
    Zitat-Normalisierung, geschützte Leerzeichen, Trailing-Whitespace-Option.
  - **Übersetzung**: Zielsprache (gleicher Wert wie im Header, beide Stellen
    schreiben dasselbe Settings-Feld), Übersetzungs-Presets pro Zielsprache mit
    **zwei Editorfeldern** (Haupttext-Prompt, Fußnoten-Prompt), Hinweistext,
    dass der vorherige Absatz automatisch als Kontext mitgesendet wird.
- Die bestehende Preset-UI (Auswahl/Neu/Duplizieren/Umbenennen/Löschen,
  `settings.prompt_presets.*`-Keys) in eine wiederverwendbare Komponente
  extrahieren und für Korrektur (1 Feld) und Übersetzung (2 Felder)
  parametrisieren, statt Code zu kopieren.
- Refactoring-Disziplin: reine Umgruppierung — keine Einstellung entfernen oder
  umbenennen (Persistenzformat bleibt identisch, nur die Darstellung ändert
  sich). Abschnitts-Komponenten in eigene Dateien ziehen
  (`frontend/src/components/settings/…`), sonst wächst die Datei ins Unwartbare.

### 5d. Texteditor-Modus

- `correct_text_streaming` funktioniert unverändert (Prompts kommen aufgelöst
  aus Rust; Kontext entfällt hier — einzelner Freitext hat keinen Vorabsatz).
- Im Übersetzungsmodus die Inline-Diff-Ansicht deaktivieren (bei
  Fremdsprachen-Ausgabe ist alles rot/grün): stattdessen Original links,
  Übersetzung rechts als Plain-Panels (eigene Komponente
  `TranslationResult.tsx`, auf schmalen Fenstern untereinander);
  „Übernehmen"-/„Verwerfen"-Flow beibehalten und um einen Button
  **„Übersetzung kopieren"** (Clipboard) ergänzen.
  Einstiegspunkt: `showDiff`/`TextEditor.tsx` — kleinste Lösung ist ein
  `mode`-Prop, das das Diff-Rendering durch die Zwei-Spalten-Ansicht ersetzt;
  die bestehende Diff-Logik bleibt unangetastet.

### 5e. i18n (`frontend/src/i18n.ts`)

- Neue Keys (mindestens): `mode.label`, `mode.correction`, `mode.translation`,
  `mode.target_language`, `button.translate`, `button.translating`,
  Sidebar-Abschnitte (`settings.section.general`, `settings.section.correction`,
  `settings.section.translation`), Labels für Haupttext-/Fußnoten-Prompt,
  Kontext-Hinweis, `docx.translated`-Erfolgstexte, Log-Hinweis
  „Felder/Inhaltsverzeichnis werden nicht übersetzt — in Word aktualisieren".
- Das Repo pflegt alle 24 Sprachen pro Key; `i18n.test.ts` prüft
  Vollständigkeit — alle Locales befüllen, Test laufen lassen.

---

## Phase 6 — Endabnahme

1. Alle drei Testsuiten (dotnet, vitest, cargo check) grün.
2. **E2E-Smoke** mit einer kleinen Test-DOCX (mit Fußnote mitten im Absatz,
   Folgezitat-Fußnote („Ebd."), fettem Einzelwort, Überschrift, Kopfzeile):
   - Übersetzungsmodus EN → `_translated_en.docx` entsteht, keine
     `_lingofix`-Datei, Fußnotenanker vorhanden, Überschrift übersetzt,
     Fußnoten nach Fußnoten-Prompt behandelt.
   - Danach Korrekturmodus auf derselben Datei → frischer Lauf
     (Checkpoint-Fingerprint), `_corrected`/`_lingofix` wie bisher.
   - Abbruch mitten im Übersetzungslauf + Neustart im Übersetzungsmodus →
     Resume greift.
3. Settings-Seitenleiste: alle bisherigen Einstellungen wiederfindbar, Werte
   überleben einen App-Neustart (Roundtrip-Kontrolle der neuen Felder).
4. README: Abschnitt zum Übersetzungsmodus ergänzen (Bedienung, Kontext-
   Verhalten, was mit Feldern/TOC passiert, Hinweis auf Verlust von
   Inline-Fett/Kursiv).

## Explizit außerhalb des Scopes (nicht bauen)

- Glossar-/Terminologie-Verwaltung (späterer Ausbau).
- Modell-Override pro Modus.
- Positionsgenaue Übertragung von Inline-Formatierung (per Vorgabe verworfen).
- Übersetzung von Feldergebnissen (TOC-Einträge etc.) — nur Log-Hinweis.
- Kontext aus dem *übersetzten* Vortext (würde Parallelisierung serialisieren).
- Track-Changes-Ausgabe für Übersetzungen, auch nicht als Opt-in — bei
  Volltext-Ersetzung unlesbar, Word-/LibreOffice-Compare kann bei komplett
  verschiedenem Text zudem sehr langsam werden oder scheitern.
- Umbenennung der `correct_text_streaming`-Commands/`correction_*`-Events in
  generische `text_operation_*`-Namen: unnötig, weil die Prompt-Auflösung in
  Rust modusabhängig ist — derselbe Command trägt beide Modi ohne Duplikation.
  (Rein kosmetisches Refactoring, separat vom Feature, falls je gewünscht.)
- Prompt-Baustein-Checkboxen („Ton erhalten", „Eigennamen unverändert" …):
  Konflikt mit frei editierbaren Presets — wer solche Regeln will, schreibt sie
  ins Preset. Keine zwei konkurrierenden Quellen für Prompt-Inhalte einführen.
- Separate Scope-Auswahl „zu übersetzende Dokumentteile" pro Modus: v1 nutzt
  die bestehende geteilte Dokumentteile-Auswahl (Label wird generalisiert,
  Phase 5c). Per-Modus-Scopes erst, wenn sich echter Bedarf zeigt.
