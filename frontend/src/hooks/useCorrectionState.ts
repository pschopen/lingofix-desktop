import { useState } from 'react';

export function useCorrectionState() {
  const [text, setText] = useState('');
  const [correctedText, setCorrectedText] = useState('');
  const [isCorrecting, setIsCorrecting] = useState(false);
  const [isStreaming, setIsStreaming] = useState(false);
  const [showDiff, setShowDiff] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const clearDiff = () => {
    setShowDiff(false);
    setCorrectedText('');
  };

  const applyDiff = () => {
    setText(correctedText);
    clearDiff();
  };

  const clearAll = () => {
    setText('');
    clearDiff();
    setIsStreaming(false);
    setError(null);
  };

  return {
    text,
    setText,
    correctedText,
    setCorrectedText,
    isCorrecting,
    setIsCorrecting,
    isStreaming,
    setIsStreaming,
    showDiff,
    setShowDiff,
    error,
    setError,
    clearDiff,
    applyDiff,
    clearAll,
  };
}
