#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::collections::{HashMap, HashSet};
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Arc;
use std::sync::OnceLock;

use anyhow::{anyhow, Context};
use base64::{engine::general_purpose, Engine as _};
use futures_util::StreamExt;
use regex::Regex;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, BufReader};
use tokio::process::Command;
use tokio::sync::Mutex;
use tokio::time::{sleep, Duration};
use uuid::Uuid;

const MAX_OFFICE_UPLOAD_BYTES: usize = 30 * 1024 * 1024;
const BACKEND_API_KEY_ENV: &str = "LINGOFIX_RUNTIME_API_KEY";
const BACKEND_TEMP_DIR_ENV: &str = "LINGOFIX_TEMP_DIR";
const BACKEND_DOTNET_PATH_ENV: &str = "LINGOFIX_DOTNET_PATH";
const SOFFICE_PATH_ENV: &str = "LINGOFIX_SOFFICE_PATH";
const BACKEND_EXECUTABLE_BASE: &str = "lingofix-backend";
const ENCRYPTION_PREFIX: &str = "enc_v1:";
const DEBUG_LOG_FILE_NAME: &str = "debug.log";
const DEBUG_LOG_MAX_BYTES: u64 = 5 * 1024 * 1024;
const DEBUG_LOG_ROTATIONS: usize = 3;
const REASONING_UNSUPPORTED_ERROR_CODE: &str = "REASONING_UNSUPPORTED";
#[cfg(target_os = "macos")]
const AUTOMATION_SETTINGS_PATH: &str = "System Settings > Privacy & Security > Automation";
const LIBREOFFICE_DOWNLOAD_URL: &str = "https://www.libreoffice.org/download/download-libreoffice/";

const DEFAULT_CUSTOM_PROMPT_EN: &str =
    "Correct the following text while maintaining the style and tone.";
const DEFAULT_CUSTOM_PROMPT_DE: &str = "Korrigiere den folgenden Text nach den Duden-Regeln. Korrigiere nur Fehler, alles andere lässt Du unverändert!";
const DEFAULT_SYSTEM_PROMPT_EN: &str =
    "Important: Respond with the corrected text only. No explanations, no notes, no extra sentences.";
const DEFAULT_SYSTEM_PROMPT_DE: &str =
    "Wichtig: Antworte nur mit dem korrigierten Text. Keine Erklärungen, keine Notizen, keine zusätzlichen Sätze.";
const DEFAULT_CUSTOM_PROMPT_PRESET_NAME_EN: &str = "Default";
const DEFAULT_CUSTOM_PROMPT_PRESET_NAME_DE: &str = "Standard";

fn normalize_locale(locale: &str) -> &'static str {
    if locale.trim().to_ascii_lowercase().starts_with("de") {
        "de"
    } else {
        "en"
    }
}

fn default_custom_prompt(locale: &str) -> String {
    if normalize_locale(locale) == "de" {
        DEFAULT_CUSTOM_PROMPT_DE.to_string()
    } else {
        DEFAULT_CUSTOM_PROMPT_EN.to_string()
    }
}

fn default_system_prompt(locale: &str) -> String {
    if normalize_locale(locale) == "de" {
        DEFAULT_SYSTEM_PROMPT_DE.to_string()
    } else {
        DEFAULT_SYSTEM_PROMPT_EN.to_string()
    }
}

fn default_batch_prompt(locale: &str) -> String {
    let _ = locale;
    String::new()
}

fn lingofix_temp_root() -> PathBuf {
    std::env::temp_dir().join("Lingofix")
}

fn default_custom_prompt_preset(locale: &str) -> CustomPromptPreset {
    let normalized_locale = normalize_locale(locale);
    let name = if normalized_locale == "de" {
        DEFAULT_CUSTOM_PROMPT_PRESET_NAME_DE
    } else {
        DEFAULT_CUSTOM_PROMPT_PRESET_NAME_EN
    };

    CustomPromptPreset {
        id: "default".to_string(),
        name: name.to_string(),
        value: default_custom_prompt(normalized_locale),
        locale: normalized_locale.to_string(),
    }
}

fn default_auto_check_updates() -> bool {
    true
}

const KNOWN_PROVIDERS: [&str; 7] = [
    "openai",
    "ollama",
    "openrouter",
    "huggingface",
    "google",
    "custom",
    "mistral",
];
const KNOWN_COMPARE_MODES: [&str; 3] = ["openxml", "word-native", "libreoffice-uno"];
const KNOWN_REASONING_EFFORTS: [&str; 3] = ["low", "medium", "high"];
const KNOWN_FONT_SIZES: [&str; 5] = ["small", "default", "large", "xl", "xxl"];
const MIN_TEMPERATURE: f64 = 0.0;
const MAX_TEMPERATURE: f64 = 2.0;
const MIN_DOCX_CHUNK_SIZE: i32 = 500;
const MAX_DOCX_CHUNK_SIZE: i32 = 50_000;
const MIN_BATCH_MAX_CHARS: i32 = 500;
const MAX_BATCH_MAX_CHARS: i32 = 50_000;
const MIN_BATCH_MAX_PARAGRAPHS: i32 = 1;
const MAX_BATCH_MAX_PARAGRAPHS: i32 = 100;
const MIN_MAX_PARALLEL_REQUESTS: i32 = 1;
const MAX_MAX_PARALLEL_REQUESTS: i32 = 16;
const KNOWN_DOCX_PARTS: [&str; 6] = [
    "main",
    "footnotes",
    "endnotes",
    "headers",
    "footers",
    "glossary",
];

fn default_docx_chunk_size() -> i32 {
    7_500
}

fn default_reasoning_effort() -> String {
    "low".to_string()
}

fn default_batching_parts() -> Vec<String> {
    KNOWN_DOCX_PARTS
        .iter()
        .map(|part| (*part).to_string())
        .collect()
}

fn default_correction_scope_parts() -> Vec<String> {
    KNOWN_DOCX_PARTS
        .iter()
        .map(|part| (*part).to_string())
        .collect()
}

fn is_known_provider(provider: &str) -> bool {
    KNOWN_PROVIDERS
        .iter()
        .any(|known| known.eq_ignore_ascii_case(provider))
}

