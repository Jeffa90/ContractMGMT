// Ctrl+K (Cmd+K on a Mac): jump to any contract, supplier or person, or add a contract (product-spec section 11).
// A combobox: type, move with the arrow keys, Enter to open. Results only include what the person can see.
import * as RadixDialog from '@radix-ui/react-dialog'
import { useQuery } from '@tanstack/react-query'
import { Building2, FilePlus2, FileText, Search, User } from 'lucide-react'
import { useDeferredValue, useEffect, useId, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { api } from '@/lib/api'
import { cn } from '@/lib/cn'
import { useMe } from '@/lib/me'
import { openSearchEvent } from '@/lib/search'

type Results = {
  contracts: { id: number; reference: string; title: string; stageName: string; supplierName: string | null }[]
  suppliers: { id: number; name: string }[]
  people: { id: number; name: string; windowsAccount: string }[]
}

type Item = { key: string; group: string; icon: ReactNode; label: string; detail?: string; to: string }

export function SearchPalette() {
  const [open, setOpen] = useState(false)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setOpen((o) => !o)
      }
    }
    const onOpen = () => setOpen(true)
    window.addEventListener('keydown', onKey)
    window.addEventListener(openSearchEvent, onOpen)
    return () => {
      window.removeEventListener('keydown', onKey)
      window.removeEventListener(openSearchEvent, onOpen)
    }
  }, [])

  return (
    <RadixDialog.Root open={open} onOpenChange={setOpen}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <RadixDialog.Content className="fixed inset-x-0 top-0 z-50 flex max-h-[85dvh] flex-col overflow-hidden border-b border-border bg-card shadow-xl md:top-[12vh] md:left-1/2 md:w-full md:max-w-xl md:-translate-x-1/2 md:rounded-[var(--radius-card)] md:border">
          <RadixDialog.Title className="sr-only">Search</RadixDialog.Title>
          <RadixDialog.Description className="sr-only">Find a contract, supplier or person. Use the arrow keys to choose and Enter to open.</RadixDialog.Description>
          {open && <Palette onClose={() => setOpen(false)} />}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  )
}

function Palette({ onClose }: { onClose: () => void }) {
  const { person } = useMe()
  const navigate = useNavigate()
  const listId = useId()
  const [q, setQ] = useState('')
  const [active, setActive] = useState(0)
  const text = useDeferredValue(q.trim())
  const results = useQuery({
    queryKey: ['search', text],
    queryFn: () => api.get<Results>(`/api/search?q=${encodeURIComponent(text)}`),
    enabled: text.length > 0,
    placeholderData: (previous) => previous,
  })

  const items: Item[] = []
  if (person.roles.contractOwner && (text.length === 0 || 'add contract'.includes(text.toLowerCase())))
    items.push({ key: 'add', group: 'Actions', icon: <FilePlus2 aria-hidden className="size-4" />, label: 'Add contract', to: '/contracts/new' })
  if (text.length > 0 && results.data) {
    for (const c of results.data.contracts)
      items.push({ key: `c${c.id}`, group: 'Contracts', icon: <FileText aria-hidden className="size-4" />, label: c.title, detail: `${c.reference} · ${c.stageName}${c.supplierName ? ` · ${c.supplierName}` : ''}`, to: `/contracts/${c.id}` })
    for (const s of results.data.suppliers)
      items.push({ key: `s${s.id}`, group: 'Suppliers', icon: <Building2 aria-hidden className="size-4" />, label: s.name, to: `/suppliers/${s.id}` })
    for (const p of results.data.people)
      items.push({ key: `p${p.id}`, group: 'People', icon: <User aria-hidden className="size-4" />, label: p.name, detail: p.windowsAccount, to: `/admin/people?q=${encodeURIComponent(p.name)}` })
  }
  const current = Math.min(active, Math.max(items.length - 1, 0))
  const go = (item: Item | undefined) => {
    if (!item) return
    onClose()
    navigate(item.to)
  }

  return (
    <>
      <div className="flex items-center gap-3 border-b border-border px-4">
        <Search aria-hidden className="size-5 shrink-0 text-muted-foreground" />
        <input
          autoFocus
          role="combobox"
          aria-expanded={items.length > 0}
          aria-controls={listId}
          aria-activedescendant={items[current] ? `${listId}-${items[current].key}` : undefined}
          aria-label="Find a contract, supplier or person"
          placeholder="Find a contract, supplier or person"
          value={q}
          onChange={(e) => {
            setQ(e.target.value)
            setActive(0)
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault()
              setActive((current + 1) % Math.max(items.length, 1))
            } else if (e.key === 'ArrowUp') {
              e.preventDefault()
              setActive((current - 1 + items.length) % Math.max(items.length, 1))
            } else if (e.key === 'Enter') {
              e.preventDefault()
              go(items[current])
            }
          }}
          className="min-h-14 w-full bg-transparent text-base outline-none placeholder:text-muted-foreground"
        />
        <kbd className="hidden rounded border border-border px-1.5 py-0.5 text-xs text-muted-foreground sm:inline">Esc</kbd>
      </div>
      <div className="overflow-y-auto p-2">
        {items.length === 0 ? (
          <p className="px-3 py-6 text-center text-sm text-muted-foreground" role="status">
            {text.length === 0 ? 'Type a title, reference (JBSAUS-0012), supplier or SAP number.' : results.isFetching ? 'Searching…' : `Nothing matches "${text}".`}
          </p>
        ) : (
          <ul id={listId} role="listbox" aria-label="Results" className="flex flex-col">
            {items.map((item, i) => (
              <li key={item.key} role="presentation">
                {(i === 0 || items[i - 1].group !== item.group) && <p className="px-3 pt-3 pb-1 text-xs font-medium text-muted-foreground">{item.group}</p>}
                <div
                  id={`${listId}-${item.key}`}
                  role="option"
                  aria-selected={i === current}
                  onMouseMove={() => setActive(i)}
                  onClick={() => go(item)}
                  className={cn('flex min-h-11 cursor-pointer items-center gap-3 rounded-[var(--radius-control)] px-3 py-2', i === current && 'bg-primary-soft text-primary-soft-foreground')}
                >
                  <span className="shrink-0 text-muted-foreground">{item.icon}</span>
                  <span className="flex min-w-0 flex-col">
                    <span className="truncate font-medium">{item.label}</span>
                    {item.detail && <span className="truncate text-xs text-muted-foreground">{item.detail}</span>}
                  </span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </>
  )
}
