export type Provider = 'openai' | 'ollama' | 'openrouter' | 'huggingface' | 'google' | 'custom' | 'mistral';

export interface DocxSettings {
  compare_mode: 'diff-engine' | 'word' | 'libreoffice';
  enable_batching: boolean;
  batch_max_chars: number;
  batch_max_paragraphs: number;
  enable_cache: boolean;
  enable_parallelization: boolean;
  max_parallel_requests: number;
}

export type FontSize = 'small' | 'default' | 'large' | 'xl' | 'xxl';

export interface Settings {
  provider: Provider;
  api_url: string;
  api_key: string | null;
  model: string;
  custom_prompt: string;
  system_prompt: string;
  batch_prompt: string;
  auto_check_updates: boolean;
  temperature: number;
  provider_keys: Record<Provider, string | null>;
  docx: DocxSettings;
  font_size: FontSize;
}

export interface DocxFile {
  name: string;
  path: string;
  size: number;
  originalPath?: string;
}