fn canonical_provider(provider: &str) -> Result<String, String> {
    let trimmed = provider.trim();
    if is_known_provider(trimmed) {
        Ok(trimmed.to_ascii_lowercase())
    } else {
        Err(
            "Invalid settings: provider is unknown. Open Settings > Advanced and use 'Reset app'."
                .to_string(),
        )
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum OfficeInputKind {
    Docx,
    Odt,
}

impl OfficeInputKind {
    fn extension(self) -> &'static str {
        match self {
            Self::Docx => "docx",
            Self::Odt => "odt",
        }
    }
}

#[derive(Default)]
struct CancellationState {
    text: Mutex<Option<Arc<std::sync::atomic::AtomicBool>>>,
    docx: Mutex<Option<Arc<std::sync::atomic::AtomicBool>>>,
}

#[derive(Default)]
struct ModelCapabilityState {
    temperature_support: Mutex<HashMap<String, bool>>,
    reasoning_effort_support: Mutex<HashMap<String, bool>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct DocxSettings {
    compare_mode: String,
    #[serde(default = "default_docx_chunk_size")]
    chunk_size: i32,
    enable_batching: bool,
    #[serde(default = "default_batching_parts")]
    batching_parts: Vec<String>,
    correction_scope_parts: Vec<String>,
    batch_max_chars: i32,
    batch_max_paragraphs: i32,
    enable_cache: bool,
    enable_parallelization: bool,
    max_parallel_requests: i32,
}

impl Default for DocxSettings {
    fn default() -> Self {
        Self {
            compare_mode: "openxml".into(),
            chunk_size: default_docx_chunk_size(),
            enable_batching: true,
            batching_parts: default_batching_parts(),
            correction_scope_parts: default_correction_scope_parts(),
            batch_max_chars: 7_500,
            batch_max_paragraphs: 10,
            enable_cache: true,
            enable_parallelization: true,
            max_parallel_requests: 4,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct CustomPromptPreset {
    id: String,
    name: String,
    value: String,
    locale: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct FrontendSettings {
    provider: String,
    api_url: String,
    api_key: Option<String>,
    model: String,
    custom_prompt: String,
    custom_prompt_presets: Vec<CustomPromptPreset>,
    active_custom_prompt_preset_id: String,
    system_prompt: String,
    batch_prompt: String,
    auto_check_updates: bool,
    temperature: f64,
    enable_reasoning: bool,
    #[serde(default = "default_reasoning_effort")]
    reasoning_effort: String,
    provider_keys: HashMap<String, Option<String>>,
    docx: DocxSettings,
    font_size: String,
}

#[derive(Debug, Clone, Serialize)]
struct WordCompareAccessStatus {
    ok: bool,
    message: String,
    details: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DocxTrackChangesInspection {
    has_track_changes: bool,
}

impl Default for FrontendSettings {
    fn default() -> Self {
        Self::default_for_locale("en")
    }
}

impl FrontendSettings {
    fn default_for_locale(locale: &str) -> Self {
        let normalized_locale = normalize_locale(locale);
        let default_preset = default_custom_prompt_preset(normalized_locale);
        Self {
            provider: "openai".into(),
            api_url: "https://api.openai.com/v1".into(),
            api_key: None,
            model: "gpt-4".into(),
            custom_prompt: default_preset.value.clone(),
            custom_prompt_presets: vec![default_preset.clone()],
            active_custom_prompt_preset_id: default_preset.id,
            system_prompt: default_system_prompt(normalized_locale),
            batch_prompt: default_batch_prompt(normalized_locale),
            auto_check_updates: default_auto_check_updates(),
            temperature: 0.0,
            enable_reasoning: false,
            reasoning_effort: default_reasoning_effort(),
            provider_keys: empty_provider_keys(),
            docx: DocxSettings::default(),
            font_size: "default".into(),
        }
    }
}

fn settings_path(app: &AppHandle) -> anyhow::Result<PathBuf> {
    let dir = app
        .path()
        .app_config_dir()
        .map_err(|e| anyhow!("failed to resolve app config dir: {e}"))?;
    std::fs::create_dir_all(&dir)?;
    Ok(dir.join("settings.json"))
}

fn debug_log_path(app: &AppHandle) -> anyhow::Result<PathBuf> {
    let dir = app
        .path()
        .app_config_dir()
        .map_err(|e| anyhow!("failed to resolve app config dir: {e}"))?;
    std::fs::create_dir_all(&dir)?;
    Ok(dir.join(DEBUG_LOG_FILE_NAME))
}

fn truncate_debug_message(value: &str, max_chars: usize) -> String {
    let mut out = String::new();
    let mut count = 0usize;
    for ch in value.chars() {
        if count >= max_chars {
            break;
        }
        out.push(ch);
        count += 1;
    }
    if value.chars().count() > max_chars {
        out.push_str("...(truncated)");
    }
    out
}

fn rotate_debug_logs_if_needed(path: &Path) {
    let Ok(metadata) = std::fs::metadata(path) else {
        return;
    };
    if metadata.len() < DEBUG_LOG_MAX_BYTES {
        return;
    }

    for index in (1..=DEBUG_LOG_ROTATIONS).rev() {
        let source = if index == 1 {
            path.to_path_buf()
        } else {
            path.with_extension(format!("log.{}", index - 1))
        };
        let target = path.with_extension(format!("log.{index}"));

        if source.exists() {
            let _ = std::fs::remove_file(&target);
            let _ = std::fs::rename(&source, &target);
        }
    }
}

fn debug_log_lock() -> &'static std::sync::Mutex<()> {
    static LOCK: OnceLock<std::sync::Mutex<()>> = OnceLock::new();
    LOCK.get_or_init(|| std::sync::Mutex::new(()))
}

fn write_debug_event(app: &AppHandle, event: &str, details: Value) {
    let Ok(path) = debug_log_path(app) else {
        return;
    };

    let Ok(_guard) = debug_log_lock().lock() else {
        return;
    };

    rotate_debug_logs_if_needed(&path);
    let timestamp_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or_default();
    let payload = json!({
        "ts_ms": timestamp_ms,
        "event": event,
        "details": details
    });

    let Ok(mut file) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
    else {
        return;
    };

    if let Ok(line) = serde_json::to_string(&payload) {
        let _ = std::io::Write::write_all(&mut file, line.as_bytes());
        let _ = std::io::Write::write_all(&mut file, b"\n");
    }
}

fn weak_device_secret() -> Vec<u8> {
    let user = std::env::var("USER").unwrap_or_default();
    let home = std::env::var("HOME").unwrap_or_default();
    let host = std::env::var("HOSTNAME").unwrap_or_default();
    let seed = format!(
        "lingofix|{}|{}|{}|{}|{}",
        user,
        home,
        host,
        std::env::consts::OS,
        std::env::consts::ARCH
    );
    Sha256::digest(seed.as_bytes()).to_vec()
}

fn build_keystream(secret: &[u8], nonce: &[u8], len: usize) -> Vec<u8> {
    let mut out = Vec::with_capacity(len);
    let mut counter: u32 = 0;
    while out.len() < len {
        let mut hasher = Sha256::new();
        hasher.update(secret);
        hasher.update(nonce);
        hasher.update(counter.to_le_bytes());
        out.extend_from_slice(&hasher.finalize());
        counter = counter.wrapping_add(1);
    }
    out.truncate(len);
    out
}

fn encrypt_secret(raw: &str) -> String {
    if raw.is_empty() || raw.starts_with(ENCRYPTION_PREFIX) {
        return raw.to_string();
    }

    let nonce = *Uuid::new_v4().as_bytes();
    let secret = weak_device_secret();
    let bytes = raw.as_bytes();
    let stream = build_keystream(&secret, &nonce, bytes.len());
    let cipher: Vec<u8> = bytes
        .iter()
        .zip(stream.iter())
        .map(|(a, b)| a ^ b)
        .collect();

    let mut payload = Vec::with_capacity(nonce.len() + cipher.len());
    payload.extend_from_slice(&nonce);
    payload.extend_from_slice(&cipher);
    format!(
        "{}{}",
        ENCRYPTION_PREFIX,
        general_purpose::STANDARD.encode(payload)
    )
}

fn decrypt_secret(raw: &str) -> Option<String> {
    if raw.trim().is_empty() {
        return Some(String::new());
    }

    if !raw.starts_with(ENCRYPTION_PREFIX) {
        return Some(raw.to_string());
    }

    let data = general_purpose::STANDARD
        .decode(&raw[ENCRYPTION_PREFIX.len()..])
        .ok()?;
    if data.len() < 16 {
        return None;
    }

    let (nonce, cipher) = data.split_at(16);
    let secret = weak_device_secret();
    let stream = build_keystream(&secret, nonce, cipher.len());
    let plain: Vec<u8> = cipher
        .iter()
        .zip(stream.iter())
        .map(|(a, b)| a ^ b)
        .collect();

    String::from_utf8(plain).ok()
}

fn decrypt_settings_secrets(mut settings: FrontendSettings) -> FrontendSettings {
    settings.api_key = settings
        .api_key
        .as_deref()
        .and_then(decrypt_secret)
        .filter(|v| !v.trim().is_empty());

    for value in settings.provider_keys.values_mut() {
        let decrypted = value
            .as_deref()
            .and_then(decrypt_secret)
            .filter(|v| !v.trim().is_empty());
        *value = decrypted;
    }

    settings
}

fn encrypt_settings_secrets(mut settings: FrontendSettings) -> FrontendSettings {
    settings.api_key = settings
        .api_key
        .as_deref()
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .map(encrypt_secret);

    for value in settings.provider_keys.values_mut() {
        let encrypted = value
            .as_deref()
            .map(str::trim)
            .filter(|v| !v.is_empty())
            .map(encrypt_secret);
        *value = encrypted;
    }

    settings
}

fn sanitize_settings_for_disk(mut settings: FrontendSettings) -> FrontendSettings {
    settings.api_key = None;
    let mut cleaned_keys: HashMap<String, Option<String>> = HashMap::new();
    for provider in KNOWN_PROVIDERS {
        let maybe_key = settings
            .provider_keys
            .get(provider)
            .and_then(|v| v.as_ref())
            .map(|v| v.trim().to_string())
            .filter(|v| !v.is_empty());
        cleaned_keys.insert(provider.to_string(), maybe_key);
    }
    settings.provider_keys = cleaned_keys;
    settings
}

fn resolve_provider_key(settings: &FrontendSettings) -> Option<String> {
    if let Some(value) = settings.api_key.as_ref().filter(|v| !v.trim().is_empty()) {
        return Some(value.clone());
    }

    settings
        .provider_keys
        .get(&settings.provider)
        .cloned()
        .flatten()
        .filter(|v| !v.trim().is_empty())
}

fn office_input_kind(path: &Path) -> Option<OfficeInputKind> {
    let ext = path.extension()?.to_str()?.to_ascii_lowercase();
    match ext.as_str() {
        "docx" => Some(OfficeInputKind::Docx),
        "odt" => Some(OfficeInputKind::Odt),
        _ => None,
    }
}

fn normalize_office_input_path(path: &str) -> anyhow::Result<(PathBuf, OfficeInputKind)> {
    let trimmed = path.trim();
    if trimmed.is_empty() {
        return Err(anyhow!("path is empty"));
    }

    let canonical =
        std::fs::canonicalize(trimmed).with_context(|| format!("invalid path: {trimmed}"))?;
    if !canonical.exists() {
        return Err(anyhow!("path does not exist"));
    }
    if !canonical.is_file() {
        return Err(anyhow!("path is not a file"));
    }

    let kind = office_input_kind(&canonical)
        .ok_or_else(|| anyhow!("only .docx and .odt files are allowed"))?;

    Ok((canonical, kind))
}

fn normalize_existing_path(path: &str) -> anyhow::Result<PathBuf> {
    let trimmed = path.trim();
    if trimmed.is_empty() {
        return Err(anyhow!("path is empty"));
    }

    let canonical =
        std::fs::canonicalize(trimmed).with_context(|| format!("invalid path: {trimmed}"))?;
    if !canonical.exists() {
        return Err(anyhow!("path does not exist"));
    }

    Ok(canonical)
}

fn frontend_path(path: &str) -> String {
    if let Some(rest) = path.strip_prefix(r"\\?\UNC\") {
        return format!(r"\\{}", rest);
    }

    if let Some(rest) = path.strip_prefix(r"\\?\") {
        return rest.to_string();
    }

    path.to_string()
}

fn resolve_temp_root_from_settings(app: &AppHandle) -> PathBuf {
    let _ = app;
    lingofix_temp_root()
}

fn temp_root_from_settings(settings: &FrontendSettings) -> PathBuf {
    let _ = settings;
    lingofix_temp_root()
}

fn clear_temp_lingofix_dir() {
    let temp_root = lingofix_temp_root();
    if !temp_root.exists() {
        return;
    }

    if let Err(err) = std::fs::remove_dir_all(&temp_root) {
        eprintln!("Failed to clear temp Lingofix directory at startup: {err}");
    }
}

#[tauri::command]
async fn load_settings(app: AppHandle, locale: Option<String>) -> Result<FrontendSettings, String> {
    let locale = locale.as_deref().unwrap_or("en");
    let path = settings_path(&app).map_err(|e| e.to_string())?;
    if !path.exists() {
        let defaults = FrontendSettings::default_for_locale(locale);
        validate_settings(&defaults)?;
        write_settings_file(&path, defaults.clone()).await?;
        return Ok(defaults);
    }

    let content = tokio::fs::read_to_string(&path)
        .await
        .map_err(|e| e.to_string())?;
    let raw_settings: FrontendSettings = serde_json::from_str(&content).map_err(|e| {
        format!("Could not parse settings.json: {e}. Open Settings > Advanced and use 'Reset app'.")
    })?;
    let has_plain_secrets = raw_settings
        .api_key
        .as_ref()
        .map(|v| !v.trim().is_empty() && !v.starts_with(ENCRYPTION_PREFIX))
        .unwrap_or(false)
        || raw_settings.provider_keys.values().any(|value| {
            value
                .as_ref()
                .map(|v| !v.trim().is_empty() && !v.starts_with(ENCRYPTION_PREFIX))
                .unwrap_or(false)
        });

    let mut settings = decrypt_settings_secrets(raw_settings);
    settings.provider = canonical_provider(&settings.provider)?;
    sync_custom_prompt_with_active_preset(&mut settings)?;
    validate_settings(&settings)?;

    if settings
        .api_key
        .as_ref()
        .map(|v| v.trim().is_empty())
        .unwrap_or(true)
    {
        settings.api_key = settings
            .provider_keys
            .get(&settings.provider)
            .cloned()
            .flatten()
            .filter(|v| !v.trim().is_empty());
    }

    if has_plain_secrets {
        let _ = write_settings_file(&path, settings.clone()).await;
    }

    Ok(settings)
}

fn validate_settings(settings: &FrontendSettings) -> Result<(), String> {
    let reset_hint = "Open Settings > Advanced and use 'Reset app'.";
    canonical_provider(&settings.provider)?;

    if settings.api_url.trim().is_empty() {
        return Err(format!(
            "Invalid settings: api_url is missing. {reset_hint}"
        ));
    }

    if settings.model.trim().is_empty() {
        return Err(format!("Invalid settings: model is missing. {reset_hint}"));
    }

    if settings.custom_prompt.trim().is_empty() {
        return Err(format!(
            "Invalid settings: custom_prompt is missing. {reset_hint}"
        ));
    }

    if settings.system_prompt.trim().is_empty() {
        return Err(format!(
            "Invalid settings: system_prompt is missing. {reset_hint}"
        ));
    }

    if settings.custom_prompt_presets.is_empty() {
        return Err(format!(
            "Invalid settings: custom_prompt_presets is empty. {reset_hint}"
        ));
    }

    if settings.active_custom_prompt_preset_id.trim().is_empty() {
        return Err(format!(
            "Invalid settings: active_custom_prompt_preset_id is missing. {reset_hint}"
        ));
    }

    let mut seen_ids = HashSet::new();
    let mut active_found = false;
    for preset in &settings.custom_prompt_presets {
        if preset.id.trim().is_empty() {
            return Err(format!(
                "Invalid settings: custom prompt preset id is missing. {reset_hint}"
            ));
        }
        if !seen_ids.insert(preset.id.to_ascii_lowercase()) {
            return Err(format!(
                "Invalid settings: duplicate custom prompt preset ids. {reset_hint}"
            ));
        }
        if preset.name.trim().is_empty() {
            return Err(format!(
                "Invalid settings: custom prompt preset name is missing. {reset_hint}"
            ));
        }
        if preset.value.trim().is_empty() {
            return Err(format!(
                "Invalid settings: custom prompt preset value is missing. {reset_hint}"
            ));
        }
        if normalize_locale(&preset.locale) != preset.locale.trim().to_ascii_lowercase() {
            return Err(format!(
                "Invalid settings: custom prompt preset locale is invalid. {reset_hint}"
            ));
        }
        if preset
            .id
            .eq_ignore_ascii_case(&settings.active_custom_prompt_preset_id)
        {
            active_found = true;
        }
    }

    if !active_found {
        return Err(format!(
            "Invalid settings: active custom prompt preset does not exist. {reset_hint}"
        ));
    }

    if !settings.provider_keys.keys().all(|key| {
        KNOWN_PROVIDERS
            .iter()
            .any(|provider| provider == &key.as_str())
    }) {
        return Err(format!(
            "Invalid settings: provider_keys contains unknown providers. {reset_hint}"
        ));
    }

    if settings.provider_keys.len() != KNOWN_PROVIDERS.len() {
        return Err(format!(
            "Invalid settings: provider_keys is incomplete. {reset_hint}"
        ));
    }

    if !KNOWN_COMPARE_MODES
        .iter()
        .any(|mode| mode.eq_ignore_ascii_case(settings.docx.compare_mode.trim()))
    {
        return Err(format!(
            "Invalid settings: docx.compare_mode is invalid. {reset_hint}"
        ));
    }

    if !KNOWN_FONT_SIZES
        .iter()
        .any(|size| size.eq_ignore_ascii_case(settings.font_size.trim()))
    {
        return Err(format!(
            "Invalid settings: font_size is invalid. {reset_hint}"
        ));
    }

    if settings.temperature.is_nan()
        || settings.temperature.is_infinite()
        || settings.temperature < MIN_TEMPERATURE
        || settings.temperature > MAX_TEMPERATURE
    {
        return Err(format!(
            "Invalid settings: temperature is out of range. {reset_hint}"
        ));
    }

    if settings.docx.batch_max_chars < MIN_BATCH_MAX_CHARS
        || settings.docx.batch_max_chars > MAX_BATCH_MAX_CHARS
    {
        return Err(format!(
            "Invalid settings: docx.batch_max_chars is out of range. {reset_hint}"
        ));
    }

    if settings.docx.chunk_size < MIN_DOCX_CHUNK_SIZE
        || settings.docx.chunk_size > MAX_DOCX_CHUNK_SIZE
    {
        return Err(format!(
            "Invalid settings: docx.chunk_size is out of range. {reset_hint}"
        ));
    }

    if settings.docx.batch_max_paragraphs < MIN_BATCH_MAX_PARAGRAPHS
        || settings.docx.batch_max_paragraphs > MAX_BATCH_MAX_PARAGRAPHS
    {
        return Err(format!(
            "Invalid settings: docx.batch_max_paragraphs is out of range. {reset_hint}"
        ));
    }

    if settings.docx.batching_parts.is_empty() {
        return Err(format!(
            "Invalid settings: docx.batching_parts is empty. {reset_hint}"
        ));
    }

    if !settings
        .docx
        .batching_parts
        .iter()
        .all(|part| KNOWN_DOCX_PARTS.iter().any(|known| known.eq_ignore_ascii_case(part.trim())))
    {
        return Err(format!(
            "Invalid settings: docx.batching_parts contains unknown values. {reset_hint}"
        ));
    }

    if !KNOWN_REASONING_EFFORTS
        .iter()
        .any(|effort| effort.eq_ignore_ascii_case(settings.reasoning_effort.trim()))
    {
        return Err(format!(
            "Invalid settings: reasoning_effort is invalid. {reset_hint}"
        ));
    }

    if settings.docx.correction_scope_parts.is_empty() {
        return Err(format!(
            "Invalid settings: docx.correction_scope_parts is empty. {reset_hint}"
        ));
    }

    if !settings
        .docx
        .correction_scope_parts
        .iter()
        .all(|part| KNOWN_DOCX_PARTS.iter().any(|known| known.eq_ignore_ascii_case(part.trim())))
    {
        return Err(format!(
            "Invalid settings: docx.correction_scope_parts contains unknown values. {reset_hint}"
        ));
    }

    if settings.docx.max_parallel_requests < MIN_MAX_PARALLEL_REQUESTS
        || settings.docx.max_parallel_requests > MAX_MAX_PARALLEL_REQUESTS
    {
        return Err(format!(
            "Invalid settings: docx.max_parallel_requests is out of range. {reset_hint}"
        ));
    }

    Ok(())
}

fn sync_custom_prompt_with_active_preset(settings: &mut FrontendSettings) -> Result<(), String> {
    let active_id = settings.active_custom_prompt_preset_id.trim();
    if active_id.is_empty() {
        return Err("Invalid settings: active custom prompt preset id is missing. Open Settings > Advanced and use 'Reset app'.".to_string());
    }

    let Some(active_preset) = settings
        .custom_prompt_presets
        .iter_mut()
        .find(|preset| preset.id.eq_ignore_ascii_case(active_id))
    else {
        return Err("Invalid settings: active custom prompt preset does not exist. Open Settings > Advanced and use 'Reset app'.".to_string());
    };

    active_preset.locale = normalize_locale(&active_preset.locale).to_string();
    settings.active_custom_prompt_preset_id = active_preset.id.trim().to_string();
    settings.custom_prompt = active_preset.value.trim().to_string();
    Ok(())
}

fn empty_provider_keys() -> HashMap<String, Option<String>> {
    let mut keys = HashMap::new();
    for provider in KNOWN_PROVIDERS {
        keys.insert(provider.to_string(), None);
    }
    keys
}

async fn write_settings_file(path: &Path, settings: FrontendSettings) -> Result<(), String> {
    let encrypted = encrypt_settings_secrets(sanitize_settings_for_disk(settings));
    let content = serde_json::to_string_pretty(&encrypted).map_err(|e| e.to_string())?;
    tokio::fs::write(path, content)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn save_settings(app: AppHandle, settings: FrontendSettings) -> Result<(), String> {
    let normalized_provider = canonical_provider(&settings.provider)?;
    let mut normalized_settings = settings;
    normalized_settings.provider = normalized_provider.clone();
    sync_custom_prompt_with_active_preset(&mut normalized_settings)?;
    if let Some(active) = normalized_settings
        .api_key
        .as_ref()
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty())
    {
        normalized_settings
            .provider_keys
            .insert(normalized_provider, Some(active));
    }

    validate_settings(&normalized_settings)?;

    let path = settings_path(&app).map_err(|e| e.to_string())?;
    write_settings_file(&path, normalized_settings).await
}

#[tauri::command]
async fn reset_settings(
    app: AppHandle,
    locale: Option<String>,
) -> Result<FrontendSettings, String> {
    let path = settings_path(&app).map_err(|e| e.to_string())?;
    let defaults = FrontendSettings::default_for_locale(locale.as_deref().unwrap_or("en"));
    validate_settings(&defaults)?;
    write_settings_file(&path, defaults.clone()).await?;
    Ok(defaults)
}

#[tauri::command]
async fn fetch_models(
    app: AppHandle,
    api_url: String,
    api_key: Option<String>,
    provider: String,
) -> Result<Vec<String>, String> {
    let client = reqwest::Client::new();
    let started_at = std::time::Instant::now();
    let effective_key = api_key.filter(|v| !v.trim().is_empty());

    write_debug_event(
        &app,
        "models.fetch.start",
        json!({
            "provider": provider.as_str(),
            "api_url": api_url.as_str(),
            "has_api_key": effective_key.is_some()
        }),
    );

    if provider.eq_ignore_ascii_case("ollama") {
        let url = format!("{}/api/tags", api_url.trim_end_matches('/'));
        let resp = client.get(url).send().await.map_err(|e| e.to_string())?;
        let value: Value = resp.json().await.map_err(|e| e.to_string())?;
        let models = value
            .get("models")
            .and_then(|v| v.as_array())
            .map(|arr| {
                arr.iter()
                    .filter_map(|m| {
                        m.get("model")
                            .and_then(|s| s.as_str())
                            .map(|s| s.to_string())
                    })
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        write_debug_event(
            &app,
            "models.fetch.success",
            json!({
                "provider": provider.as_str(),
                "model_count": models.len(),
                "duration_ms": started_at.elapsed().as_millis()
            }),
        );
        return Ok(models);
    }

    let url = format!("{}/models", api_url.trim_end_matches('/'));
    let mut req = client.get(url);
    if let Some(key) = effective_key {
        if !key.trim().is_empty() {
            req = req.bearer_auth(key);
        }
    }

    let resp = req.send().await.map_err(|e| e.to_string())?;
    let value: Value = resp.json().await.map_err(|e| e.to_string())?;
    let mut models = Vec::new();
    if let Some(arr) = value.get("data").and_then(|v| v.as_array()) {
        for m in arr {
            if let Some(id) = m.get("id").and_then(|s| s.as_str()) {
                let l = id.to_lowercase();
                if l.contains("vision") || l.contains("audio") || l.contains("image") {
                    continue;
                }
                models.push(id.to_string());
            }
        }
    }
    write_debug_event(
        &app,
        "models.fetch.success",
        json!({
            "provider": provider.as_str(),
            "model_count": models.len(),
            "duration_ms": started_at.elapsed().as_millis()
        }),
    );
    Ok(models)
}

#[tauri::command]
async fn cancel_correction(state: State<'_, CancellationState>) -> Result<(), String> {
    let guard = state.text.lock().await;
    if let Some(flag) = guard.as_ref() {
        flag.store(true, std::sync::atomic::Ordering::Relaxed);
    }
    Ok(())
}

#[tauri::command]
async fn correct_text_streaming(
    app: AppHandle,
    state: State<'_, CancellationState>,
    capability_state: State<'_, ModelCapabilityState>,
    text: String,
    settings: FrontendSettings,
) -> Result<(), String> {
    let started_at = std::time::Instant::now();
    let mut effective_settings = settings;
    effective_settings.api_key = resolve_provider_key(&effective_settings);

    write_debug_event(
        &app,
        "editor.correction.start",
        json!({
            "provider": effective_settings.provider.as_str(),
            "model": effective_settings.model.as_str(),
            "api_url": effective_settings.api_url.as_str(),
            "input_chars": text.chars().count(),
            "temperature": effective_settings.temperature,
            "has_api_key": effective_settings.api_key.as_ref().map(|v| !v.trim().is_empty()).unwrap_or(false)
        }),
    );

    if !effective_settings.provider.eq_ignore_ascii_case("ollama")
        && effective_settings
            .api_key
            .as_ref()
            .map(|v| v.trim().is_empty())
            .unwrap_or(true)
    {
        let message = "API key missing. Please open settings and provide an API key.".to_string();
        write_debug_event(
            &app,
            "editor.correction.error",
            json!({
                "provider": effective_settings.provider.as_str(),
                "model": effective_settings.model.as_str(),
                "duration_ms": started_at.elapsed().as_millis(),
                "error": message
            }),
        );
        let _ = app.emit("correction_error", message.clone());
        return Err(message);
    }

    let cancel_flag = Arc::new(std::sync::atomic::AtomicBool::new(false));
    {
        let mut guard = state.text.lock().await;
        *guard = Some(cancel_flag.clone());
    }

    let _ = app.emit("correction_started", json!(null));
    let result = if effective_settings.provider.eq_ignore_ascii_case("ollama") {
        stream_ollama(&app, &cancel_flag, &text, &effective_settings).await
    } else {
        stream_openai_like(
            &app,
            &cancel_flag,
            &text,
            &effective_settings,
            capability_state.inner(),
        )
        .await
    };

    {
        let mut guard = state.text.lock().await;
        *guard = None;
    }

    if let Err(err) = result {
        let message = err.to_string();
        write_debug_event(
            &app,
            "editor.correction.error",
            json!({
                "provider": effective_settings.provider.as_str(),
                "model": effective_settings.model.as_str(),
                "duration_ms": started_at.elapsed().as_millis(),
                "error": truncate_debug_message(&message, 1200)
            }),
        );
        let _ = app.emit("correction_error", message.clone());
        return Err(message);
    }

    write_debug_event(
        &app,
        "editor.correction.success",
        json!({
            "provider": effective_settings.provider.as_str(),
            "model": effective_settings.model.as_str(),
            "duration_ms": started_at.elapsed().as_millis()
        }),
    );

    Ok(())
}

async fn stream_ollama(
    app: &AppHandle,
    cancel_flag: &Arc<std::sync::atomic::AtomicBool>,
    text: &str,
    settings: &FrontendSettings,
) -> anyhow::Result<()> {
    let client = reqwest::Client::new();
    let url = format!("{}/api/chat", settings.api_url.trim_end_matches('/'));
    let request_started_at = std::time::Instant::now();
    let mut first_token_ms: Option<u128> = None;
    let prompt = format!(
        "{} {}\n\nText:\n{}",
        settings.custom_prompt.trim(),
        settings.system_prompt.trim(),
        text
    );
    let body = json!({
        "model": settings.model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": true,
        "temperature": settings.temperature
    });

    let resp = client.post(url).json(&body).send().await?;
    if !resp.status().is_success() {
        let status = resp.status();
        let body = resp.text().await.unwrap_or_default();
        let detail = extract_api_error_message(&body);
        write_debug_event(
            app,
            "editor.ollama.http_error",
            json!({
                "provider": settings.provider.as_str(),
                "model": settings.model.as_str(),
                "status": status.as_u16(),
                "error": truncate_debug_message(&detail, 1000),
                "elapsed_ms": request_started_at.elapsed().as_millis()
            }),
        );
        return Err(anyhow!("Ollama request failed ({}): {}", status, detail));
    }
    let mut stream = resp.bytes_stream();
    let mut buf = String::new();
    let mut all = String::new();

    while let Some(chunk) = stream.next().await {
        if cancel_flag.load(std::sync::atomic::Ordering::Relaxed) {
            let _ = app.emit("correction_complete", all.clone());
            return Ok(());
        }

        let chunk = chunk?;
        buf.push_str(&String::from_utf8_lossy(&chunk));
        while let Some(pos) = buf.find('\n') {
            let line = buf[..pos].trim().to_string();
            buf = buf[pos + 1..].to_string();
            if line.is_empty() {
                continue;
            }
            if let Ok(value) = serde_json::from_str::<Value>(&line) {
                if let Some(content) = value
                    .get("message")
                    .and_then(|m| m.get("content"))
                    .and_then(|c| c.as_str())
                {
                    if first_token_ms.is_none() {
                        first_token_ms = Some(request_started_at.elapsed().as_millis());
                    }
                    all.push_str(content);
                    let _ = app.emit("correction_chunk", remove_markdown(&all));
                }
                if value.get("done").and_then(|d| d.as_bool()).unwrap_or(false) {
                    let final_text = remove_markdown(&all);
                    if final_text.trim().is_empty() {
                        return Err(anyhow!("Ollama returned an empty correction result"));
                    }
                    write_debug_event(
                        app,
                        "editor.ollama.complete",
                        json!({
                            "provider": settings.provider.as_str(),
                            "model": settings.model.as_str(),
                            "output_chars": final_text.chars().count(),
                            "first_token_ms": first_token_ms,
                            "elapsed_ms": request_started_at.elapsed().as_millis()
                        }),
                    );
                    let _ = app.emit("correction_complete", final_text);
                    return Ok(());
                }
            }
        }
    }

    let final_text = remove_markdown(&all);
    if final_text.trim().is_empty() {
        return Err(anyhow!("Ollama returned an empty correction result"));
    }

    write_debug_event(
        app,
        "editor.ollama.complete",
        json!({
            "provider": settings.provider.as_str(),
            "model": settings.model.as_str(),
            "output_chars": final_text.chars().count(),
            "first_token_ms": first_token_ms,
            "elapsed_ms": request_started_at.elapsed().as_millis()
        }),
    );

    let _ = app.emit("correction_complete", final_text);
    Ok(())
}

async fn stream_openai_like(
    app: &AppHandle,
    cancel_flag: &Arc<std::sync::atomic::AtomicBool>,
    text: &str,
    settings: &FrontendSettings,
    capability_state: &ModelCapabilityState,
) -> anyhow::Result<()> {
    let client = reqwest::Client::new();
    let url = format!(
        "{}/chat/completions",
        settings.api_url.trim_end_matches('/')
    );
    let request_started_at = std::time::Instant::now();
    let mut first_token_ms: Option<u128> = None;
    let prompt = format!(
        "{} {}\n\nText:\n{}",
        settings.custom_prompt.trim(),
        settings.system_prompt.trim(),
        text
    );
    let cache_key = temperature_capability_key(settings);
    let mut include_temperature = {
        let cache = capability_state.temperature_support.lock().await;
        cache.get(&cache_key).copied().unwrap_or(true)
    };
    let mut include_reasoning_effort = {
        settings.enable_reasoning && {
            let cache = capability_state.reasoning_effort_support.lock().await;
            cache.get(&cache_key).copied().unwrap_or(true)
        }
    };

    let resp = loop {
        let request_future = send_openai_like_request(
            &client,
            &url,
            settings,
            &prompt,
            include_reasoning_effort,
            include_temperature,
        );
        tokio::pin!(request_future);

        let response = loop {
            tokio::select! {
                result = &mut request_future => {
                    break result?;
                }
                _ = sleep(Duration::from_millis(120)) => {
                    if cancel_flag.load(std::sync::atomic::Ordering::Relaxed) {
                        let _ = app.emit("correction_complete", text.to_string());
                        return Ok(());
                    }
                }
            }
        };

        if response.status().is_success() {
            if include_temperature {
                let mut cache = capability_state.temperature_support.lock().await;
                cache.insert(cache_key.clone(), true);
            }
            if include_reasoning_effort {
                let mut cache = capability_state.reasoning_effort_support.lock().await;
                cache.insert(cache_key.clone(), true);
            }
            break response;
        }

        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        let detail = extract_api_error_message(&body);
        write_debug_event(
            app,
            "editor.stream.http_error",
            json!({
                "provider": settings.provider.as_str(),
                "model": settings.model.as_str(),
                "status": status.as_u16(),
                "include_temperature": include_temperature,
                "include_reasoning_effort": include_reasoning_effort,
                "error": truncate_debug_message(&detail, 1000),
                "elapsed_ms": request_started_at.elapsed().as_millis()
            }),
        );

        if status.as_u16() == 400
            && include_temperature
            && is_temperature_unsupported_error(&detail)
        {
            include_temperature = false;
            write_debug_event(
                app,
                "editor.stream.fallback",
                json!({
                    "provider": settings.provider.as_str(),
                    "model": settings.model.as_str(),
                    "kind": "temperature",
                    "elapsed_ms": request_started_at.elapsed().as_millis()
                }),
            );
            let mut cache = capability_state.temperature_support.lock().await;
            cache.insert(cache_key.clone(), false);
            continue;
        }

        if status.as_u16() == 400
            && include_reasoning_effort
            && is_reasoning_or_thinking_error(&detail)
        {
            let mut cache = capability_state.reasoning_effort_support.lock().await;
            cache.insert(cache_key.clone(), false);

            if settings.enable_reasoning {
                return Err(anyhow!(
                    "{}: reasoning_effort is not supported by this model/provider. {}",
                    REASONING_UNSUPPORTED_ERROR_CODE,
                    detail
                ));
            }

            include_reasoning_effort = false;
            write_debug_event(
                app,
                "editor.stream.fallback",
                json!({
                    "provider": settings.provider.as_str(),
                    "model": settings.model.as_str(),
                    "kind": "reasoning_effort",
                    "elapsed_ms": request_started_at.elapsed().as_millis()
                }),
            );
            continue;
        }

        return Err(anyhow!("LLM request failed ({}): {}", status, detail));
    };

    let mut stream = resp.bytes_stream();
    let mut buf = String::new();
    let mut all = String::new();

    loop {
        tokio::select! {
            _ = sleep(Duration::from_millis(120)) => {
                if cancel_flag.load(std::sync::atomic::Ordering::Relaxed) {
                    let partial = remove_markdown(&all);
                    let payload = if partial.trim().is_empty() { text.to_string() } else { partial };
                    let _ = app.emit("correction_complete", payload);
                    return Ok(());
                }
            }
            maybe_chunk = stream.next() => {
                let Some(chunk) = maybe_chunk else { break; };
                let chunk = chunk?;
                buf.push_str(&String::from_utf8_lossy(&chunk));

                while let Some(pos) = buf.find('\n') {
                    let line = buf[..pos].trim().to_string();
                    buf = buf[pos + 1..].to_string();

                    if !line.starts_with("data: ") {
                        continue;
                    }

                    let data = &line[6..];
                    if data == "[DONE]" {
                        let final_text = remove_markdown(&all);
                        if final_text.trim().is_empty() {
                            return Err(anyhow!("LLM returned an empty correction result"));
                        }
                        write_debug_event(
                            app,
                            "editor.stream.complete",
                            json!({
                                "provider": settings.provider.as_str(),
                                "model": settings.model.as_str(),
                                "output_chars": final_text.chars().count(),
                                "first_token_ms": first_token_ms,
                                "elapsed_ms": request_started_at.elapsed().as_millis()
                            }),
                        );
                        let _ = app.emit("correction_complete", final_text);
                        return Ok(());
                    }

                    if let Ok(value) = serde_json::from_str::<Value>(data) {
                        if let Some(content) = extract_openai_like_stream_content(&value) {
                            if !content.is_empty() {
                                if first_token_ms.is_none() {
                                    first_token_ms = Some(request_started_at.elapsed().as_millis());
                                }
                                all.push_str(&content);
                                let _ = app.emit("correction_chunk", remove_markdown(&all));
                            }
                        }
                    }
                }
            }
        }
    }

    let final_text = remove_markdown(&all);
    if final_text.trim().is_empty() {
        return Err(anyhow!("LLM returned an empty correction result"));
    }

    write_debug_event(
        app,
        "editor.stream.complete",
        json!({
            "provider": settings.provider.as_str(),
            "model": settings.model.as_str(),
            "output_chars": final_text.chars().count(),
            "first_token_ms": first_token_ms,
            "elapsed_ms": request_started_at.elapsed().as_millis()
        }),
    );

    let _ = app.emit("correction_complete", final_text);
    Ok(())
}

async fn send_openai_like_request(
    client: &reqwest::Client,
    url: &str,
    settings: &FrontendSettings,
    prompt: &str,
    include_reasoning_effort: bool,
    include_temperature: bool,
) -> anyhow::Result<reqwest::Response> {
    let mut body = json!({
        "model": settings.model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": true
    });

    if include_temperature {
        body["temperature"] = json!(settings.temperature);
    }

    if include_reasoning_effort {
        body["reasoning_effort"] = json!(settings.reasoning_effort);
    }

    let mut req = client.post(url).json(&body);
    if let Some(api_key) = settings.api_key.as_ref() {
        if !api_key.trim().is_empty() {
            req = req.bearer_auth(api_key);
        }
    }

    Ok(req.send().await?)
}

fn is_reasoning_or_thinking_error(message: &str) -> bool {
    let m = message.to_lowercase();
    m.contains("reasoning") || m.contains("thinking")
}

fn is_temperature_unsupported_error(message: &str) -> bool {
    let m = message.to_lowercase();
    m.contains("temperature")
        && (m.contains("not support")
            || m.contains("does not support")
            || m.contains("unsupported")
            || m.contains("not allowed")
            || m.contains("invalid"))
}

fn extract_openai_like_stream_content(value: &Value) -> Option<String> {
    if let Some(event_type) = value.get("type").and_then(Value::as_str) {
        if event_type.contains("output_text") {
            if let Some(delta) = value.get("delta").and_then(Value::as_str) {
                if !delta.is_empty() {
                    return Some(delta.to_string());
                }
            }
            if let Some(text) = value.get("text").and_then(Value::as_str) {
                if !text.is_empty() {
                    return Some(text.to_string());
                }
            }
        }
    }

    if let Some(first_choice) = value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|arr| arr.first())
    {
        if let Some(delta) = first_choice.get("delta") {
            if let Some(content) = delta.get("content") {
                if let Some(text) = read_text_content_value(content) {
                    return Some(text);
                }
            }

            if let Some(text) = delta.get("text").and_then(Value::as_str) {
                if !text.is_empty() {
                    return Some(text.to_string());
                }
            }
        }

        if let Some(message) = first_choice.get("message") {
            if let Some(content) = message.get("content") {
                if let Some(text) = read_text_content_value(content) {
                    return Some(text);
                }
            }
        }

        if let Some(text) = first_choice.get("text").and_then(Value::as_str) {
            if !text.is_empty() {
                return Some(text.to_string());
            }
        }
    }

    if let Some(output_text) = value.get("output_text") {
        if let Some(text) = read_text_content_value(output_text) {
            return Some(text);
        }
    }

    if let Some(output) = value.get("output").and_then(Value::as_array) {
        let mut parts = Vec::new();
        for item in output {
            if let Some(content) = item.get("content") {
                if let Some(text) = read_text_content_value(content) {
                    if !text.is_empty() {
                        parts.push(text);
                    }
                }
            }
        }
        if !parts.is_empty() {
            return Some(parts.join(""));
        }
    }

    None
}

fn read_text_content_value(content: &Value) -> Option<String> {
    match content {
        Value::String(s) => {
            if s.is_empty() {
                None
            } else {
                Some(s.clone())
            }
        }
        Value::Array(items) => {
            let mut parts = Vec::new();
            for item in items {
                if let Some(text) = read_text_content_value(item) {
                    if !text.is_empty() {
                        parts.push(text);
                    }
                }
            }

            if parts.is_empty() {
                None
            } else {
                Some(parts.join(""))
            }
        }
        Value::Object(map) => {
            if let Some(text) = map.get("text").and_then(Value::as_str) {
                if !text.is_empty() {
                    return Some(text.to_string());
                }
            }

            if let Some(value) = map.get("value").and_then(Value::as_str) {
                if !value.is_empty() {
                    return Some(value.to_string());
                }
            }

            if let Some(nested) = map.get("content") {
                if let Some(text) = read_text_content_value(nested) {
                    if !text.is_empty() {
                        return Some(text);
                    }
                }
            }

            None
        }
        _ => None,
    }
}

fn temperature_capability_key(settings: &FrontendSettings) -> String {
    let provider = settings.provider.trim().to_ascii_lowercase();
    let api_url = settings
        .api_url
        .trim()
        .trim_end_matches('/')
        .to_ascii_lowercase();
    let model = settings.model.trim().to_ascii_lowercase();
    format!("{}|{}|{}", provider, api_url, model)
}

#[derive(Debug)]
struct LlmCapabilityUpdate {
    key: String,
    capability: String,
    supported: bool,
}

fn parse_llm_capability_update_log(message: &str) -> Option<LlmCapabilityUpdate> {
    let prefix = "LLM capability update: ";
    if !message.starts_with(prefix) {
        return None;
    }

    let mut key = String::new();
    let mut capability = String::new();
    let mut supported: Option<bool> = None;
    for part in message[prefix.len()..].split(';') {
        let part = part.trim();
        if let Some(value) = part.strip_prefix("key=") {
            key = value.trim().to_string();
        } else if let Some(value) = part.strip_prefix("capability=") {
            capability = value.trim().to_string();
        } else if let Some(value) = part.strip_prefix("supported=") {
            let normalized = value.trim().to_ascii_lowercase();
            if normalized == "true" {
                supported = Some(true);
            } else if normalized == "false" {
                supported = Some(false);
            }
        }
    }

    if key.is_empty() || capability.is_empty() {
        return None;
    }

    Some(LlmCapabilityUpdate {
        key,
        capability,
        supported: supported?,
    })
}

fn extract_api_error_message(body: &str) -> String {
    if body.trim().is_empty() {
        return "no response body".to_string();
    }

    if let Ok(value) = serde_json::from_str::<Value>(body) {
        if let Some(error) = value.get("error") {
            if let Some(msg) = error.get("message").and_then(|m| m.as_str()) {
                return msg.to_string();
            }
            if let Some(msg) = error.as_str() {
                return msg.to_string();
            }
        }

        if let Some(msg) = value.get("message").and_then(|m| m.as_str()) {
            return msg.to_string();
        }
    }

    let one_line = body.lines().next().unwrap_or_default().trim();
    if one_line.is_empty() {
        "unexpected error".to_string()
    } else {
        one_line.to_string()
    }
}

#[tauri::command]
async fn cancel_docx(state: State<'_, CancellationState>) -> Result<(), String> {
    let guard = state.docx.lock().await;
    if let Some(flag) = guard.as_ref() {
        flag.store(true, std::sync::atomic::Ordering::Relaxed);
    }
    Ok(())
}

#[tauri::command]
async fn correct_docx(
    app: AppHandle,
    state: State<'_, CancellationState>,
    capability_state: State<'_, ModelCapabilityState>,
    file_path: String,
    original_path: Option<String>,
    accept_existing_track_changes: Option<bool>,
    settings: FrontendSettings,
) -> Result<(), String> {
    let (input_path, input_kind) =
        normalize_office_input_path(&file_path).map_err(|e| e.to_string())?;
    let input_path_str = input_path.to_string_lossy().to_string();
    let original_normalized = match original_path.as_deref() {
        Some(value) if !value.trim().is_empty() => Some(
            normalize_office_input_path(value)
                .map_err(|e| e.to_string())?
                .0
                .to_string_lossy()
                .to_string(),
        ),
        _ => None,
    };

    let backend_input_path = if input_kind == OfficeInputKind::Odt {
        let temp_root = temp_root_from_settings(&settings);
        let converted = convert_office_file_to(
            &input_path,
            OfficeInputKind::Docx,
            "ODT input conversion failed",
            &temp_root,
        )
        .await
        .map_err(|e| e.to_string())?;
        converted
    } else {
        input_path.clone()
    };
    let backend_input_path_str = backend_input_path.to_string_lossy().to_string();

    let mut effective_settings = settings;
    effective_settings.api_key = resolve_provider_key(&effective_settings);

    let cancel_flag = Arc::new(std::sync::atomic::AtomicBool::new(false));
    {
        let mut guard = state.docx.lock().await;
        if guard.is_some() {
            return Err("A DOCX correction job is already running".to_string());
        }
        *guard = Some(cancel_flag.clone());
    }

    let _ = app.emit("docx_started", frontend_path(&input_path_str));

    let result = run_docx_processor(
        &app,
        &cancel_flag,
        capability_state.inner(),
        &backend_input_path_str,
        &input_path_str,
        input_kind,
        original_normalized.as_deref(),
        accept_existing_track_changes.unwrap_or(false),
        &effective_settings,
    )
    .await;

    {
        let mut guard = state.docx.lock().await;
        *guard = None;
    }

    result.map_err(|e| e.to_string())
}

async fn run_docx_processor(
    app: &AppHandle,
    cancel_flag: &Arc<std::sync::atomic::AtomicBool>,
    capability_state: &ModelCapabilityState,
    backend_input_path: &str,
    source_input_path: &str,
    source_kind: OfficeInputKind,
    original_path: Option<&str>,
    accept_existing_track_changes: bool,
    settings: &FrontendSettings,
) -> anyhow::Result<()> {
    const DOCX_PROCESS_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(3 * 60 * 60);
    let temp_root = temp_root_from_settings(settings);
    tokio::fs::create_dir_all(&temp_root).await?;

    write_debug_event(
        app,
        "docx.run.start",
        json!({
            "provider": settings.provider.as_str(),
            "model": settings.model.as_str(),
            "api_url": settings.api_url.as_str(),
            "input_path": backend_input_path,
            "source_path": source_input_path,
            "source_kind": if source_kind == OfficeInputKind::Odt { "odt" } else { "docx" },
            "compare_mode": settings.docx.compare_mode.as_str(),
            "chunk_size": settings.docx.chunk_size,
            "batching": settings.docx.enable_batching,
            "correction_scope_parts": &settings.docx.correction_scope_parts,
            "batch_max_chars": settings.docx.batch_max_chars,
            "batch_max_paragraphs": settings.docx.batch_max_paragraphs,
            "parallelization": settings.docx.enable_parallelization,
            "max_parallel_requests": settings.docx.max_parallel_requests,
            "enable_reasoning": settings.enable_reasoning,
            "reasoning_effort": &settings.reasoning_effort
        }),
    );

    let mut backend_settings = settings.clone();
    let env_api_key = backend_settings
        .api_key
        .clone()
        .filter(|value| !value.trim().is_empty());
    if env_api_key.is_some() {
        backend_settings.api_key = Some(format!("ENV:{BACKEND_API_KEY_ENV}"));
    }
    for value in backend_settings.provider_keys.values_mut() {
        *value = None;
    }

    let capability_key = temperature_capability_key(settings);
    let temperature_hint = {
        let cache = capability_state.temperature_support.lock().await;
        cache.get(&capability_key).copied()
    };
    let reasoning_hint = {
        let cache = capability_state.reasoning_effort_support.lock().await;
        cache.get(&capability_key).copied()
    };

    write_debug_event(
        app,
        "docx.run.capability_hint",
        json!({
            "key": capability_key,
            "temperature_supported": temperature_hint,
            "reasoning_effort_supported": reasoning_hint
        }),
    );

    let mut settings_value = serde_json::to_value(&backend_settings)?;
    if let Some(object) = settings_value.as_object_mut() {
        let mut hint_obj = serde_json::Map::new();
        if let Some(value) = temperature_hint {
            hint_obj.insert("temperature_supported".to_string(), json!(value));
        }
        if let Some(value) = reasoning_hint {
            hint_obj.insert("reasoning_effort_supported".to_string(), json!(value));
        }
        if !hint_obj.is_empty() {
            object.insert("llm_capability_hint".to_string(), Value::Object(hint_obj));
        }
    }

    let settings_json = serde_json::to_string(&settings_value)?;
    let settings_temp = temp_root.join(format!("lingofix-settings-{}.json", uuid_like()));
    tokio::fs::write(&settings_temp, settings_json).await?;
    let source_kind_arg = if source_kind == OfficeInputKind::Odt {
        "odt"
    } else {
        "docx"
    };
    let source_original_path_arg = if source_kind == OfficeInputKind::Odt {
        original_path
            .filter(|value| !value.trim().is_empty())
            .unwrap_or(source_input_path)
    } else {
        source_input_path
    };
    let run_result = async {
        let (mut cmd, launch_mode) = if let Some(backend_bin) = resolve_bundled_backend_executable(app) {
            let mut command = Command::new(&backend_bin);
            command
                .arg("--input")
                .arg(backend_input_path)
                .arg("--settings-path")
                .arg(&settings_temp)
                .arg("--source-kind")
                .arg(source_kind_arg)
                .arg("--source-original-path")
                .arg(source_original_path_arg);
            if accept_existing_track_changes {
                command.arg("--accept-existing-track-changes");
            }
            if let Some(parent) = backend_bin.parent() {
                command.current_dir(parent);
            }
            (
                command,
                format!("bundled backend executable ({})", backend_bin.display()),
            )
        } else {
            let project = workspace_root()?.join("backend").join("Lingofix.Backend.csproj");
            let dotnet = resolve_dotnet_path();
            let dotnet_cmd = dotnet
                .clone()
                .unwrap_or_else(|| PathBuf::from("dotnet"));
            let launch = if let Some(path) = dotnet {
                format!("dotnet run via {}", path.display())
            } else {
                "dotnet run via PATH".to_string()
            };

            let mut command = Command::new(dotnet_cmd);
            command
                .arg("run")
                .arg("--project")
                .arg(project)
                .arg("--")
                .arg("--input")
                .arg(backend_input_path)
                .arg("--settings-path")
                .arg(&settings_temp)
                .arg("--source-kind")
                .arg(source_kind_arg)
                .arg("--source-original-path")
                .arg(source_original_path_arg);
            if accept_existing_track_changes {
                command.arg("--accept-existing-track-changes");
            }
            (command, launch)
        };

        if let Some(api_key) = env_api_key.as_ref() {
            cmd.env(BACKEND_API_KEY_ENV, api_key);
        }
        cmd.env(
            BACKEND_TEMP_DIR_ENV,
            temp_root.to_string_lossy().to_string(),
        );
        cmd.stdout(Stdio::piped())
            .stderr(Stdio::piped());

        #[cfg(target_os = "windows")]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x08000000;
            cmd.as_std_mut().creation_flags(CREATE_NO_WINDOW);
        }

        let mut child = cmd
            .spawn()
            .with_context(|| format!("failed to start DOCX processor ({launch_mode})"))?;
        let stdout = child.stdout.take().context("missing DOCX processor stdout")?;
        let stderr = child.stderr.take().context("missing DOCX processor stderr")?;
        let mut reader = BufReader::new(stdout).lines();
        let stderr_task = tokio::spawn(async move {
            let mut stderr_reader = BufReader::new(stderr);
            let mut buffer = String::new();
            let _ = stderr_reader.read_to_string(&mut buffer).await;
            buffer
        });

        let mut output_path: Option<String> = None;
        let mut track_changes = false;
        let mut backend_error: Option<String> = None;
        let mut cancelled = false;
        let started_at = std::time::Instant::now();

        loop {
            if cancel_flag.load(std::sync::atomic::Ordering::Relaxed) {
                cancelled = true;
                let _ = child.kill().await;
                break;
            }

            if started_at.elapsed() > DOCX_PROCESS_TIMEOUT {
                backend_error = Some("DOCX processor timed out".to_string());
                let _ = child.kill().await;
                break;
            }

            match tokio::time::timeout(std::time::Duration::from_millis(100), reader.next_line()).await {
                Ok(Ok(Some(line))) => {
                    if let Ok(value) = serde_json::from_str::<Value>(&line) {
                        match value.get("type").and_then(|v| v.as_str()).unwrap_or_default() {
                            "progress" => {
                                let percent = value.get("percent").and_then(|v| v.as_i64()).unwrap_or(0);
                                let message = value
                                    .get("message")
                                    .and_then(|v| v.as_str())
                                    .unwrap_or_default()
                                    .to_string();
                                let _ = app.emit("docx_progress", json!({ "percent": percent, "message": message }));
                                write_debug_event(
                                    app,
                                    "docx.run.progress",
                                    json!({
                                        "percent": percent,
                                        "message": truncate_debug_message(&message, 800),
                                        "elapsed_ms": started_at.elapsed().as_millis()
                                    }),
                                );
                            }
                            "log" => {
                                let level = value.get("level").and_then(|v| v.as_str()).unwrap_or("info");
                                let message = value.get("message").and_then(|v| v.as_str()).unwrap_or_default();
                                if let Some(update) = parse_llm_capability_update_log(message) {
                                    if update.key == capability_key {
                                        if update.capability == "temperature" {
                                            let mut cache = capability_state.temperature_support.lock().await;
                                            cache.insert(capability_key.clone(), update.supported);
                                        } else if update.capability == "reasoning_effort" {
                                            let mut cache = capability_state.reasoning_effort_support.lock().await;
                                            cache.insert(capability_key.clone(), update.supported);
                                        }

                                        write_debug_event(
                                            app,
                                            "docx.run.capability_update",
                                            json!({
                                                "key": update.key,
                                                "capability": update.capability,
                                                "supported": update.supported
                                            }),
                                        );
                                    }
                                }
                                let _ = app.emit("docx_log", json!({ "level": level, "message": message }));
                                write_debug_event(
                                    app,
                                    "docx.run.log",
                                    json!({
                                        "level": level,
                                        "message": truncate_debug_message(message, 1200),
                                        "elapsed_ms": started_at.elapsed().as_millis()
                                    }),
                                );
                            }
                            "result" => {
                                output_path = value.get("outputPath").and_then(|v| v.as_str()).map(|s| s.to_string());
                                track_changes = value
                                    .get("trackChangesCreated")
                                    .and_then(|v| v.as_bool())
                                    .unwrap_or(false);
                            }
                            "error" => {
                                let message = value.get("message").and_then(|v| v.as_str()).unwrap_or("DOCX failed");
                                backend_error = Some(message.to_string());
                                write_debug_event(
                                    app,
                                    "docx.run.backend_error",
                                    json!({
                                        "error": truncate_debug_message(message, 1200),
                                        "elapsed_ms": started_at.elapsed().as_millis()
                                    }),
                                );
                            }
                            _ => {}
                        }
                    }
                }
                Ok(Ok(None)) => break,
                Ok(Err(e)) => {
                    backend_error = Some(e.to_string());
                    break;
                }
                Err(_) => {}
            }
        }

        let status = child.wait().await?;
        let stderr_output = match stderr_task.await {
            Ok(text) => text,
            Err(_) => String::new(),
        };

        if cancelled {
            write_debug_event(
                app,
                "docx.run.cancelled",
                json!({
                    "elapsed_ms": started_at.elapsed().as_millis()
                }),
            );
            return Err(anyhow!("DOCX processing cancelled"));
        }

        if let Some(message) = backend_error {
            let combined = combine_backend_error(&message, &stderr_output);
            write_debug_event(
                app,
                "docx.run.error",
                json!({
                    "error": truncate_debug_message(&combined, 1500),
                    "elapsed_ms": started_at.elapsed().as_millis()
                }),
            );
            let _ = app.emit("docx_error", combined.clone());
            return Err(anyhow!(combined));
        }

        if !status.success() {
            let message = combine_backend_error("DOCX processor failed", &stderr_output);
            write_debug_event(
                app,
                "docx.run.error",
                json!({
                    "error": truncate_debug_message(&message, 1500),
                    "elapsed_ms": started_at.elapsed().as_millis(),
                    "exit_status": status.code()
                }),
            );
            let _ = app.emit("docx_error", message.clone());
            return Err(anyhow!(message));
        }

        let mut final_output = output_path.ok_or_else(|| anyhow!("No output path from DOCX processor"))?;

        if source_kind == OfficeInputKind::Odt {
            let is_openxml_mode = settings.docx.compare_mode.eq_ignore_ascii_case("openxml");
            let output_suffix = Path::new(&final_output)
                .file_stem()
                .and_then(|s| s.to_str())
                .map(|stem| {
                    if stem.ends_with("_corrected") {
                        "_corrected"
                    } else {
                        "_lingofix"
                    }
                })
                .unwrap_or("_lingofix");
            let naming_source = original_path.unwrap_or(source_input_path);
            if is_openxml_mode {
                let docx_target = build_output_path(Path::new(naming_source), output_suffix, OfficeInputKind::Docx)?;
                tokio::fs::copy(&final_output, &docx_target).await?;
                final_output = docx_target.to_string_lossy().to_string();
                let _ = app.emit(
                    "docx_log",
                    json!({
                        "level": "warning",
                        "message": "ODT re-conversion skipped for OpenXML mode; returning DOCX output for validation."
                    }),
                );
            } else {
                let target = build_output_path(Path::new(naming_source), output_suffix, OfficeInputKind::Odt)?;
                let final_output_is_odt = office_input_kind(Path::new(&final_output)) == Some(OfficeInputKind::Odt);
                if final_output_is_odt {
                    tokio::fs::copy(&final_output, &target).await?;
                    final_output = target.to_string_lossy().to_string();
                } else {
                    match convert_docx_to_odt(Path::new(&final_output), &target, &temp_root).await {
                        Ok(()) => {
                            final_output = target.to_string_lossy().to_string();
                        }
                        Err(conversion_error) => {
                            let fallback_docx_target = build_output_path(
                                Path::new(naming_source),
                                output_suffix,
                                OfficeInputKind::Docx,
                            )?;
                            tokio::fs::copy(&final_output, &fallback_docx_target).await?;
                            final_output = fallback_docx_target.to_string_lossy().to_string();
                            let _ = app.emit(
                                "docx_log",
                                json!({
                                    "level": "warning",
                                    "message": format!(
                                        "ODT re-conversion failed; returning DOCX fallback: {}",
                                        conversion_error
                                    )
                                }),
                            );
                        }
                    }
                }
            }
        } else if let Some(original) = original_path {
            let input = PathBuf::from(source_input_path);
            let temp_base = temp_root.join("uploads");
            if input.starts_with(temp_base) && Path::new(original).exists() {
                let output_suffix = Path::new(&final_output)
                    .file_stem()
                    .and_then(|s| s.to_str())
                    .map(|stem| if stem.ends_with("_corrected") { "_corrected" } else { "_lingofix" })
                    .unwrap_or("_lingofix");
                let target = build_output_path(Path::new(original), output_suffix, OfficeInputKind::Docx)?;
                tokio::fs::copy(&final_output, &target).await?;
                final_output = target.to_string_lossy().to_string();
            }
        }

        let _ = app.emit(
            "docx_complete",
            json!({
                "outputPath": frontend_path(&final_output),
                "trackChanges": track_changes
            }),
        );

        write_debug_event(
            app,
            "docx.run.success",
            json!({
                "output_path": final_output.as_str(),
                "track_changes": track_changes,
                "elapsed_ms": started_at.elapsed().as_millis()
            }),
        );

        Ok(())
    }
    .await;

    run_result
}

#[tauri::command]
async fn inspect_docx_track_changes(
    app: AppHandle,
    file_path: String,
) -> Result<DocxTrackChangesInspection, String> {
    let (input_path, input_kind) =
        normalize_office_input_path(&file_path).map_err(|e| e.to_string())?;
    let temp_root = resolve_temp_root_from_settings(&app);
    let backend_input_path = if input_kind == OfficeInputKind::Odt {
        let converted = convert_office_file_to(
            &input_path,
            OfficeInputKind::Docx,
            "ODT input conversion for track-changes inspection failed",
            &temp_root,
        )
        .await
        .map_err(|e| e.to_string())?;
        converted
    } else {
        input_path
    };

    let input_path_str = backend_input_path.to_string_lossy().to_string();
    let has_track_changes = inspect_docx_track_changes_via_backend(&app, &input_path_str)
        .await
        .map_err(|e| e.to_string())?;

    Ok(DocxTrackChangesInspection { has_track_changes })
}

async fn inspect_docx_track_changes_via_backend(
    app: &AppHandle,
    file_path: &str,
) -> anyhow::Result<bool> {
    const INSPECT_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(120);

    let (mut cmd, launch_mode) = if let Some(backend_bin) = resolve_bundled_backend_executable(app)
    {
        let mut command = Command::new(&backend_bin);
        command
            .arg("--input")
            .arg(file_path)
            .arg("--inspect-track-changes");
        if let Some(parent) = backend_bin.parent() {
            command.current_dir(parent);
        }
        (
            command,
            format!("bundled backend executable ({})", backend_bin.display()),
        )
    } else {
        let project = workspace_root()?
            .join("backend")
            .join("Lingofix.Backend.csproj");
        let dotnet = resolve_dotnet_path();
        let dotnet_cmd = dotnet.clone().unwrap_or_else(|| PathBuf::from("dotnet"));
        let launch = if let Some(path) = dotnet {
            format!("dotnet run via {}", path.display())
        } else {
            "dotnet run via PATH".to_string()
        };

        let mut command = Command::new(dotnet_cmd);
        command
            .arg("run")
            .arg("--project")
            .arg(project)
            .arg("--")
            .arg("--input")
            .arg(file_path)
            .arg("--inspect-track-changes");
        (command, launch)
    };

    let temp_root = resolve_temp_root_from_settings(app);
    cmd.env(BACKEND_TEMP_DIR_ENV, temp_root.to_string_lossy().to_string());
    cmd.stdout(Stdio::piped()).stderr(Stdio::piped());

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        cmd.as_std_mut().creation_flags(CREATE_NO_WINDOW);
    }

    let output = tokio::time::timeout(INSPECT_TIMEOUT, cmd.output())
        .await
        .map_err(|_| anyhow!("DOCX inspection timed out"))?
        .with_context(|| format!("failed to start DOCX inspector ({launch_mode})"))?;

    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    let stderr = String::from_utf8_lossy(&output.stderr).to_string();

    if !output.status.success() {
        let message = combine_backend_error("DOCX track-changes inspection failed", &stderr);
        return Err(anyhow!(message));
    }

    for line in stdout.lines().rev() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }

        if let Ok(value) = serde_json::from_str::<Value>(trimmed) {
            if value
                .get("type")
                .and_then(|v| v.as_str())
                .unwrap_or_default()
                == "track_changes_inspection"
            {
                let has_track_changes = value
                    .get("hasTrackChanges")
                    .and_then(|v| v.as_bool())
                    .unwrap_or(false);
                return Ok(has_track_changes);
            }
        }
    }

    Err(anyhow!("DOCX inspector returned no track-changes result"))
}

fn resolve_bundled_backend_executable(app: &AppHandle) -> Option<PathBuf> {
    let resource_dir = app.path().resource_dir().ok()?;
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(Path::to_path_buf));
    let sidecar_name = format!("{BACKEND_EXECUTABLE_BASE}-{}", current_sidecar_triple());
    let base_name = backend_executable_filename();

    let mut candidates = vec![
        resource_dir.join("binaries").join(&sidecar_name),
        resource_dir.join("binaries").join(&base_name),
        resource_dir.join(&sidecar_name),
        resource_dir.join(&base_name),
        resource_dir
            .join("Resources")
            .join("binaries")
            .join(&sidecar_name),
        resource_dir
            .join("Resources")
            .join("binaries")
            .join(&base_name),
    ];

    if let Some(exe_dir) = exe_dir {
        candidates.push(exe_dir.join(&base_name));
        candidates.push(exe_dir.join(&sidecar_name));
        candidates.push(exe_dir.join("binaries").join(&base_name));
        candidates.push(exe_dir.join("binaries").join(&sidecar_name));
    }

    let resolved = candidates.into_iter().find(|path| path.is_file());

    if let Some(path) = resolved.as_ref() {
        ensure_executable_permissions(path);
    }

    resolved
}

fn ensure_executable_permissions(path: &Path) {
    #[cfg(unix)]
    {
        let Ok(metadata) = std::fs::metadata(path) else {
            return;
        };

        let mut permissions = metadata.permissions();
        let mode = permissions.mode();
        let desired_mode = mode | 0o111;
        if desired_mode == mode {
            return;
        }

        permissions.set_mode(desired_mode);
        let _ = std::fs::set_permissions(path, permissions);
    }

    #[cfg(not(unix))]
    {
        let _ = path;
    }
}

fn resolve_dotnet_path() -> Option<PathBuf> {
    let from_env = std::env::var(BACKEND_DOTNET_PATH_ENV)
        .ok()
        .map(|v| PathBuf::from(v.trim()))
        .filter(|v| !v.as_os_str().is_empty())
        .filter(|v| v.is_file());
    if from_env.is_some() {
        return from_env;
    }

    let candidates = [
        PathBuf::from("/opt/homebrew/bin/dotnet"),
        PathBuf::from("/usr/local/share/dotnet/dotnet"),
        PathBuf::from("/usr/local/bin/dotnet"),
        PathBuf::from("C:/Program Files/dotnet/dotnet.exe"),
    ];

    candidates.into_iter().find(|path| path.is_file())
}

fn resolve_soffice_candidates() -> Vec<PathBuf> {
    fn add_soffice_candidate(candidates: &mut Vec<PathBuf>, candidate: PathBuf) {
        if cfg!(target_os = "windows") {
            let filename = candidate
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or_default();

            if filename.eq_ignore_ascii_case("soffice.exe") {
                let mut com_variant = candidate.clone();
                com_variant.set_file_name("soffice.com");
                candidates.push(com_variant);
            }
        }

        candidates.push(candidate);
    }

    let mut candidates = Vec::new();

    if let Some(from_env) = std::env::var(SOFFICE_PATH_ENV)
        .ok()
        .map(|v| PathBuf::from(v.trim()))
        .filter(|v| !v.as_os_str().is_empty())
    {
        add_soffice_candidate(&mut candidates, from_env);
    }

    if cfg!(target_os = "macos") {
        add_soffice_candidate(
            &mut candidates,
            PathBuf::from("/Applications/LibreOffice.app/Contents/MacOS/soffice"),
        );
    }

    if cfg!(target_os = "windows") {
        add_soffice_candidate(
            &mut candidates,
            PathBuf::from("C:/Program Files/LibreOffice/program/soffice.exe"),
        );
        add_soffice_candidate(
            &mut candidates,
            PathBuf::from("C:/Program Files (x86)/LibreOffice/program/soffice.exe"),
        );
        add_soffice_candidate(&mut candidates, PathBuf::from("soffice.exe"));
    } else {
        if std::env::var_os("FLATPAK_ID").is_some() {
            add_soffice_candidate(&mut candidates, PathBuf::from("/run/host/usr/bin/soffice"));
            add_soffice_candidate(&mut candidates, PathBuf::from("/run/host/usr/local/bin/soffice"));
        }
        add_soffice_candidate(&mut candidates, PathBuf::from("/usr/bin/soffice"));
        add_soffice_candidate(&mut candidates, PathBuf::from("/usr/local/bin/soffice"));
        add_soffice_candidate(&mut candidates, PathBuf::from("soffice"));
    }

    candidates
}

async fn convert_office_file_to(
    input_path: &Path,
    target_kind: OfficeInputKind,
    error_prefix: &str,
    temp_root: &Path,
) -> anyhow::Result<PathBuf> {
    const SOFFICE_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(180);

    let input_kind =
        office_input_kind(input_path).ok_or_else(|| anyhow!("unsupported input file extension"))?;
    if input_kind == target_kind {
        return Ok(input_path.to_path_buf());
    }

    let stem = input_path
        .file_stem()
        .and_then(|s| s.to_str())
        .ok_or_else(|| anyhow!("invalid input filename"))?;

    let conversion_dir = temp_root
        .join("conversions")
        .join(uuid_like());
    tokio::fs::create_dir_all(&conversion_dir).await?;

    let staged_input = conversion_dir.join(format!("source.{}", input_kind.extension()));
    tokio::fs::copy(input_path, &staged_input).await?;

    let candidates = resolve_soffice_candidates();
    let total_candidates = candidates.len();
    let mut not_found_failures = 0usize;
    let mut failures = Vec::new();
    for candidate in candidates {
        let mut cmd = Command::new(&candidate);
        cmd.arg("--headless")
            .arg("--convert-to")
            .arg(target_kind.extension())
            .arg("--outdir")
            .arg(&conversion_dir)
            .arg(&staged_input)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        #[cfg(target_os = "windows")]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x08000000;
            cmd.as_std_mut().creation_flags(CREATE_NO_WINDOW);
        }

        let output = match tokio::time::timeout(SOFFICE_TIMEOUT, cmd.output()).await {
            Ok(Ok(result)) => result,
            Ok(Err(err)) => {
                if err.kind() == std::io::ErrorKind::NotFound {
                    not_found_failures += 1;
                }
                failures.push(format!("{}: {}", candidate.display(), err));
                continue;
            }
            Err(_) => {
                failures.push(format!("{}: timed out", candidate.display()));
                continue;
            }
        };

        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let converted_staged = conversion_dir.join(format!("source.{}", target_kind.extension()));

        if output.status.success() && converted_staged.exists() {
            let converted_output =
                conversion_dir.join(format!("{stem}.{}", target_kind.extension()));
            tokio::fs::copy(&converted_staged, &converted_output).await?;
            return Ok(converted_output);
        }

        let mut reason = format!("{}: exit status {}", candidate.display(), output.status);
        if !stderr.is_empty() {
            reason.push_str(&format!("; stderr: {stderr}"));
        }
        if !stdout.is_empty() {
            reason.push_str(&format!("; stdout: {stdout}"));
        }
        failures.push(reason);
    }

    if total_candidates > 0 && not_found_failures == total_candidates {
        return Err(anyhow!(
            "{error_prefix}.\nLibreOffice ist nicht installiert oder `soffice` wurde nicht gefunden.\nBitte installieren Sie zuerst LibreOffice:\n{LIBREOFFICE_DOWNLOAD_URL}\n\nOptional: setze `{SOFFICE_PATH_ENV}` auf den absoluten `soffice`-Pfad."
        ));
    }

    let details = failures.join("\n");
    Err(anyhow!(
        "{error_prefix}. LibreOffice (soffice) failed to convert this file.\nInstall LibreOffice if needed: {LIBREOFFICE_DOWNLOAD_URL}\n\nDetails:\n{details}"
    ))
}

async fn convert_docx_to_odt(input_docx: &Path, output_odt: &Path, temp_root: &Path) -> anyhow::Result<()> {
    std::fs::create_dir_all(temp_root)?;
    let converted = convert_office_file_to(
        input_docx,
        OfficeInputKind::Odt,
        "ODT output conversion failed",
        temp_root,
    )
    .await?;
    if let Some(parent) = output_odt.parent() {
        tokio::fs::create_dir_all(parent).await?;
    }
    tokio::fs::copy(&converted, output_odt).await?;
    Ok(())
}

#[cfg(target_os = "windows")]
fn backend_executable_filename() -> String {
    format!("{BACKEND_EXECUTABLE_BASE}.exe")
}

#[cfg(not(target_os = "windows"))]
fn backend_executable_filename() -> String {
    BACKEND_EXECUTABLE_BASE.to_string()
}

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
fn current_sidecar_triple() -> &'static str {
    "aarch64-apple-darwin"
}

