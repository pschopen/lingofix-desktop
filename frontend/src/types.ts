import type { Language } from './i18n';

export const PROVIDERS = ['openai', 'ollama', 'openrouter', 'huggingface', 'google', 'custom', 'mistral'] as const;
export type Provider = (typeof PROVIDERS)[number];

export const PROVIDER_SENTINEL_UNCONFIGURED = 'none' as const;
export type UnconfiguredProvider = typeof PROVIDER_SENTINEL_UNCONFIGURED;
export type AnyProvider = Provider | UnconfiguredProvider;

export const PROVIDER_DEFAULT_URLS: Record<Provider, string> = {
  openai: 'https://api.openai.com/v1',
  ollama: 'http://localhost:11434',
  openrouter: 'https://openrouter.ai/api/v1',
  huggingface: 'https://router.huggingface.co/v1',
  google: 'https://generativelanguage.googleapis.com/v1beta/openai',
  mistral: 'https://api.mistral.ai/v1',
  custom: '',
};

export const PROVIDER_LABELS: Record<Provider, string> = {
  openai: 'OpenAI',
  ollama: 'Ollama (lokal)',
  openrouter: 'Openrouter',
  huggingface: 'Hugging Face',
  google: 'Google AI Studio',
  mistral: 'Mistral',
  custom: 'Custom',
};

export const PROVIDER_API_KEY_URLS: Partial<Record<Provider, string>> = {
  openai: 'https://platform.openai.com/api-keys',
  mistral: 'https://console.mistral.ai/api-keys',
  openrouter: 'https://openrouter.ai/keys',
  google: 'https://aistudio.google.com/app/apikey',
  huggingface: 'https://huggingface.co/settings/tokens',
};

export const ONBOARDING_PROVIDERS: Provider[] = ['openai', 'mistral', 'openrouter', 'google', 'huggingface'];

export const ONBOARDING_MODELS: { tag: string; label: string }[] = [
  { tag: 'ministral-3:3b', label: 'Ministral 3 (3B)' },
  { tag: 'ministral-3:8b', label: 'Ministral 3 (8B)' },
  { tag: 'ministral-3:14b', label: 'Ministral 3 (14B)' },
];

export const DEFAULT_MODELS_FALLBACK: Partial<Record<Provider, string>> = {
  openai: 'gpt-4o-mini',
  mistral: 'mistral-small-latest',
  openrouter: 'openai/gpt-4o-mini',
  google: 'gemini-2.0-flash',
  huggingface: 'meta-llama/Llama-3.3-70B-Instruct',
};

export const DOCX_COMPARE_MODES = ['openxml', 'word-native', 'libreoffice-uno'] as const;
export type DocxCompareMode = (typeof DOCX_COMPARE_MODES)[number];
export const REASONING_EFFORTS = ['low', 'medium', 'high'] as const;
export type ReasoningEffort = (typeof REASONING_EFFORTS)[number];
export const DOCX_DOCUMENT_PARTS = ['main', 'footnotes', 'endnotes', 'headers', 'footers', 'glossary'] as const;
export type DocxDocumentPart = (typeof DOCX_DOCUMENT_PARTS)[number];
export const DOCX_BATCHING_PARTS = DOCX_DOCUMENT_PARTS;
export type DocxBatchingPart = DocxDocumentPart;
export const DOCX_CORRECTION_SCOPE_PARTS = DOCX_DOCUMENT_PARTS;
export type DocxCorrectionScopePart = DocxDocumentPart;

export const CITATION_NORMALIZATION_MODES = ['auto', 'with_space', 'without_space'] as const;
export type CitationNormalizationMode = (typeof CITATION_NORMALIZATION_MODES)[number];

export interface DocxSettings {
  compare_mode: DocxCompareMode;
  chunk_size: number;
  enable_batching: boolean;
  batching_parts: DocxBatchingPart[];
  correction_scope_parts: DocxCorrectionScopePart[];
  batch_max_chars: number;
  batch_max_paragraphs: number;
  enable_cache: boolean;
  enable_parallelization: boolean;
  max_parallel_requests: number;
  speed_mode: SpeedMode;
  manual_requests_per_minute: number | null;
  restore_non_breaking_spaces: boolean;
  ignore_trailing_paragraph_whitespace: boolean;
  citation_normalization: CitationNormalizationMode;
}

export const SPEED_MODES = ['auto', 'manual'] as const;
export type SpeedMode = (typeof SPEED_MODES)[number];

export interface EditorSettings {
  chunk_size: number;
}

export const FONT_SIZES = ['small', 'default', 'large', 'xl', 'xxl'] as const;
export type FontSize = (typeof FONT_SIZES)[number];

export const FONT_SIZE_PX: Record<FontSize, number> = {
  small: 14,
  default: 16,
  large: 18,
  xl: 20,
  xxl: 22,
};

export const SETTINGS_LIMITS = {
  temperature: { min: 0, max: 2, step: 0.1 },
  chunkSize: { min: 500, max: 50000, step: 500 },
  batchMaxChars: { min: 500, max: 50000, step: 500 },
  batchMaxParagraphs: { min: 1, max: 100, step: 1 },
  maxParallelRequests: { min: 1, max: 16, step: 1 },
} as const;

export interface CustomPromptPreset {
  id: string;
  name: string;
  value: string;
  locale: string;
}

export const OPERATION_MODES = ['correction', 'translation'] as const;
export type OperationMode = (typeof OPERATION_MODES)[number];

// A translation preset carries both prompts as a pair, so they are always switched
// together.
export interface TranslationPromptPreset {
  id: string;
  name: string;
  // Target-language slug (not a Language code): the target language can be free text.
  locale: string;
  main_prompt: string;
  footnote_prompt: string;
}

export interface TranslationSettings {
  target_language: string;
  prompt_presets: TranslationPromptPreset[];
  active_preset_ids: Record<string, string>;
  footnote_prompt: string;
  // Translation's own system prompt, separate from the top-level system_prompt (which is
  // correction-only) — see AdvancedSection's "Übersetzung" heading.
  system_prompt: string;
  // Free-text "other language" entries the user has typed in, remembered across target-
  // language switches. Never contains a
  // EU_LANGUAGE_CODES entry; kept in sync by the Rust backend.
  custom_languages: string[];
}

export interface Settings {
  provider: AnyProvider;
  api_url: string;
  api_key: string | null;
  model: string;
  custom_prompt: string;
  custom_prompt_presets: CustomPromptPreset[];
  active_custom_prompt_preset_id: string;
  active_custom_prompt_preset_ids: Partial<Record<Language, string>>;
  system_prompt: string;
  batch_prompt: string;
  ui_language: Language;
  correction_language: Language;
  auto_check_updates: boolean;
  temperature: number;
  enable_reasoning: boolean;
  reasoning_effort: ReasoningEffort;
  provider_keys: Record<Provider, string | null>;
  editor: EditorSettings;
  docx: DocxSettings;
  font_size: FontSize;
  setup_completed?: boolean | null;
  mode: OperationMode;
  // Gates the translation feature entirely:
  // off by default since it's experimental. While false, Settings only shows the toggle
  // itself (not the rest of the Translation section), and the main window hides the
  // correction/translation mode switch, keeping the app in correction-only mode.
  translation_enabled: boolean;
  translation: TranslationSettings;
}

export interface DocxFile {
  name: string;
  path: string;
  size: number;
  originalPath?: string;
}
