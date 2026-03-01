import { useCallback, useState } from 'react';
import { DocxFile } from '../types';

type DocxLog = { level: string; message: string; timestamp: number };
type DocxResult = { inputName: string; outputPath: string; trackChanges: boolean };

export function useDocxState() {
  const [docxFiles, setDocxFiles] = useState<DocxFile[]>([]);
  const [activeDocxFileIndex, setActiveDocxFileIndex] = useState(0);
  const [docxProgress, setDocxProgress] = useState<{ percent: number; message: string } | null>(null);
  const [docxResults, setDocxResults] = useState<DocxResult[]>([]);
  const [docxWarning, setDocxWarning] = useState<string | null>(null);
  const [docxLogs, setDocxLogs] = useState<DocxLog[]>([]);
  const [showLogs, setShowLogs] = useState(false);
  const docxFile = docxFiles[activeDocxFileIndex] ?? null;

  const setDocxSelection = useCallback((files: DocxFile[] | null) => {
    setDocxFiles(files ?? []);
    setActiveDocxFileIndex(0);
    setDocxResults([]);
    setDocxWarning(null);
    setDocxProgress(null);
    setDocxLogs([]);
    setShowLogs(false);
  }, []);

  const resetDocxRunState = useCallback(() => {
    setDocxResults([]);
    setDocxWarning(null);
    setDocxProgress(null);
    setDocxLogs([]);
    setShowLogs(false);
  }, []);

  const appendDocxLog = useCallback((level: string, message: string) => {
    setDocxLogs((prev) => {
      const next = [...prev, { level, message, timestamp: Date.now() }];
      return next.length > 500 ? next.slice(next.length - 500) : next;
    });
  }, []);

  return {
    docxFiles,
    activeDocxFileIndex,
    setActiveDocxFileIndex,
    docxFile,
    docxProgress,
    docxResults,
    docxWarning,
    docxLogs,
    showLogs,
    setShowLogs,
    setDocxSelection,
    setDocxProgress,
    setDocxResults,
    setDocxFiles,
    setDocxWarning,
    setDocxLogs,
    appendDocxLog,
    resetDocxRunState,
  };
}