#[cfg(all(target_os = "macos", target_arch = "x86_64"))]
fn current_sidecar_triple() -> &'static str {
    "x86_64-apple-darwin"
}

#[cfg(all(target_os = "windows", target_arch = "x86_64"))]
fn current_sidecar_triple() -> &'static str {
    "x86_64-pc-windows-msvc"
}

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn current_sidecar_triple() -> &'static str {
    "x86_64-unknown-linux-gnu"
}

#[cfg(not(any(
    all(target_os = "macos", target_arch = "aarch64"),
    all(target_os = "macos", target_arch = "x86_64"),
    all(target_os = "windows", target_arch = "x86_64"),
    all(target_os = "linux", target_arch = "x86_64")
)))]
fn current_sidecar_triple() -> &'static str {
    ""
}

fn combine_backend_error(message: &str, stderr: &str) -> String {
    let trimmed = stderr.trim();
    if trimmed.is_empty() {
        return message.to_string();
    }

    let lines: Vec<&str> = trimmed.lines().collect();
    let start = lines.len().saturating_sub(20);
    let tail = lines[start..].join("\n");
    format!("{message}\n{tail}")
}

#[tauri::command]
fn check_word_compare_access() -> Result<WordCompareAccessStatus, String> {
    #[cfg(target_os = "windows")]
    {
        let script = "$ErrorActionPreference='Stop'; $word=$null; try { $word = New-Object -ComObject Word.Application; $word.Visible = $false; $name = $word.Name; if ([string]::IsNullOrWhiteSpace($name)) { Write-Output 'Microsoft Word'; } else { Write-Output $name; } } finally { if ($null -ne $word) { try { $word.Quit() } catch { } [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($word) } }";

        let mut command = std::process::Command::new("powershell.exe");
        command
            .arg("-NoProfile")
            .arg("-Sta")
            .arg("-Command")
            .arg(script);

        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        command.creation_flags(CREATE_NO_WINDOW);

        let output = command
            .output()
            .map_err(|e| format!("failed to run PowerShell: {e}"))?;

        if output.status.success() {
            let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
            let details = if stdout.is_empty() {
                "Microsoft Word detected via COM automation.".to_string()
            } else {
                format!("Detected: {stdout}")
            };

            return Ok(WordCompareAccessStatus {
                ok: true,
                message: "Word compare setup is ready.".to_string(),
                details,
            });
        }

        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let details = if !stderr.is_empty() {
            stderr
        } else if !stdout.is_empty() {
            stdout
        } else {
            format!("PowerShell exited with status {}", output.status)
        };

        return Ok(WordCompareAccessStatus {
            ok: false,
            message: "Word compare access is not ready yet.".to_string(),
            details: format!(
                "Check Microsoft Word installation and COM availability.\n\n{}",
                details
            ),
        });
    }

    #[cfg(target_os = "linux")]
    {
        return Ok(WordCompareAccessStatus {
            ok: false,
            message: "Word compare access is not supported on this operating system.".to_string(),
            details: "Use LibreOffice UNO compare mode on Linux.".to_string(),
        });
    }

    #[cfg(not(any(target_os = "macos", target_os = "windows", target_os = "linux")))]
    {
        return Ok(WordCompareAccessStatus {
            ok: false,
            message: "Word compare access is not supported on this operating system.".to_string(),
            details: "Use OpenXML or LibreOffice UNO compare mode instead.".to_string(),
        });
    }

    #[cfg(target_os = "macos")]
    {
        let workspace = lingofix_temp_root().join("word-compare-probe");
        std::fs::create_dir_all(&workspace)
            .map_err(|e| format!("failed to create workspace: {e}"))?;

        let probe = workspace.join("access-probe.txt");
        std::fs::write(&probe, b"ok").map_err(|e| format!("failed to write probe file: {e}"))?;
        let read_back = std::fs::read_to_string(&probe)
            .map_err(|e| format!("failed to read probe file: {e}"))?;
        let _ = std::fs::remove_file(&probe);
        let _ = std::fs::remove_dir(&workspace);

        if read_back.trim() != "ok" {
            return Ok(WordCompareAccessStatus {
                ok: false,
                message: "Workspace probe failed.".to_string(),
                details: format!("Unexpected probe content in {}", workspace.display()),
            });
        }

        let output = std::process::Command::new("osascript")
            .arg("-e")
            .arg("tell application \"Microsoft Word\" to get name")
            .output()
            .map_err(|e| format!("failed to run osascript: {e}"))?;

        if output.status.success() {
            return Ok(WordCompareAccessStatus {
                ok: true,
                message: "Word compare setup is ready.".to_string(),
                details: format!("Workspace: {}", workspace.display()),
            });
        }

        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let mut details = if !stderr.is_empty() { stderr } else { stdout };
        if details.is_empty() {
            details = format!("osascript exited with status {}", output.status);
        }

        let permission_hint =
            if details.contains("-1743") || details.to_lowercase().contains("not authorized") {
                format!(
                    "Allow Lingofix to control Microsoft Word in {}.",
                    AUTOMATION_SETTINGS_PATH
                )
            } else {
                format!(
                    "Check Microsoft Word installation and grant access in {}.",
                    AUTOMATION_SETTINGS_PATH
                )
            };

        Ok(WordCompareAccessStatus {
            ok: false,
            message: "Word compare access is not ready yet.".to_string(),
            details: format!("{permission_hint}\n\n{details}"),
        })
    }
}

