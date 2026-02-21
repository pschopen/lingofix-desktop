import { useCallback, useState } from 'react';
import { DocxFile } from '../types';

type DocxLog = { level: string; message: string; timestamp: number };

export function useDocxState() {
  const [docxFile, setDocxFile] = useState<DocxFile | null>(null);
  const [docxProgress, setDocxProgress] = useState<{ percent: number; message: string } | null>(null);
  const [docxResult, setDocxResult] = useState<{ outputPath: string; trackChanges: boolean } | null>(null);
  const [docxWarning, setDocxWarning] = useState<string | null>(null);
  const [docxLogs, setDocxLogs] = useState<DocxLog[]>([]);
  const [showLogs, setShowLogs] = useState(false);

  const setDocxSelection = useCallback((file: DocxFile | null) => {
    setDocxFile(file);
    setDocxResult(null);
    setDocxWarning(null);
    setDocxProgress(null);
    setDocxLogs([]);
    setShowLogs(false);
  }, []);

  const resetDocxRunState = useCallback(() => {
    setDocxResult(null);
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
  };
}
