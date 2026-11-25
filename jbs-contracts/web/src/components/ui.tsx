// Shared components (design-system.md section 8). Screens use these, never one-off styling.
import { forwardRef, useId, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, Loader2, XCircle } from 'lucide-react'
import { cn } from '@/lib/cn'
import { initials } from '@/lib/format'

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'link'

export const Button = forwardRef<
  HTMLButtonElement,
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: 'md' | 'lg'; loading?: boolean; icon?: ReactNode }
>(function Button({ variant = 'secondary', size = 'md', loading, icon, className, children, disabled, type = 'button', ...props }, ref) {
  return (
    <button
      ref={ref}
      // Never submit a surrounding form by accident: only buttons marked type="submit" do.
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cn(
        'relative inline-flex items-center justify-center gap-2 rounded-[var(--radius-control)] font-medium transition-colors duration-150',
        'disabled:cursor-not-allowed disabled:opacity-60',
        size === 'md' ? 'min-h-11 px-4 text-sm' : 'min-h-[52px] px-5 text-base',
        variant === 'primary' && 'bg-primary text-primary-foreground hover:bg-primary-hover active:bg-primary-active',
        variant === 'secondary' && 'border border-input bg-card text-foreground hover:bg-muted',
        variant === 'ghost' && 'text-foreground hover:bg-muted',
        variant === 'danger' && 'bg-danger text-white hover:opacity-90',
        variant === 'link' && 'min-h-0 px-0 text-primary underline-offset-4 hover:underline',
        className,
      )}
      {...props}
    >
      {loading && <Loader2 aria-hidden className="absolute size-5 animate-spin" />}
      <span className={cn('inline-flex items-center gap-2', loading && 'invisible')}>
        {icon}
        {children}
      </span>
    </button>
  )
})

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('rounded-[var(--radius-card)] border border-border bg-card', className)}>{children}</div>
}

export type Tone = 'neutral' | 'success' | 'warning' | 'danger' | 'info' | 'brand'

const toneClasses: Record<Tone, string> = {
  neutral: 'bg-muted text-muted-foreground',
  success: 'bg-success-soft text-success',
  warning: 'bg-warning-soft text-warning',
  danger: 'bg-danger-soft text-danger',
  info: 'bg-info-soft text-info',
  brand: 'bg-primary-soft text-primary-soft-foreground',
}

/** Status is always icon + word, never colour alone. */
export function Badge({ tone = 'neutral', icon, children }: { tone?: Tone; icon?: ReactNode; children: ReactNode }) {
  return (
    <span className={cn('inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium', toneClasses[tone])}>
      {icon}
      {children}
    </span>
  )
}

export function Avatar({ name, size = 36 }: { name: string; size?: number }) {
  return (
    <span
      aria-hidden
      style={{ width: size, height: size, fontSize: size * 0.38 }}
      className="inline-flex shrink-0 items-center justify-center rounded-full bg-primary-soft font-semibold text-primary-soft-foreground"
    >
      {initials(name)}
    </span>
  )
}

/** Label above, help below, plain-words error under the field. "(required)", never an asterisk. */
export function Field({
  label,
  help,
  error,
  required,
  children,
}: {
  label: string
  help?: ReactNode
  error?: string
  required?: boolean
  children: (props: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean }) => ReactNode
}) {
  const id = useId()
  const helpId = `${id}-help`
  const errorId = `${id}-error`
  const describedBy = [help ? helpId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
        {required && <span className="font-normal text-muted-foreground"> (required)</span>}
      </label>
      {children({ id, 'aria-describedby': describedBy, 'aria-invalid': error ? true : undefined })}
      {help && (
        <p id={helpId} className="text-xs text-muted-foreground">
          {help}
        </p>
      )}
      {error && (
        <p id={errorId} className="flex items-start gap-1 text-xs font-medium text-danger">
          <XCircle aria-hidden className="mt-0.5 size-3.5 shrink-0" />
          {error}
        </p>
      )}
    </div>
  )
}

export const TextInput = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(function TextInput(
  { className, ...props },
  ref,
) {
  return (
    <input
      ref={ref}
      className={cn(
        'min-h-11 w-full rounded-[var(--radius-control)] border border-input bg-card px-3 text-foreground placeholder:text-muted-foreground',
        'aria-[invalid=true]:border-danger',
        className,
      )}
      {...props}
    />
  )
})

export function Checkbox({
  label,
  description,
  checked,
  onChange,
  disabled,
}: {
  label: string
  description?: string
  checked: boolean
  onChange: (checked: boolean) => void
  disabled?: boolean
}) {
  const id = useId()
  return (
    <div className="flex min-h-11 items-start gap-3 py-1">
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        aria-describedby={description ? `${id}-d` : undefined}
        className="mt-0.5 size-5 shrink-0 accent-[var(--primary)]"
      />
      <div className="flex flex-col">
        <label htmlFor={id} className="cursor-pointer font-medium">
          {label}
        </label>
        {description && (
          <span id={`${id}-d`} className="text-xs text-muted-foreground">
            {description}
          </span>
        )}
      </div>
    </div>
  )
}

export function EmptyState({ icon, title, action }: { icon: ReactNode; title: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
      <span aria-hidden className="text-muted-foreground [&>svg]:size-10">
        {icon}
      </span>
      <p className="max-w-md text-muted-foreground">{title}</p>
      {action}
    </div>
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden className={cn('animate-pulse rounded-md bg-muted', className)} />
}

export function Alert({ tone = 'info', title, children }: { tone?: 'info' | 'warning' | 'danger' | 'success'; title: string; children?: ReactNode }) {
  const Icon = { info: Info, warning: AlertTriangle, danger: XCircle, success: CheckCircle2 }[tone]
  return (
    <div role={tone === 'danger' ? 'alert' : 'status'} className={cn('flex gap-3 rounded-[var(--radius-card)] p-4', toneClasses[tone])}>
      <Icon aria-hidden className="mt-0.5 size-5 shrink-0" />
      <div className="flex flex-col gap-1">
        <p className="font-semibold">{title}</p>
        {children && <div className="text-sm text-foreground">{children}</div>}
      </div>
    </div>
  )
}

/** Segmented filter, e.g. "Active 12 | Switched off 1". */
export function Segmented<T extends string>({
  label,
  options,
  value,
  onChange,
}: {
  label: string
  options: { value: T; label: string; count?: number }[]
  value: T
  onChange: (value: T) => void
}) {
  return (
    // Scrolls sideways on narrow screens rather than cutting options off.
    <div role="radiogroup" aria-label={label} className="inline-flex max-w-full overflow-x-auto rounded-[var(--radius-control)] border border-border bg-card p-1 [scrollbar-width:none]">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            'min-h-9 shrink-0 rounded-lg px-3 text-sm font-medium whitespace-nowrap transition-colors',
            value === o.value ? 'bg-primary-soft text-primary-soft-foreground' : 'text-muted-foreground hover:text-foreground',
          )}
        >
          {o.label}
          {o.count !== undefined && <span className="tabular ml-1.5 text-xs">{o.count}</span>}
        </button>
      ))}
    </div>
  )
}