#[tauri::command]
fn check_libreoffice_compare_access() -> Result<WordCompareAccessStatus, String> {
    let candidates = resolve_soffice_candidates();
    let mut failures = Vec::new();

    for candidate in candidates {
        let mut command = std::process::Command::new(&candidate);
        command.arg("--version");

        #[cfg(target_os = "windows")]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x08000000;
            command.creation_flags(CREATE_NO_WINDOW);
        }

        match command.output() {
            Ok(output) => {
                if output.status.success() {
                    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
                    let version = if stdout.is_empty() {
                        "LibreOffice detected".to_string()
                    } else {
                        stdout
                    };

                    return Ok(WordCompareAccessStatus {
                        ok: true,
                        message: "LibreOffice compare setup is ready.".to_string(),
                        details: format!("{}\nExecutable: {}", version, candidate.display()),
                    });
                }

                let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
                let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
                let reason = if stderr.is_empty() { stdout } else { stderr };
                failures.push(if reason.is_empty() {
                    format!("{}: exited with {}", candidate.display(), output.status)
                } else {
                    format!("{}: {}", candidate.display(), reason)
                });
            }
            Err(err) => {
                failures.push(format!("{}: {}", candidate.display(), err));
            }
        }
    }

    let details = if failures.is_empty() {
        "No soffice candidates available.".to_string()
    } else {
        failures.join("\n")
    };

    Ok(WordCompareAccessStatus {
        ok: false,
        message: "LibreOffice compare access is not ready yet.".to_string(),
        details: format!(
            "Install LibreOffice and retry: {}\nOptional: set {} to the absolute soffice path.\n\n{}",
            LIBREOFFICE_DOWNLOAD_URL,
            SOFFICE_PATH_ENV,
            details
        ),
    })
}

