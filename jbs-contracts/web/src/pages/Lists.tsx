// Admin, Lists: the choices on contracts (the spend taxonomy: families, categories and subcategories, agreement types, business units, sites, currencies, document
// kinds) and on suppliers (document types, tiers, risk levels).
// Nothing is deleted: an item is hidden and stays on contracts that already use it.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronRight, Eye, EyeOff, Pencil, Plus, Search } from 'lucide-react'
import { useMemo, useState } from 'react'
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

type Tab = 'Category' | 'AgreementType' | 'BusinessUnit' | 'Site' | 'Entity' | 'Function' | 'Currency' | 'SupplierDocumentType' | 'SupplierTier' | 'DocumentKind' | 'SupplierRisk'
const tabs: { value: Tab; label: string; single: string; group: 'Contracts' | 'Suppliers'; note?: string }[] = [
  {
    value: 'Category',
    group: 'Contracts',
    label: 'Categories',
    single: 'family',
    note: "Three levels: a family, its categories, and their subcategories. A contract picks a category (and a subcategory if you like). Give a family or category a supplier tier: a supplier's tier is then the highest tier among its current contracts' main categories, unless Procurement sets one by hand. A level without a tier follows the one above it. The higher a tier is in Supplier tiers, the higher it counts. To load a whole taxonomy, use Import from Excel.",
  },
  { value: 'AgreementType', group: 'Contracts', label: 'Agreement types', single: 'agreement type' },
  { value: 'BusinessUnit', group: 'Contracts', label: 'Business units', single: 'business unit' },
  { value: 'Site', group: 'Contracts', label: 'Sites', single: 'site' },
  { value: 'Entity', group: 'Contracts', label: 'Entities', single: 'entity', note: 'The JBS companies that sign contracts.' },
  { value: 'Function', group: 'Contracts', label: 'Functions', single: 'function', note: 'The parts of the business contracts are for.' },
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

type Editing = { kind: 'add'; parent: Category | null; level?: string } | { kind: 'rename'; item: ListItem | Category }
const levelName = ['family', 'category', 'subcategory'] as const

export function ListsPage() {
  const { person } = useMe()
  const queryClient = useQueryClient()
  const [tab, setTab] = useState<Tab>('Category')
  const [editing, setEditing] = useState<Editing | null>(null)
  // Families start folded (a taxonomy has hundreds of entries); Find opens whatever matches.
  const [open, setOpen] = useState<Set<number>>(new Set())
  const [find, setFind] = useState('')
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const setTier = useMutation({
    mutationFn: (x: { cat: Category; tierId: number | null }) => api.put(`/api/categories/${x.cat.id}/tier`, { tierId: x.tierId }),
    onSuccess: (_, x) => {
      queryClient.invalidateQueries({ queryKey: ['lists'] })
      queryClient.invalidateQueries({ queryKey: ['suppliers'] })
      toast.success(`Tier for ${x.cat.name} saved.`)
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : "That didn't save. Try again."),
  })
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

  const categories = useMemo(() => lists.data?.categories ?? [], [lists.data])
  // With Find: the ones whose name matches, and the levels above them (to show where they sit).
  const shown = useMemo(() => {
    const words = find.toLowerCase().split(/\s+/).filter(Boolean)
    if (words.length === 0) return null
    const byId = new Map(categories.map((c) => [c.id, c]))
    const keep = new Set<number>()
    for (const c of categories) {
      if (!words.every((w) => c.name.toLowerCase().includes(w))) continue
      for (let at: Category | undefined = c; at; at = at.parentId === null ? undefined : byId.get(at.parentId)) keep.add(at.id)
    }
    return keep
  }, [categories, find])
  if (!person.roles.admin) return <Alert tone="warning" title="Only admins can change the lists.">Ask your admin.</Alert>
  const current = tabs.find((t) => t.value === tab)!

  // The supplier tier each spend category gives (D-108): a supplier's tier is the highest among its current contracts' categories.
  const tiers = lists.data?.items.filter((i) => i.listName === 'SupplierTier' && i.isActive) ?? []
  const tierPick = (cat: Category, parent: Category | null) => (
    <Select
      aria-label={`Supplier tier for ${cat.name}`}
      className="h-9 min-h-9 w-44 text-sm"
      value={cat.tierId ? String(cat.tierId) : ''}
      onChange={(e) => setTier.mutate({ cat, tierId: e.target.value ? Number(e.target.value) : null })}
    >
      <option value="">{parent ? `Tier: as ${parent.name}` : 'Tier: none'}</option>
      {tiers.map((t) => (
        <option key={t.id} value={t.id}>
          Tier: {t.value}
        </option>
      ))}
    </Select>
  )

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
            <>
              <div className="relative mt-4">
                <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
                <TextInput aria-label="Find a family, category or subcategory" placeholder="Find: type a word" className="pl-9" value={find} onChange={(e) => setFind(e.target.value)} />
              </div>
              {shown?.size === 0 && <p className="py-6 text-muted-foreground">Nothing matches "{find}".</p>}
              <ul className="mt-2 divide-y divide-border">
                {categories.filter((c) => c.parentId === null).length === 0 && (
                  <li className="py-6 text-muted-foreground">No categories yet. Add your first family, for example Packaging, or load a whole taxonomy with Import from Excel.</li>
                )}
                {categories
                  .filter((c) => c.parentId === null && (!shown || shown.has(c.id)))
                  .map((family) => (
                    <CategoryNode
                      key={family.id}
                      node={family}
                      parent={null}
                      depth={1}
                      categories={categories}
                      shown={shown}
                      open={open}
                      setOpen={setOpen}
                      tierPick={tierPick}
                      rowActions={rowActions}
                      onAdd={(parent, level) => setEditing({ kind: 'add', parent, level })}
                    />
                  ))}
              </ul>
            </>
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
    editing.kind === 'rename'
      ? `Rename ${initial}`
      : editing.parent
        ? `Add a ${editing.level ?? 'subcategory'} in ${editing.parent.name}`
        : `Add ${/^[aeiou]/i.test(single) ? 'an' : 'a'} ${single}`
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

/** One family, category or subcategory, with the levels under it (D-115). Families and categories fold. */
function CategoryNode({
  node,
  parent,
  depth,
  categories,
  shown,
  open,
  setOpen,
  tierPick,
  rowActions,
  onAdd,
}: {
  node: Category
  parent: Category | null
  depth: 1 | 2 | 3
  categories: Category[]
  shown: Set<number> | null
  open: Set<number>
  setOpen: (s: Set<number>) => void
  tierPick: (cat: Category, parent: Category | null) => React.ReactNode
  rowActions: (item: Category, name: string) => React.ReactNode
  onAdd: (parent: Category, level: string) => void
}) {
  const children = depth < 3 ? categories.filter((c) => c.parentId === node.id && (!shown || shown.has(c.id))) : []
  const isOpen = shown !== null || open.has(node.id)
  const childLevel: string = levelName[Math.min(depth, 2)]
  return (
    <li className={cn(depth === 1 ? 'py-3' : 'border-l border-border py-1 pl-3')}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="flex min-w-0 items-center gap-1">
          {depth < 3 && (
            <Button
              variant="ghost"
              className="size-9 min-h-9 shrink-0 p-0"
              aria-expanded={isOpen}
              aria-label={`${isOpen ? 'Fold' : 'Show what is in'} ${node.name}`}
              disabled={shown !== null}
              onClick={() => {
                const next = new Set(open)
                if (next.has(node.id)) next.delete(node.id)
                else next.add(node.id)
                setOpen(next)
              }}
            >
              <ChevronRight aria-hidden className={cn('size-4 transition-transform', isOpen && 'rotate-90')} />
            </Button>
          )}
          <span className={cn('min-w-0 break-words', depth === 1 && 'font-medium')}>
            {node.name} {!node.isActive && <Badge>Hidden</Badge>}
            {depth < 3 && (
              <span className="text-xs text-muted-foreground">
                {' '}
                ({categories.filter((c) => c.parentId === node.id).length})
              </span>
            )}
          </span>
        </span>
        <span className="flex flex-wrap items-center gap-1">
          {tierPick(node, parent)}
          {depth < 3 && (
            <Button variant="ghost" icon={<Plus aria-hidden className="size-4" />} onClick={() => onAdd(node, childLevel)} aria-label={`Add a ${childLevel} in ${node.name}`}>
              {childLevel === 'category' ? 'Category' : 'Subcategory'}
            </Button>
          )}
          {rowActions(node, node.name)}
        </span>
      </div>
      {isOpen && children.length > 0 && (
        <ul className="mt-1 ml-5 flex flex-col">
          {children.map((c) => (
            <CategoryNode
              key={c.id}
              node={c}
              parent={node}
              depth={(depth + 1) as 2 | 3}
              categories={categories}
              shown={shown}
              open={open}
              setOpen={setOpen}
              tierPick={tierPick}
              rowActions={rowActions}
              onAdd={onAdd}
            />
          ))}
        </ul>
      )}
    </li>
  )
}
