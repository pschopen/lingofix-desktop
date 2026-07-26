import { Plus, Copy, Pencil, Trash2 } from 'lucide-react';
import { Language, t } from '../../i18n';
import { FieldGroup, SelectField } from './shared';

export type PresetDialogMode = 'new' | 'rename' | 'delete' | null;

export interface PromptPresetSummary {
  id: string;
  name: string;
}

export interface PromptPresetEditorField {
  key: string;
  label?: string;
  hint?: string;
  placeholder?: string;
  value: string;
  onChange: (value: string) => void;
}

interface PromptPresetEditorProps {
  label: string;
  presets: PromptPresetSummary[];
  activePresetId: string;
  activePresetName: string;
  onSelect: (id: string) => void;
  onCreate: () => void;
  onDuplicate: () => void;
  onRename: () => void;
  onDelete: () => void;
  fields: PromptPresetEditorField[];
  message: string;
  dialogMode: PresetDialogMode;
  dialogValue: string;
  onDialogValueChange: (value: string) => void;
  onDialogConfirm: () => void;
  onDialogCancel: () => void;
  isDarkMode: boolean;
  lang: Language;
  menuBoundaryRef: React.RefObject<HTMLElement | null>;
}

/**
 * Reusable preset editor: select/new/duplicate/rename/delete toolbar over one or more
 * text fields. Used for the correction prompt (one field) and the translation prompt
 * (two fields: main text + footnotes), parametrized via `fields` rather than copied.
 */
export function PromptPresetEditor({
  label,
  presets,
  activePresetId,
  activePresetName,
  onSelect,
  onCreate,
  onDuplicate,
  onRename,
  onDelete,
  fields,
  message,
  dialogMode,
  dialogValue,
  onDialogValueChange,
  onDialogConfirm,
  onDialogCancel,
  isDarkMode,
  lang,
  menuBoundaryRef,
}: PromptPresetEditorProps) {
  return (
    <>
      <FieldGroup label={label} isDarkMode={isDarkMode}>
        <div className={`rounded-xl border p-3 space-y-3 ${isDarkMode ? 'border-surface-700 bg-surface-800/50' : 'border-surface-200 bg-surface-50/80'}`}>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={onCreate}
              className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
            >
              <Plus size={12} />
              {t('settings.prompt_presets.new', lang)}
            </button>
            <button
              type="button"
              onClick={onDuplicate}
              className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
            >
              <Copy size={12} />
              {t('settings.prompt_presets.duplicate', lang)}
            </button>
            <button
              type="button"
              onClick={onRename}
              className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
            >
              <Pencil size={12} />
              {t('settings.prompt_presets.rename', lang)}
            </button>
            <button
              type="button"
              onClick={onDelete}
              className="btn-secondary !text-sm !px-2.5 !py-1.5 !rounded-md !gap-1"
            >
              <Trash2 size={12} />
              {t('settings.prompt_presets.delete', lang)}
            </button>
          </div>

          <SelectField
            value={activePresetId}
            onChange={onSelect}
            menuBoundaryRef={menuBoundaryRef}
            isDarkMode={isDarkMode}
          >
            {presets.map((preset) => (
              <option key={preset.id} value={preset.id} className={isDarkMode ? '!bg-surface-700 !text-surface-100' : ''}>
                {preset.name}
              </option>
            ))}
          </SelectField>

          {fields.map((field) => (
            <div key={field.key} className="space-y-1">
              {field.label && (
                <label className={`block text-sm font-medium ${isDarkMode ? 'text-surface-300' : 'text-surface-600'}`}>
                  {field.label}
                </label>
              )}
              <textarea
                value={field.value}
                onChange={(e) => field.onChange(e.target.value)}
                placeholder={field.placeholder}
                className={`textarea !text-base h-28 ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100 placeholder:!text-surface-500' : ''}`}
              />
              {field.hint && (
                <p className={`text-sm ${isDarkMode ? 'text-surface-400' : 'text-surface-500'}`}>{field.hint}</p>
              )}
            </div>
          ))}

          {message && (
            <p className="text-sm text-amber-600">{message}</p>
          )}
        </div>
      </FieldGroup>

      {dialogMode && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 backdrop-blur-[1px] px-4">
          <div className={`w-full max-w-md rounded-xl overflow-hidden border shadow-xl ${isDarkMode ? 'bg-surface-800 border-surface-700 text-surface-100' : 'bg-white border-surface-200 text-surface-900'}`}>
            <div className={`px-5 py-4 border-b ${isDarkMode ? 'border-surface-700' : 'border-surface-100'}`}>
              <h3 className="text-base font-semibold">
                {dialogMode === 'new'
                  ? t('settings.prompt_presets.new', lang)
                  : dialogMode === 'rename'
                    ? t('settings.prompt_presets.rename', lang)
                    : t('settings.prompt_presets.delete', lang)}
              </h3>
            </div>

            <div className="px-5 py-4 space-y-3">
              {dialogMode === 'delete' ? (
                <p className={`text-sm ${isDarkMode ? 'text-surface-300' : 'text-surface-700'}`}>
                  {t('settings.prompt_presets.delete_confirm', lang).replace('{name}', activePresetName)}
                </p>
              ) : (
                <>
                  <label className={`block text-sm font-medium ${isDarkMode ? 'text-surface-200' : 'text-surface-700'}`}>
                    {t('settings.prompt_presets.name_label', lang)}
                  </label>
                  <input
                    type="text"
                    value={dialogValue}
                    onChange={(e) => onDialogValueChange(e.target.value)}
                    className={`input !text-base ${isDarkMode ? '!bg-surface-700 !border-surface-600 !text-surface-100' : ''}`}
                    autoFocus
                  />
                </>
              )}
            </div>

            <div className={`px-5 py-4 border-t flex justify-end gap-2 ${isDarkMode ? 'border-surface-700 bg-surface-900/50' : 'border-surface-100 bg-white'}`}>
              <button type="button" onClick={onDialogCancel} className="btn-secondary !text-base">
                {t('settings.cancel', lang)}
              </button>
              <button type="button" onClick={onDialogConfirm} className="btn-primary !text-base">
                {dialogMode === 'delete' ? t('settings.prompt_presets.delete', lang) : t('settings.save', lang)}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
