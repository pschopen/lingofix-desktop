import { useMemo, useEffect, useState, useCallback, type KeyboardEvent } from 'react';
import { diffWordsWithSpace } from 'diff';
import { invoke, listen, open } from '../lib/bridge';
import { FileText, Upload, X } from 'lucide-react';
import { Language, t } from '../i18n';
import { DocxFile } from '../types';

interface TextEditorProps {
  text: string;
  onChange: (text: string) => void;
  onSubmitShortcut?: () => void;
  correctedText: string;
  showDiff: boolean;
  readOnly: boolean;
  isStreaming?: boolean;
  lang: Language;
  isDarkMode?: boolean;
  docxFile: DocxFile | null;
  onDocxFile: (file: DocxFile | null) => void;
  isCorrecting: boolean;
}

export function TextEditor({
  text,
  onChange,
  onSubmitShortcut,
  correctedText,
  showDiff,
  readOnly,
  isStreaming = false,
  lang,
  isDarkMode = false,
  docxFile,
  onDocxFile,
  isCorrecting,
}: TextEditorProps) {
  const [isDragging, setIsDragging] = useState(false);

  const isSupportedOfficeFile = useCallback((nameOrPath: string): boolean => {
    const lower = nameOrPath.toLowerCase();
    return lower.endsWith('.docx') || lower.endsWith('.odt');
  }, []);

  const normalizeDroppedPath = useCallback((raw: string | undefined | null): string | null => {
    if (!raw) return null;
    const trimmed = raw.trim();
    if (!trimmed) return null;

    const fromFileUri = (() => {
      const lower = trimmed.toLowerCase();
      if (!lower.startsWith('file://')) return trimmed;
      try {
        let normalized = decodeURIComponent(trimmed.replace(/^file:\/\//i, ''));
        if (/^\/[A-Za-z]:\//.test(normalized)) {
          normalized = normalized.slice(1);
        }
        return normalized.replace(/\//g, '\\');
      } catch {
        return null;
      }
    })();

    if (!fromFileUri) return null;
    if (/^[A-Za-z]:\\/.test(fromFileUri) || fromFileUri.startsWith('\\')) return fromFileUri;
    if (fromFileUri.startsWith('/')) return fromFileUri;
    return null;
  }, []);

  const extractPathFromDropEvent = useCallback((event: DragEvent): string | null => {
    const anyFile = event.dataTransfer?.files?.[0] as (File & { path?: string }) | undefined;
    const nativePath = normalizeDroppedPath(anyFile?.path);
    if (nativePath) return nativePath;

    const uriList = event.dataTransfer?.getData('text/uri-list');
    if (uriList) {
      const first = uriList.split('\n').map(s => s.trim()).find(s => s && !s.startsWith('#'));
      const uriPath = normalizeDroppedPath(first);
      if (uriPath) return uriPath;
    }

    const textPlain = event.dataTransfer?.getData('text/plain');
    const plainPath = normalizeDroppedPath(textPlain);
    if (plainPath) return plainPath;

    return null;
  }, [normalizeDroppedPath]);

  const importBrowserFile = useCallback(async (file: File) => {
    if (!isSupportedOfficeFile(file.name)) {
      return;
    }

    const nativePath = normalizeDroppedPath((file as File & { path?: string }).path);
    if (nativePath) {
      onDocxFile({ name: file.name, path: nativePath, size: file.size, originalPath: nativePath });
      return;
    }

    const buffer = await file.arrayBuffer();
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
      binary += String.fromCharCode(bytes[i]);
    }
    const base64 = btoa(binary);
    const path = await invoke<string>('save_temp_docx', {
      name: file.name,
      base64,
    });

    onDocxFile({ name: file.name, path, size: file.size });
  }, [isSupportedOfficeFile, normalizeDroppedPath, onDocxFile]);

  // Drag and drop handling with visual feedback
  useEffect(() => {
    const unlistenDrag = listen('tauri://drag', () => setIsDragging(true));
    const unlistenDragEnter = listen('tauri://drag-enter', () => setIsDragging(true));
    const unlistenDragOver = listen('tauri://drag-over', () => setIsDragging(true));
    const unlistenDrop = listen<{ paths: string[] }>('tauri://drag-drop', async (event) => {
      setIsDragging(false);
      const paths = event.payload.paths;
      if (paths && paths.length > 0) {
        const filePath = paths[0];
        if (isSupportedOfficeFile(filePath)) {
          const fileName = filePath.split(/[/\\]/).pop() || 'document.docx';
          try {
            const size = await invoke<number>('get_file_size', { path: filePath });
            onDocxFile({ name: fileName, path: filePath, size, originalPath: filePath });
          } catch (err) {
            console.error('Failed to get file size:', err);
            onDocxFile({ name: fileName, path: filePath, size: 0, originalPath: filePath });
          }
        }
      }
    });
    const unlistenLeave = listen('tauri://drag-leave', () => setIsDragging(false));
    const unlistenCancel = listen('tauri://drag-cancelled', () => setIsDragging(false));

    const handleDomDragOver = (event: DragEvent) => {
      event.preventDefault();
      setIsDragging(true);
    };

    const handleDomDragLeave = () => {
      setIsDragging(false);
    };

    const handleDomDrop = async (event: DragEvent) => {
      event.preventDefault();
      setIsDragging(false);

      const droppedPath = extractPathFromDropEvent(event);
      if (droppedPath && isSupportedOfficeFile(droppedPath)) {
        const fileName = droppedPath.split(/[/\\]/).pop() || 'document.docx';
        try {
          const size = await invoke<number>('get_file_size', { path: droppedPath });
          onDocxFile({ name: fileName, path: droppedPath, size, originalPath: droppedPath });
          return;
        } catch {
          onDocxFile({ name: fileName, path: droppedPath, size: 0, originalPath: droppedPath });
          return;
        }
      }

      const file = event.dataTransfer?.files?.[0];
      if (!file) {
        return;
      }
      try {
        await importBrowserFile(file);
      } catch (err) {
        console.error('Failed to import dropped file:', err);
      }
    };

    window.addEventListener('dragover', handleDomDragOver);
    window.addEventListener('dragleave', handleDomDragLeave);
    window.addEventListener('drop', handleDomDrop);

    return () => {
      unlistenDrag.then(fn => fn()).catch(() => {});
      unlistenDragEnter.then(fn => fn()).catch(() => {});
      unlistenDragOver.then(fn => fn()).catch(() => {});
      unlistenDrop.then(fn => fn()).catch(() => {});
      unlistenLeave.then(fn => fn()).catch(() => {});
      unlistenCancel.then(fn => fn()).catch(() => {});
      window.removeEventListener('dragover', handleDomDragOver);
      window.removeEventListener('dragleave', handleDomDragLeave);
      window.removeEventListener('drop', handleDomDrop);
    };
  }, [extractPathFromDropEvent, importBrowserFile, isSupportedOfficeFile, onDocxFile]);

  const handlePickFile = async () => {
    if (isCorrecting) return;
    try {
      const selected = await open({
        multiple: false,
        filters: [{ name: 'Word/OpenDocument', extensions: ['docx', 'odt'] }],
      });

      if (selected && typeof selected === 'string') {
        const fileName = selected.split(/[/\\]/).pop() || 'document.docx';
        try {
          const size = await invoke<number>('get_file_size', { path: selected });
          onDocxFile({ name: fileName, path: selected, size, originalPath: selected });
        } catch (err) {
          console.error('Failed to get file size:', err);
          onDocxFile({ name: fileName, path: selected, size: 0, originalPath: selected });
        }
      } else {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.docx,.odt';
        input.onchange = async () => {
          const file = input.files?.[0];
          if (!file) {
            return;
          }
          try {
            await importBrowserFile(file);
          } catch (error) {
            console.error('Failed to import selected file:', error);
          }
        };
        input.click();
      }
    } catch (err) {
      console.error('Failed to open file dialog:', err);
    }
  };

  const handleEditorKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (!(event.ctrlKey || event.metaKey) || event.key !== 'Enter') {
      return;
    }

    if (readOnly || isCorrecting) {
      return;
    }

    event.preventDefault();
    onSubmitShortcut?.();
  };

  const formatFileSize = (bytes: number): string => {
    if (!bytes) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  };

  const diffHtml = useMemo(() => {
    if (!showDiff || !correctedText) return null;

    let displayText = text;
    const displayCorrectedText = correctedText;
    
    if (isStreaming) {
      const lastSentenceMatch = text.match(/.*[.!?](?:\s|$)/s);
      if (lastSentenceMatch) {
        const endPos = lastSentenceMatch[0].length;
        displayText = text.slice(0, endPos);
      }
    }

    const changes = diffWordsWithSpace(displayText, displayCorrectedText);

    let html = '';
    for (let i = 0; i < changes.length; i++) {
      const change = changes[i];
      const val = change.value;
      const m = val.match(/^(\s*)([\s\S]*?)(\s*)$/);
      const leading = m ? m[1] : '';
      const core = m ? m[2] : val;
      const trailing = m ? m[3] : '';

      if (leading) html += escapeHtml(leading);

      if (core) {
        if (change.added) {
          html += `<span class="diff-inserted">${escapeHtml(core)}</span>`;
        } else if (change.removed) {
          html += `<span class="diff-deleted">${escapeHtml(core)}</span>`;
        } else {
          html += escapeHtml(core);
        }
      }

      if (trailing) html += escapeHtml(trailing);
      
      const nextChange = changes[i + 1];
      if (nextChange && (change.added || change.removed) && (nextChange.added || nextChange.removed)) {
        html += ' ';
      }
    }
    
    return html;
  }, [text, correctedText, showDiff, isStreaming]);

  const isEmpty = !text.trim();
  const showEmptyHint = isEmpty && !docxFile && !showDiff;
  const dragContainerClass = isDragging
    ? (isDarkMode ? 'bg-accent-900/20' : 'bg-accent-50')
    : '';
  const dragOverlay = isDragging ? (
    <div className={`absolute inset-4 z-10 flex flex-col items-center justify-center pointer-events-none rounded-xl ${
      isDarkMode 
        ? 'bg-accent-900/30 border-2 border-dashed border-accent-500' 
        : 'bg-accent-50 border-2 border-dashed border-accent-400'
    }`}>
      <div className={`w-16 h-16 rounded-2xl flex items-center justify-center mb-4 ${
        isDarkMode ? 'bg-accent-800' : 'bg-accent-100'
      }`}>
        <Upload className={`w-8 h-8 ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`} />
      </div>
      <p className={`text-lg font-medium ${isDarkMode ? 'text-accent-300' : 'text-accent-700'}`}>
        {t('editor.dropDocx', lang)}
      </p>
    </div>
  ) : null;
  // Diff view - no border, flush with header
  if (showDiff && diffHtml) {
    return (
      <div className={`relative w-full h-full transition-colors duration-200 ${dragContainerClass}`} style={{ padding: 0, margin: 0 }}>
        {dragOverlay}
        <div
          className={`w-full h-full font-sans text-base leading-relaxed
                     overflow-y-auto whitespace-pre-wrap px-5 py-4
                     transition-colors duration-200 ${
                        isDarkMode 
                          ? 'bg-surface-800 text-surface-100' 
                          : 'bg-white text-surface-800'
                      }`}
          style={{ margin: 0 }}
          dangerouslySetInnerHTML={{ __html: diffHtml }}
        />
      </div>
    );
  }

  // DOCX file loaded view - no border
  if (docxFile) {
    return (
      <div className={`relative w-full h-full transition-colors duration-200 ${dragContainerClass}`} style={{ padding: 0, margin: 0 }}>
        {dragOverlay}
        <div
          className={`w-full h-full flex items-start justify-center px-4 py-4 sm:px-6
                     transition-colors duration-200 ${
                        isDarkMode 
                          ? 'bg-surface-800' 
                          : 'bg-white'
                      }`}
          style={{ margin: 0 }}
        >
          <div className={`flex items-center gap-4 rounded-2xl px-6 py-4 max-w-md w-full shadow-premium-md animate-scale-in ${
            isDarkMode
              ? 'bg-surface-700 border border-surface-600'
              : 'bg-accent-50/60 border border-accent-200/60'
          }`}>
            <div className={`flex-shrink-0 w-12 h-12 rounded-xl flex items-center justify-center ${
              isDarkMode ? 'bg-surface-600' : 'bg-accent-100'
            }`}>
              <FileText className={`w-6 h-6 ${isDarkMode ? 'text-accent-400' : 'text-accent-600'}`} />
            </div>
            <div className="flex-1 min-w-0">
              <p className={`text-base font-medium truncate ${isDarkMode ? 'text-surface-100' : 'text-surface-900'}`}>
                {docxFile.name}
              </p>
              {docxFile.size > 0 && (
                <p className={`text-sm mt-0.5 ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>
                  {formatFileSize(docxFile.size)}
                </p>
              )}
            </div>
            {!isCorrecting && (
              <button
                onClick={() => onDocxFile(null)}
                className={`flex-shrink-0 w-9 h-9 flex items-center justify-center rounded-lg transition-all duration-200 ${
                  isDarkMode
                    ? 'text-surface-400 hover:text-red-400 hover:bg-surface-600'
                    : 'text-surface-500 hover:text-red-500 hover:bg-red-50'
                }`}
                title={t('editor.removeFile', lang)}
              >
                <X className="w-5 h-5" />
              </button>
            )}
          </div>
        </div>
      </div>
    );
  }

  // Text editor with drag feedback
  return (
    <div 
      className={`relative w-full h-full transition-colors duration-200 ${dragContainerClass}`}
      style={{ padding: 0, margin: 0 }}
    >
      {/* Drag overlay - visual feedback when dragging */}
      {dragOverlay}
      
      <textarea
        value={text}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={handleEditorKeyDown}
        placeholder={t('editor.placeholder', lang)}
        disabled={readOnly}
        className={`w-full h-full font-sans text-base leading-relaxed
                    resize-none border-0
                    focus:outline-none focus:ring-0 focus:border-transparent
                    px-5 py-4
                    transition-none ${
                      isDarkMode
                        ? 'bg-surface-800 text-surface-100 placeholder:text-surface-600 disabled:bg-surface-900 disabled:text-surface-500'
                        : 'bg-white text-surface-800 placeholder:text-surface-400 disabled:bg-surface-50 disabled:text-surface-400'
                    }`}
        style={{ whiteSpace: 'pre-wrap', margin: 0 }}
      />
      
      {/* Empty state overlay - minimal docx drop hint */}
      {showEmptyHint && !isDragging && (
        <div 
          className={`absolute inset-x-0 bottom-8 flex justify-center pointer-events-none transition-opacity duration-300 ${
            isEmpty ? 'opacity-100' : 'opacity-0'
          }`}
        >
          <div 
            onClick={handlePickFile}
            className={`flex items-center gap-2 px-4 py-2 rounded-lg cursor-pointer pointer-events-auto transition-all duration-200 ${
              isDarkMode
                ? 'bg-surface-700/60 hover:bg-surface-700 text-surface-400 hover:text-surface-300'
                : 'bg-surface-100/80 hover:bg-surface-200/80 text-surface-500 hover:text-surface-700'
            }`}
          >
            <Upload className="w-4 h-4" />
            <span className="text-sm">
              {t('editor.browse', lang)}
            </span>
          </div>
        </div>
      )}
    </div>
  );
}

function escapeHtml(text: string): string {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}
