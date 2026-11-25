// Admin, Lists: categories (with subcategories), agreement types, business units, sites and currencies.
// Nothing is deleted: an item is hidden and stays on contracts that already use it.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Eye, EyeOff, Pencil, Plus } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { ExcelButtons } from '@/components/ExcelImport'
import { PageHeader } from '@/components/Help'
import { Alert, Badge, Button, Card, Field, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import type { Category, ListItem, Lists } from '@/lib/contracts'
import { useMe } from '@/lib/me'
import { help } from '@/help'

type Tab = 'Category' | 'AgreementType' | 'BusinessUnit' | 'Site' | 'Currency'
const tabs: { value: Tab; label: string; single: string }[] = [
  { value: 'Category', label: 'Categories', single: 'category' },
  { value: 'AgreementType', label: 'Agreement types', single: 'agreement type' },
  { value: 'BusinessUnit', label: 'Business units', single: 'business unit' },
  { value: 'Site', label: 'Sites', single: 'site' },
  { value: 'Currency', label: 'Currencies', single: 'currency' },
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
        description="The choices people pick from on a contract."
        help={help.lists}
        action={<ExcelButtons kind="lists" canImport invalidate={[['lists']]} />}
      />
      <div className="overflow-x-auto">
        <Segmented label="List" value={tab} onChange={setTab} options={tabs.map((t) => ({ value: t.value, label: t.label }))} />
      </div>

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
