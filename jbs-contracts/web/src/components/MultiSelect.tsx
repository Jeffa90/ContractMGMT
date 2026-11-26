// A drop-down where several values can be ticked (D-114): it stays open while you tick, with Select all and Clear at the top and
// Done at the bottom. Escape or clicking away also closes it. Used for business unit, site, entity and function, and for added
// fields where several can be chosen.
import * as Menu from '@radix-ui/react-dropdown-menu'
import { Check, ChevronDown } from 'lucide-react'
import { useId } from 'react'
import { cn } from '@/lib/cn'

export type MultiOption<K extends string | number> = { id: K; label: string; hidden?: boolean }

const item = 'flex min-h-11 cursor-pointer items-center gap-3 rounded-lg px-3 outline-none select-none data-[highlighted]:bg-muted data-[disabled]:cursor-default data-[disabled]:opacity-50'

function Box({ checked }: { checked: boolean }) {
  return (
    <span
      aria-hidden
      className={cn(
        'flex size-5 shrink-0 items-center justify-center rounded border-2',
        checked ? 'border-primary bg-primary text-primary-foreground' : 'border-input bg-card',
      )}
    >
      {checked && <Check className="size-3.5" strokeWidth={3} />}
    </span>
  )
}

export function MultiSelect<K extends string | number>({
  id,
  options,
  selected,
  onChange,
  disabled,
  'aria-describedby': describedBy,
  'aria-invalid': invalid,
}: {
  id?: string
  options: MultiOption<K>[]
  selected: K[]
  onChange: (ids: K[]) => void
  disabled?: boolean
  'aria-describedby'?: string
  'aria-invalid'?: boolean
}) {
  const chosen = options.filter((o) => selected.includes(o.id))
  const all = options.length > 0 && chosen.length === options.length
  const summary = chosen.length === 0 ? 'Not chosen' : chosen.map((o) => o.label).join(', ')
  // In the list's order, whatever order they were ticked in.
  const toggle = (optionId: K, on: boolean) => onChange(options.filter((o) => (o.id === optionId ? on : selected.includes(o.id))).map((o) => o.id))
  const keepOpen = (e: Event) => e.preventDefault()
  // The button's name is its label; what's chosen is read out as its description.
  const summaryId = useId()

  return (
    <Menu.Root modal={false}>
      <Menu.Trigger
        id={id}
        disabled={disabled}
        aria-describedby={[summaryId, describedBy].filter(Boolean).join(' ')}
        aria-invalid={invalid}
        className="flex min-h-11 w-full items-center gap-2 rounded-[var(--radius-control)] border border-input bg-card px-3 text-left text-foreground disabled:opacity-60 aria-[invalid=true]:border-danger"
      >
        <span id={summaryId} className={cn('min-w-0 flex-1 truncate', chosen.length === 0 && 'text-muted-foreground')}>
          {summary}
        </span>
        {chosen.length > 1 && <span className="shrink-0 text-xs text-muted-foreground tabular">{chosen.length} chosen</span>}
        <ChevronDown aria-hidden className="size-4 shrink-0" />
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Content
          align="start"
          sideOffset={4}
          collisionPadding={8}
          className="z-50 flex max-h-[min(26rem,var(--radix-dropdown-menu-content-available-height))] w-[var(--radix-dropdown-menu-trigger-width)] min-w-64 flex-col rounded-[var(--radius-card)] border border-border bg-card p-1 shadow-lg"
        >
          <div className="flex gap-1 border-b border-border pb-1">
            <Menu.Item className={cn(item, 'flex-1 justify-center font-medium text-primary')} disabled={all} onSelect={(e) => { keepOpen(e); onChange(options.map((o) => o.id)) }}>
              Select all
            </Menu.Item>
            <Menu.Item className={cn(item, 'flex-1 justify-center font-medium text-primary')} disabled={chosen.length === 0} onSelect={(e) => { keepOpen(e); onChange([]) }}>
              Clear
            </Menu.Item>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto py-1">
            {options.map((o) => {
              const checked = selected.includes(o.id)
              return (
                <Menu.CheckboxItem key={o.id} className={item} checked={checked} onCheckedChange={(on) => toggle(o.id, on === true)} onSelect={keepOpen}>
                  <Box checked={checked} />
                  <span className="min-w-0 flex-1">
                    {o.label}
                    {o.hidden && <span className="text-muted-foreground"> (hidden)</span>}
                  </span>
                </Menu.CheckboxItem>
              )
            })}
          </div>
          <div className="border-t border-border pt-1">
            <Menu.Item className={cn(item, 'justify-center font-medium')}>Done</Menu.Item>
          </div>
        </Menu.Content>
      </Menu.Portal>
    </Menu.Root>
  )
}