#[tauri::command]
fn open_folder(path: String) -> Result<(), String> {
    let canonical = normalize_existing_path(&path).map_err(|e| e.to_string())?;
    let reveal_file = canonical.is_file();
    open_path_in_system_explorer(&canonical, reveal_file)
}

fn open_path_in_system_explorer(path: &Path, reveal_file: bool) -> Result<(), String> {
    if cfg!(target_os = "windows") {
        let mut command = std::process::Command::new("explorer");
        if reveal_file {
            command.arg("/select,").arg(path);
        } else {
            command.arg(path);
        }
        command.spawn().map_err(|e| e.to_string())?;
        return Ok(());
    }

    if cfg!(target_os = "macos") {
        let mut command = std::process::Command::new("open");
        if reveal_file {
            command.arg("-R");
        }
        command.arg(path).spawn().map_err(|e| e.to_string())?;
        return Ok(());
    }

    let target = if reveal_file {
        path.parent().unwrap_or(path)
    } else {
        path
    };
    std::process::Command::new("xdg-open")
        .arg(target)
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
fn open_temp_lingofix_folder() -> Result<(), String> {
    let temp_dir = lingofix_temp_root();
    std::fs::create_dir_all(&temp_dir).map_err(|e| e.to_string())?;
    open_path_in_system_explorer(&temp_dir, false)
}

#[tauri::command]
fn open_settings_json(app: AppHandle) -> Result<(), String> {
    let settings = settings_path(&app).map_err(|e| e.to_string())?;
    if !settings.exists() {
        let defaults =
            encrypt_settings_secrets(sanitize_settings_for_disk(FrontendSettings::default()));
        let content = serde_json::to_string_pretty(&defaults).map_err(|e| e.to_string())?;
        std::fs::write(&settings, content).map_err(|e| e.to_string())?;
    }

    open_path_in_system_explorer(&settings, true)
}

#[tauri::command]
fn open_debug_log(app: AppHandle) -> Result<(), String> {
    let path = debug_log_path(&app).map_err(|e| e.to_string())?;
    if !path.exists() {
        std::fs::write(&path, "").map_err(|e| e.to_string())?;
    }
    open_path_in_system_explorer(&path, true)
}

#[tauri::command]
fn open_external_url(url: String) -> Result<(), String> {
    let parsed = reqwest::Url::parse(url.trim()).map_err(|e| e.to_string())?;
    let scheme = parsed.scheme().to_ascii_lowercase();
    if scheme != "http" && scheme != "https" {
        return Err("only http and https URLs are allowed".to_string());
    }

    let target = parsed.to_string();
    if cfg!(target_os = "windows") {
        std::process::Command::new("explorer")
            .arg(target)
            .spawn()
            .map_err(|e| e.to_string())?;
    } else if cfg!(target_os = "macos") {
        std::process::Command::new("open")
            .arg(target)
            .spawn()
            .map_err(|e| e.to_string())?;
    } else {
        std::process::Command::new("xdg-open")
            .arg(target)
            .spawn()
            .map_err(|e| e.to_string())?;
    }

    Ok(())
}

#[tauri::command]
fn get_app_version(app: AppHandle) -> String {
    app.package_info().version.to_string()
}

#[tauri::command]
fn get_file_size(path: String) -> Result<u64, String> {
    let safe_path = normalize_office_input_path(&path)
        .map_err(|e| e.to_string())?
        .0;
    let meta = std::fs::metadata(safe_path).map_err(|e| e.to_string())?;
    Ok(meta.len())
}

#[tauri::command]
async fn save_temp_docx(name: String, base64: String) -> Result<String, String> {
    let bytes = decode_base64(&base64).map_err(|e| e.to_string())?;
    if bytes.is_empty() {
        return Err("file payload is empty".to_string());
    }
    if bytes.len() > MAX_OFFICE_UPLOAD_BYTES {
        return Err(format!(
            "file too large (max {} MB)",
            MAX_OFFICE_UPLOAD_BYTES / (1024 * 1024)
        ));
    }

    let safe_name = Path::new(&name)
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("document.docx");
    let lower_name = safe_name.to_ascii_lowercase();
    if !(lower_name.ends_with(".docx") || lower_name.ends_with(".odt")) {
        return Err("only .docx and .odt uploads are allowed".to_string());
    }

    let dir = lingofix_temp_root().join("uploads");
    tokio::fs::create_dir_all(&dir)
        .await
        .map_err(|e| e.to_string())?;
    let path = dir.join(format!("{}_{}", uuid_like(), safe_name));
    tokio::fs::write(&path, bytes)
        .await
        .map_err(|e| e.to_string())?;
    Ok(path.to_string_lossy().to_string())
}

fn decode_base64(value: &str) -> anyhow::Result<Vec<u8>> {
    general_purpose::STANDARD
        .decode(value)
        .context("invalid base64")
}

fn remove_markdown(text: &str) -> String {
    static RE_BOLD: OnceLock<Regex> = OnceLock::new();
    static RE_UBOLD: OnceLock<Regex> = OnceLock::new();
    static RE_ISTAR: OnceLock<Regex> = OnceLock::new();
    static RE_IUNDER: OnceLock<Regex> = OnceLock::new();
    static RE_HEADERS: OnceLock<Regex> = OnceLock::new();
    static RE_BULLETS: OnceLock<Regex> = OnceLock::new();
    static RE_LINKS: OnceLock<Regex> = OnceLock::new();
    static RE_IMAGES: OnceLock<Regex> = OnceLock::new();
    static RE_MULTI_NL: OnceLock<Regex> = OnceLock::new();

    let mut result = text.to_string();
    result = RE_BOLD
        .get_or_init(|| Regex::new(r"\*\*(.+?)\*\*").expect("regex"))
        .replace_all(&result, "$1")
        .to_string();
    result = RE_UBOLD
        .get_or_init(|| Regex::new(r"__(.+?)__").expect("regex"))
        .replace_all(&result, "$1")
        .to_string();
    result = RE_ISTAR
        .get_or_init(|| Regex::new(r"\*(.+?)\*").expect("regex"))
        .replace_all(&result, "$1")
        .to_string();
    result = RE_IUNDER
        .get_or_init(|| Regex::new(r"_(.+?)_").expect("regex"))
        .replace_all(&result, "$1")
        .to_string();
    result = result.replace("```", "").replace('`', "");
    result = RE_HEADERS
        .get_or_init(|| Regex::new(r"(?m)^#{1,3} ").expect("regex"))
        .replace_all(&result, "")
        .to_string();
    result = RE_BULLETS
        .get_or_init(|| Regex::new(r"(?m)^[-*] ").expect("regex"))
        .replace_all(&result, "")
        .to_string();
    result = RE_LINKS
        .get_or_init(|| Regex::new(r"\[([^\]]+)\]\([^)]+\)").expect("regex"))
        .replace_all(&result, "$1")
        .to_string();
    result = RE_IMAGES
        .get_or_init(|| Regex::new(r"!\[([^\]]*)\]\([^)]+\)").expect("regex"))
        .replace_all(&result, "")
        .to_string();
    result = result
        .lines()
        .map(|s| s.trim())
        .collect::<Vec<_>>()
        .join("\n");
    result = RE_MULTI_NL
        .get_or_init(|| Regex::new(r"\n{3,}").expect("regex"))
        .replace_all(&result, "\n\n")
        .to_string();
    result.trim().to_string()
}

fn build_output_path(
    input: &Path,
    suffix: &str,
    extension: OfficeInputKind,
) -> anyhow::Result<PathBuf> {
    let stem = input
        .file_stem()
        .and_then(|s| s.to_str())
        .ok_or_else(|| anyhow!("invalid input filename"))?;
    let parent = input
        .parent()
        .ok_or_else(|| anyhow!("invalid input parent"))?;
    let extension_text = extension.extension();
    let base_name = format!("{stem}{suffix}");
    let candidate = parent.join(format!("{base_name}.{extension_text}"));
    if !candidate.exists() {
        return Ok(candidate);
    }

    let mut index: u32 = 2;
    loop {
        let numbered = parent.join(format!("{base_name}-{index}.{extension_text}"));
        if !numbered.exists() {
            return Ok(numbered);
        }
        index += 1;
    }
}

fn workspace_root() -> anyhow::Result<PathBuf> {
    if let Ok(from_env) = std::env::var("LINGOFIX_WORKSPACE_ROOT") {
        let p = PathBuf::from(from_env);
        if is_workspace_root(&p) {
            return Ok(p);
        }
    }

    let manifest_parent = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| anyhow!("manifest parent not found"))?;
    if is_workspace_root(&manifest_parent) {
        return Ok(manifest_parent);
    }

    let mut candidates = Vec::new();
    if let Ok(cwd) = std::env::current_dir() {
        candidates.push(cwd);
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            candidates.push(parent.to_path_buf());
        }
    }

    for mut current in candidates {
        for _ in 0..10 {
            if is_workspace_root(&current) {
                return Ok(current);
            }
            if !current.pop() {
                break;
            }
        }
    }

    Err(anyhow!("workspace root not found"))
}

fn is_workspace_root(path: &Path) -> bool {
    path.join("frontend").is_dir() && path.join("backend").is_dir() && path.join("tauri").is_dir()
}

fn uuid_like() -> String {
    Uuid::new_v4().simple().to_string()
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_shell::init())
        .setup(|app| {
            let _ = app;
            clear_temp_lingofix_dir();
            Ok(())
        })
        .manage(CancellationState::default())
        .manage(ModelCapabilityState::default())
        .invoke_handler(tauri::generate_handler![
            load_settings,
            save_settings,
            reset_settings,
            fetch_models,
            correct_text_streaming,
            cancel_correction,
            correct_docx,
            cancel_docx,
            check_word_compare_access,
            check_libreoffice_compare_access,
            inspect_docx_track_changes,
            open_folder,
            open_temp_lingofix_folder,
            open_settings_json,
            open_debug_log,
            open_external_url,
            get_app_version,
            get_file_size,
            save_temp_docx
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
