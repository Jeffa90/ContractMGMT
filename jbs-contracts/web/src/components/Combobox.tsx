// A text box that finds a choice as you type (D-115): for long lists like the spend taxonomy. Every word typed must appear in the
// choice, in any order ("collagen cas" finds "Packaging › Casings › Collagen casing"). Arrow keys move, Enter picks, Escape puts
// it back. Follows the WAI-ARIA combobox pattern.
import { ChevronDown, Search } from 'lucide-react'
import { useId, useMemo, useRef, useState } from 'react'
import { cn } from '@/lib/cn'

export type ComboOption = { value: string; label: string; hint?: string }

const MAX_SHOWN = 60

export function Combobox({
  id,
  options,
  value,
  onChange,
  placeholder = 'Type to find',
  'aria-describedby': describedBy,
  'aria-invalid': invalid,
  'aria-label': ariaLabel,
  disabled,
  className,
}: {
  id?: string
  options: ComboOption[]
  value: string
  onChange: (value: string) => void
  placeholder?: string
  'aria-describedby'?: string
  'aria-invalid'?: boolean
  'aria-label'?: string
  disabled?: boolean
  className?: string
}) {
  const listId = useId()
  const optionId = (i: number) => `${listId}-${i}`
  const selected = options.find((o) => o.value === value)
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const [active, setActive] = useState(0)
  const input = useRef<HTMLInputElement>(null)

  const matches = useMemo(() => {
    const words = text.toLowerCase().split(/\s+/).filter(Boolean)
    if (words.length === 0) return options
    return options.filter((o) => {
      const hay = `${o.label} ${o.hint ?? ''}`.toLowerCase()
      return words.every((w) => hay.includes(w))
    })
  }, [options, text])
  const shown = matches.slice(0, MAX_SHOWN)

  const close = () => {
    setOpen(false)
    setText('')
  }
  const pick = (o: ComboOption) => {
    onChange(o.value)
    close()
  }

  return (
    <div className={cn('relative', className)}>
      <div className="relative">
        <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
        <input
          ref={input}
          id={id}
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={open && shown[active] ? optionId(active) : undefined}
          aria-describedby={describedBy}
          aria-invalid={invalid}
          aria-label={ariaLabel}
          disabled={disabled}
          autoComplete="off"
          spellCheck={false}
          placeholder={selected ? selected.label : placeholder}
          value={open ? text : (selected?.label ?? '')}
          onFocus={() => {
            setOpen(true)
            setActive(
              Math.max(
                0,
                options.findIndex((o) => o.value === value),
              ),
            )
          }}
          onBlur={close}
          onChange={(e) => {
            setText(e.target.value)
            setActive(0)
            setOpen(true)
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault()
              setOpen(true)
              setActive((a) => Math.min(a + 1, shown.length - 1))
            } else if (e.key === 'ArrowUp') {
              e.preventDefault()
              setActive((a) => Math.max(a - 1, 0))
            } else if (e.key === 'Enter' && open) {
              e.preventDefault()
              if (shown[active]) pick(shown[active])
            } else if (e.key === 'Escape' && open) {
              e.preventDefault()
              close()
            }
          }}
          className="min-h-11 w-full rounded-[var(--radius-control)] border border-input bg-card pr-9 pl-9 text-foreground placeholder:text-muted-foreground disabled:opacity-60 aria-[invalid=true]:border-danger"
        />
        <ChevronDown aria-hidden className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2" />
      </div>
      {open && (
        <ul
          id={listId}
          role="listbox"
          className="absolute z-50 mt-1 max-h-80 w-full overflow-y-auto rounded-[var(--radius-card)] border border-border bg-card p-1 shadow-lg"
        >
          {shown.length === 0 && <li className="px-3 py-2 text-sm text-muted-foreground">Nothing matches "{text}". Try fewer letters.</li>}
          {shown.map((o, i) => (
            <li
              key={o.value}
              id={optionId(i)}
              role="option"
              aria-selected={o.value === value}
              // Mouse down, not click, so the pick happens before the box loses focus.
              onMouseDown={(e) => {
                e.preventDefault()
                pick(o)
              }}
              onMouseMove={() => setActive(i)}
              className={cn(
                'flex min-h-11 cursor-pointer flex-col justify-center rounded-lg px-3 py-1.5',
                i === active && 'bg-muted',
                o.value === value && 'font-medium text-primary',
              )}
            >
              <span>{o.label}</span>
              {o.hint && <span className="text-xs text-muted-foreground">{o.hint}</span>}
            </li>
          ))}
          {matches.length > shown.length && (
            <li className="px-3 py-2 text-xs text-muted-foreground">{matches.length - shown.length} more. Keep typing to narrow it down.</li>
          )}
        </ul>
      )}
    </div>
  )
}
