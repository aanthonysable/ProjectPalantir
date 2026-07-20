import { useEffect, useId, useRef, useState } from 'react'

export type MenuSelectOption = { value: string; label: string }

export function MenuSelect({
  label,
  value,
  options,
  disabled,
  onChange,
  placeholder = 'Select…',
}: {
  label: string
  value: string
  options: MenuSelectOption[]
  disabled?: boolean
  onChange: (value: string) => void
  placeholder?: string
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const listId = useId()
  const labelId = `${listId}-label`
  const selected = options.find((o) => o.value === value)
  const display = selected?.label ?? placeholder

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open])

  useEffect(() => {
    if (disabled) setOpen(false)
  }, [disabled])

  return (
    <div
      className={['menu-select', disabled ? 'is-disabled' : '', open ? 'is-open' : '']
        .filter(Boolean)
        .join(' ')}
      ref={rootRef}
    >
      <span className="menu-select-label" id={labelId}>
        {label}
      </span>
      <button
        type="button"
        className="ghost menu-select-trigger"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        aria-labelledby={labelId}
        onClick={() => setOpen((v) => !v)}
      >
        <span className="menu-select-value">{display}</span>
        <svg className="menu-select-caret" viewBox="0 0 16 16" aria-hidden="true">
          <path
            d="M4.2 6.2a.75.75 0 0 1 1.06 0L8 8.94l2.74-2.74a.75.75 0 1 1 1.06 1.06l-3.27 3.27a.75.75 0 0 1-1.06 0L4.2 7.26a.75.75 0 0 1 0-1.06Z"
            fill="currentColor"
          />
        </svg>
      </button>
      {open && (
        <>
          <button
            type="button"
            className="menu-select-backdrop"
            aria-label={`Close ${label}`}
            onClick={() => setOpen(false)}
          />
          <ul className="menu-select-menu" role="listbox" id={listId} aria-labelledby={labelId}>
            <li className="menu-select-sheet-cap" aria-hidden="true">
              <span className="menu-select-sheet-grip" />
              <span className="menu-select-sheet-label">{label}</span>
            </li>
            {options.map((opt) => {
              const isSelected = opt.value === value
              return (
                <li key={opt.value} role="presentation">
                  <button
                    type="button"
                    role="option"
                    aria-selected={isSelected}
                    className={
                      isSelected
                        ? 'ghost menu-select-option is-selected'
                        : 'ghost menu-select-option'
                    }
                    onClick={() => {
                      onChange(opt.value)
                      setOpen(false)
                    }}
                  >
                    <span className="menu-select-option-label">{opt.label}</span>
                    {isSelected && (
                      <svg className="menu-select-check" viewBox="0 0 16 16" aria-hidden="true">
                        <path
                          d="M6.4 11.3 3.2 8.1a.75.75 0 0 1 1.06-1.06l2.14 2.14 5.14-5.14a.75.75 0 1 1 1.06 1.06l-5.67 5.67a.75.75 0 0 1-1.06 0Z"
                          fill="currentColor"
                        />
                      </svg>
                    )}
                  </button>
                </li>
              )
            })}
          </ul>
        </>
      )}
    </div>
  )
}
