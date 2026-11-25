// Admin, Lists: the choices on contracts (categories with subcategories, agreement types, business units, sites, currencies, document
// kinds) and on suppliers (document types, tiers, risk levels).
// Nothing is deleted: an item is hidden and stays on contracts that already use it.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Eye, EyeOff, Pencil, Plus } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { ExcelButtons } from '@/components/ExcelImport'
import { PageHeader } from '@/components/Help'
import { Select } from '@/components/form'
import { Alert, Badge, Button, Card, Field, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import { cn } from '@/lib/cn'
import type { Category, ListItem, Lists } from '@/lib/contracts'
import { useMe } from '@/lib/me'
import { help } from '@/help'

type Tab = 'Category' | 'AgreementType' | 'BusinessUnit' | 'Site' | 'Currency' | 'SupplierDocumentType' | 'SupplierTier' | 'DocumentKind' | 'SupplierRisk'
const tabs: { value: Tab; label: string; single: string; group: 'Contracts' | 'Suppliers'; note?: string }[] = [
  { value: 'Category', group: 'Contracts', label: 'Categories', single: 'category' },
  { value: 'AgreementType', group: 'Contracts', label: 'Agreement types', single: 'agreement type' },
  { value: 'BusinessUnit', group: 'Contracts', label: 'Business units', single: 'business unit' },
  { value: 'Site', group: 'Contracts', label: 'Sites', single: 'site' },
  { value: 'Currency', group: 'Contracts', label: 'Currencies', single: 'currency' },
  { value: 'SupplierDocumentType', group: 'Suppliers', label: 'Supplier document types', single: 'supplier document type' },
  { value: 'SupplierTier', group: 'Suppliers', label: 'Supplier tiers', single: 'supplier tier' },
  {
    value: 'DocumentKind',
    group: 'Contracts',
    label: 'Contract document kinds',
    single: 'document kind',
    note: 'Draft, Final agreement, Signed copy, Schedule, Legal mark-up, Variation, Correspondence, Supporting document and Internal sign-off are always there (the app relies on them), so they aren\'t shown here. Add your own, for example Insurance certificate or Quote.',
  },
  {
    value: 'SupplierRisk',
    group: 'Suppliers',
    label: 'Supplier risk levels',
    single: 'risk level',
    note: 'Low, Medium and High are always there, so they aren\'t shown here. Add your own, for example Critical. Every level except Low asks for a reason.',
  },
]

type Editing = { kind: 'add'; parent: Category | null } | { kind: 'rename'; item: ListItem | Category }

export function ListsPage() {
  const { person } = useMe()
  const queryClient = useQueryClient()
  const [tab, setTab] = useState<Tab>('Category')
  const [editing, setEditing] = useState<Editing | null>(null)
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const toggle = useMutation({
    mutationFn: (x: { item: ListItem | Category }) =>
      'listName' in x.item ? api.put(`/api/lists/items/${x.item.id}`, { isActive: !x.item.isActive }) : api.put(`/api/categories/${x.item.id}`, { isActive: !x.item.isActive }),
    onSuccess: (_, x) => {
      queryClient.invalidateQueries({ queryKey: ['lists'] })
      const name = 'listName' in x.item ? x.item.value : x.item.name
      toast.success(x.item.isActive ? `${name} hidden. Contracts that use it keep it.` : `${name} is back in use.`)
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : 'That didn\'t work. Try again.'),
  })

  if (!person.roles.admin) return <Alert tone="warning" title="Only admins can change the lists.">Ask your admin.</Alert>
  const current = tabs.find((t) => t.value === tab)!

  const rowActions = (item: ListItem | Category, name: string) => (
    <span className="flex shrink-0 gap-1">
      <Button variant="ghost" icon={<Pencil aria-hidden className="size-4" />} onClick={() => setEditing({ kind: 'rename', item })} aria-label={`Rename ${name}`}>
        Rename
      </Button>
      <Button
        variant="ghost"
        icon={item.isActive ? <EyeOff aria-hidden className="size-4" /> : <Eye aria-hidden className="size-4" />}
        onClick={() => toggle.mutate({ item })}
        aria-label={item.isActive ? `Hide ${name}` : `Show ${name} again`}
      >
        {item.isActive ? 'Hide' : 'Show'}
      </Button>
    </span>
  )

  return (
    <>
      <PageHeader
        title="Lists"
        description="The choices people pick from on contracts and suppliers."
        help={help.lists}
        action={<ExcelButtons kind="lists" canImport invalidate={[['lists']]} />}
      />
      <div className="flex flex-col gap-4 md:flex-row md:items-start">
      {/* Which list: a short menu beside it on wider screens, a drop-down on a phone. */}
      <div className="md:hidden">
        <Field label="List">
          {(p) => (
            <Select {...p} value={tab} onChange={(e) => setTab(e.target.value as Tab)}>
              {(['Contracts', 'Suppliers'] as const).map((g) => (
                <optgroup key={g} label={g}>
                  {tabs.filter((t) => t.group === g).map((t) => (
                    <option key={t.value} value={t.value}>
                      {t.label}
                    </option>
                  ))}
                </optgroup>
              ))}
            </Select>
          )}
        </Field>
      </div>
      <nav aria-label="Lists" className="hidden w-60 shrink-0 flex-col gap-4 md:flex">
        {(['Contracts', 'Suppliers'] as const).map((g) => (
          <div key={g} className="flex flex-col gap-1">
            <p className="px-3 text-xs font-semibold tracking-wide text-muted-foreground uppercase">{g}</p>
            {tabs.filter((t) => t.group === g).map((t) => (
              <button
                key={t.value}
                type="button"
                aria-current={tab === t.value ? 'true' : undefined}
                onClick={() => setTab(t.value)}
                className={cn(
                  'min-h-11 rounded-[var(--radius-control)] px-3 text-left hover:bg-muted',
                  tab === t.value && 'bg-primary-soft font-medium text-primary-soft-foreground hover:bg-primary-soft',
                )}
              >
                {t.label}
              </button>
            ))}
          </div>
        ))}
      </nav>
      <div className="min-w-0 flex-1">

      {lists.isPending ? (
        <Skeleton className="h-48 w-full" />
      ) : lists.isError ? (
        <Alert tone="danger" title="We couldn't load the lists.">{(lists.error as Error).message}</Alert>
      ) : (
        <Card className="p-5 md:p-6">
          <div className="flex items-center justify-between gap-3">
            <h2 className="font-display text-lg font-semibold">{current.label}</h2>
            <Button variant="primary" icon={<Plus aria-hidden className="size-4" />} onClick={() => setEditing({ kind: 'add', parent: null })}>
              Add {current.single}
            </Button>
          </div>
          {current.note && <p className="mt-2 text-sm text-muted-foreground">{current.note}</p>}
          {tab === 'Category' ? (
            <ul className="mt-4 divide-y divide-border">
              {lists.data.categories.filter((c) => c.parentId === null).length === 0 && <li className="py-6 text-muted-foreground">No categories yet. Add your first one, for example Freight.</li>}
              {lists.data.categories
                .filter((c) => c.parentId === null)
                .map((top) => (
                  <li key={top.id} className="py-3">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <span className="font-medium">
                        {top.name} {!top.isActive && <Badge>Hidden</Badge>}
                      </span>
                      <span className="flex flex-wrap gap-1">
                        <Button variant="ghost" icon={<Plus aria-hidden className="size-4" />} onClick={() => setEditing({ kind: 'add', parent: top })}>
                          Subcategory
                        </Button>
                        {rowActions(top, top.name)}
                      </span>
                    </div>
                    <ul className="mt-1 ml-5 flex flex-col">
                      {lists.data.categories
                        .filter((s) => s.parentId === top.id)
                        .map((sub) => (
                          <li key={sub.id} className="flex flex-wrap items-center justify-between gap-2 border-l border-border py-1 pl-3">
                            <span>
                              {sub.name} {!sub.isActive && <Badge>Hidden</Badge>}
                            </span>
                            {rowActions(sub, sub.name)}
                          </li>
                        ))}
                    </ul>
                  </li>
                ))}
            </ul>
          ) : (
            <ul className="mt-4 divide-y divide-border">
              {lists.data.items.filter((i) => i.listName === tab).length === 0 && <li className="py-6 text-muted-foreground">Nothing here yet. Add the first {current.single}.</li>}
              {lists.data.items
                .filter((i) => i.listName === tab)
                .map((i) => (
                  <li key={i.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                    <span>
                      {i.value} {!i.isActive && <Badge>Hidden</Badge>}
                    </span>
                    {rowActions(i, i.value)}
                  </li>
                ))}
            </ul>
          )}
        </Card>
      )}
      </div>
      </div>
      {editing && <ListItemDialog tab={tab} single={current.single} editing={editing} onClose={() => setEditing(null)} />}
    </>
  )
}

function ListItemDialog({ tab, single, editing, onClose }: { tab: Tab; single: string; editing: Editing; onClose: () => void }) {
  const queryClient = useQueryClient()
  const initial = editing.kind === 'rename' ? ('listName' in editing.item ? editing.item.value : editing.item.name) : ''
  const [name, setName] = useState(initial)
  const [error, setError] = useState<string | undefined>()
  const save = useMutation({
    mutationFn: () => {
      if (editing.kind === 'rename') {
        return 'listName' in editing.item ? api.put(`/api/lists/items/${editing.item.id}`, { value: name }) : api.put(`/api/categories/${editing.item.id}`, { name })
      }
      return tab === 'Category' ? api.post('/api/categories', { name, parentId: editing.parent?.id ?? null }) : api.post('/api/lists/items', { listName: tab, value: name })
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['lists'] })
      toast.success(editing.kind === 'rename' ? 'Renamed.' : `${name.trim()} added.`)
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? (Object.values(e.fields)[0] ?? e.message) : 'That didn\'t work. Try again.'),
  })
  const title =
    editing.kind === 'rename' ? `Rename ${initial}` : editing.parent ? `Add a subcategory under ${editing.parent.name}` : `Add ${/^[aeiou]/i.test(single) ? 'an' : 'a'} ${single}`
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={title}
      description={editing.kind === 'rename' ? 'Contracts that use it show the new name.' : undefined}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>
            {editing.kind === 'rename' ? 'Save' : 'Add'}
          </Button>
        </>
      }
    >
      <form
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate()
        }}
      >
        <Field label="Name" required error={error} help={tab === 'Currency' ? 'The three-letter code, for example AUD.' : undefined}>
          {(p) => <TextInput {...p} autoFocus value={name} onChange={(e) => setName(e.target.value)} maxLength={100} />}
        </Field>
      </form>
    </Dialog>
  )
}
