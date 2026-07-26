import { Children, isValidElement, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown } from 'lucide-react';
import { PROVIDER_SENTINEL_UNCONFIGURED } from '../../types';

/* ================================================================
   Shared low-level field components used across all settings
   sections (General/Correction/Translation).
   ================================================================ */

export function FieldGroup({
  label,
  hint,
  error,
  required,
  isDarkMode = false,
  children,
}: {
  label: string;
  hint?: string;
  error?: string;
  required?: boolean;
  isDarkMode?: boolean;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label className={`block text-base font-medium mb-1.5 ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>
        {label}
        {required && <span className="text-red-400 ml-0.5">*</span>}
      </label>
      {children}
      {hint && <p className={`mt-1 text-sm ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>{hint}</p>}
      {error && <p className="mt-1 text-sm text-red-500">{error}</p>}
    </div>
  );
}

export function SelectField({
  value,
  onChange,
  onOpen,
  menuBoundaryRef,
  children,
  className = '',
  isDarkMode = false,
}: {
  value: string;
  onChange: (value: string) => void;
  onOpen?: () => void;
  menuBoundaryRef?: React.RefObject<HTMLElement | null>;
  children: React.ReactNode;
  className?: string;
  isDarkMode?: boolean;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const [menuStyle, setMenuStyle] = useState<{ left: number; top: number; width: number; maxHeight: number }>({
    left: 0,
    top: 0,
    width: 0,
    maxHeight: 240,
  });

  const options = useMemo(() => {
    return Children.toArray(children)
      .map((child) => {
        if (!isValidElement(child)) {
          return null;
        }

        const props = child.props as { value?: string; children?: React.ReactNode };
        if (typeof props.value === 'undefined') {
          return null;
        }

        const label = typeof props.children === 'string' || typeof props.children === 'number'
          ? String(props.children)
          : String(props.value);

        return {
          value: String(props.value),
          label,
        };
      })
      .filter((entry): entry is { value: string; label: string } => entry !== null);
  }, [children]);

  const selected = options.find((option) => option.value === value);

  const recalculateMenuPosition = () => {
    const trigger = containerRef.current;
    if (!trigger) {
      return;
    }

    const triggerRect = trigger.getBoundingClientRect();
    const boundaryRect = menuBoundaryRef?.current?.getBoundingClientRect() ?? {
      top: 8,
      right: window.innerWidth - 8,
      bottom: window.innerHeight - 8,
      left: 8,
      width: window.innerWidth - 16,
      height: window.innerHeight - 16,
      x: 8,
      y: 8,
      toJSON: () => ({}),
    };

    const horizontalPadding = 8;
    const verticalPadding = 8;
    const minHeight = 96;
    const preferredHeight = 256;

    const availableBelow = boundaryRect.bottom - triggerRect.bottom - verticalPadding;
    const availableAbove = triggerRect.top - boundaryRect.top - verticalPadding;
    const openBelow = availableBelow >= availableAbove;
    const availablePrimary = openBelow ? availableBelow : availableAbove;
    const maxHeight = Math.max(minHeight, Math.min(preferredHeight, availablePrimary));

    const estimatedMenuHeight = Math.min(preferredHeight, Math.max(40, options.length * 36 + 8));
    const menuHeight = Math.min(maxHeight, estimatedMenuHeight);

    const unclampedLeft = triggerRect.left;
    const maxLeft = boundaryRect.right - triggerRect.width - horizontalPadding;
    const minLeft = boundaryRect.left + horizontalPadding;
    const left = Math.max(minLeft, Math.min(unclampedLeft, maxLeft));
    const top = openBelow
      ? triggerRect.bottom + 6
      : triggerRect.top - menuHeight - 6;

    setMenuStyle({
      left,
      top,
      width: triggerRect.width,
      maxHeight,
    });
  };

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    recalculateMenuPosition();

    const handleOutsideClick = (event: MouseEvent) => {
      const target = event.target as Node | null;
      if (!target) {
        return;
      }

      if (containerRef.current?.contains(target) || menuRef.current?.contains(target)) {
        return;
      }

      setIsOpen(false);
    };

    const handleReposition = () => {
      recalculateMenuPosition();
    };

    window.addEventListener('mousedown', handleOutsideClick);
    window.addEventListener('resize', handleReposition);
    window.addEventListener('scroll', handleReposition, true);
    return () => {
      window.removeEventListener('mousedown', handleOutsideClick);
      window.removeEventListener('resize', handleReposition);
      window.removeEventListener('scroll', handleReposition, true);
    };
  }, [isOpen, menuBoundaryRef, options.length]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    // The "none" sentinel is not a real option — it means "no provider configured yet"
    // and the user is expected to pick one from the open menu. Don't auto-close.
    if (value === PROVIDER_SENTINEL_UNCONFIGURED) {
      return;
    }

    if (!options.some((option) => option.value === value)) {
      setIsOpen(false);
    }
  }, [isOpen, options, value]);

  const toggleOpen = () => {
    const next = !isOpen;
    setIsOpen(next);
    if (next) {
      onOpen?.();
    }
  };

  const handleSelect = (nextValue: string) => {
    onChange(nextValue);
    setIsOpen(false);
  };

  return (
    <div ref={containerRef} className={`relative ${className}`}>
      <button
        type="button"
        onClick={toggleOpen}
        className={`input !text-base !pr-9 text-left cursor-pointer ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100' : ''}`}
      >
        <span className="block truncate">
          {value === PROVIDER_SENTINEL_UNCONFIGURED
            ? '—'
            : (selected?.label ?? value)}
        </span>
      </button>
      <div className="absolute inset-y-0 right-0 flex items-center px-3 pointer-events-none">
        <ChevronDown size={16} className={isDarkMode ? 'text-surface-400' : 'text-surface-500'} />
      </div>

      {isOpen && createPortal(
        <div
          ref={menuRef}
          className={`fixed z-[70] rounded-xl border shadow-premium overflow-hidden ${isDarkMode ? 'border-surface-600 bg-surface-800' : 'border-surface-200 bg-white'}`}
          style={{
            left: `${menuStyle.left}px`,
            top: `${menuStyle.top}px`,
            width: `${menuStyle.width}px`,
          }}
        >
          <div className="overflow-y-auto p-1" style={{ maxHeight: `${menuStyle.maxHeight}px` }}>
            {options.map((option) => {
              const active = option.value === value;
              return (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => handleSelect(option.value)}
                  className={`w-full text-left px-3 py-2 rounded-lg text-sm transition-colors ${active
                    ? (isDarkMode ? 'bg-accent-900/40 text-accent-300' : 'bg-accent-50 text-accent-700')
                    : (isDarkMode ? 'text-surface-200 hover:bg-surface-700' : 'text-surface-700 hover:bg-surface-50')
                  }`}
                >
                  {option.label}
                </button>
              );
            })}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

export function ToggleRow({
  label,
  checked,
  onChange,
  isDarkMode = false,
}: {
  label: string;
  checked: boolean;
  onChange: () => void;
  isDarkMode?: boolean;
}) {
  return (
    <div className="flex items-center justify-between py-1">
      <label className={`text-base font-medium ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>{label}</label>
      <button
        type="button"
        onClick={onChange}
        className={`toggle-track ${checked ? 'toggle-track-on' : 'toggle-track-off'}`}
      >
        <span className={`toggle-thumb ${checked ? 'toggle-thumb-on' : 'toggle-thumb-off'}`} />
      </button>
    </div>
  );
}
