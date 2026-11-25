// Admin, Contract fields (slice 2; decisions D-15 to D-18): add, rename, move, hide and require fields and sections without code.
// Changes are made on a working copy with a live preview, then saved together; every saved version can be put back.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Eye, EyeOff, History, Lock, Pencil, Plus, RotateCcw } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { Select, Textarea } from '@/components/form'
import { PageHeader } from '@/components/Help'
import { Alert, Badge, Button, Card, Checkbox, Field, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import type { Lists, Supplier } from '@/lib/contracts'
import { formatDateTime } from '@/lib/format'
import { addedKinds, type ChoiceOption, type FieldKind, type Layout, type LayoutField, type LayoutSection } from '@/lib/layout'
import { useMe } from '@/lib/me'
import { help } from '@/help'
import { ContractForm } from './ContractForm'

type Draft = { versionId: number; sections: LayoutSection[]; fields: LayoutField[] }
type HistoryItem = { id: number; savedUtc: string; savedByName: string; summary: string }

let tempKey = 0
const nextKey = (prefix: string) => `${prefix}-new-${++tempKey}`

function move<T>(items: T[], index: number, by: number): T[] {
  const to = index + by
  if (to < 0 || to >= items.length) return items
  const copy = [...items]
  const [item] = copy.splice(index, 1)
  copy.splice(to, 0, item)
  return copy
}

export function ContractFieldsPage() {
  const { person } = useMe()
  const layout = useQuery({ queryKey: ['layout'], queryFn: () => api.get<Layout>('/api/layout') })
  if (!person.roles.admin) return <Alert tone="warning" title="Only admins can change the contract fields.">Ask your admin.</Alert>
  if (layout.isPending) return <Skeleton className="h-96 w-full" />
  if (layout.isError) return <Alert tone="danger" title="We couldn't load the fields.">{(layout.error as Error).message}</Alert>
  return <Designer key={layout.data.versionId} saved={layout.data} />
}

function Designer({ saved }: { saved: Layout }) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<Draft>(() => structuredClone(saved))
  const [editing, setEditing] = useState<{ field: LayoutField | null; sectionKey: string } | null>(null)
  const [renaming, setRenaming] = useState<LayoutSection | 'new' | null>(null)
  const [showHistory, setShowHistory] = useState(false)
  const [problems, setProblems] = useState<string[]>([])
  const [view, setView] = useState<'edit' | 'preview'>('edit')
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })
  const people = useQuery({ queryKey: ['people-picker'], queryFn: () => api.get<{ id: number; name: string }[]>('/api/people/picker') })

  const unsaved = useMemo(() => JSON.stringify(draft) !== JSON.stringify(saved), [draft, saved])
  useEffect(() => {
    if (!unsaved) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [unsaved])

  const save = useMutation({
    mutationFn: () => {
      // Fields in section order, then their order within each section.
      const ordered = draft.sections.flatMap((s) => draft.fields.filter((f) => f.sectionKey === s.key))
      return api.put<Layout>('/api/layout', {
        versionId: draft.versionId,
        sections: draft.sections.map((s) => ({ key: s.key, title: s.title, isHidden: s.isHidden })),
        fields: ordered.map((f) => ({
          key: f.key.includes('-new-') ? null : f.key,
          kind: f.kind,
          label: f.label,
          help: f.help,
          sectionKey: f.sectionKey,
          isRequired: f.isRequired,
          isHidden: f.isHidden,
          showInList: f.showInList,
          filterable: f.filterable,
          inExport: f.inExport,
          afterEffective: f.afterEffective,
          choices: f.choices,
          maxLength: f.maxLength,
          minValue: f.minValue,
          maxValue: f.maxValue,
        })),
      })
    },
    onSuccess: (layout) => {
      setProblems([])
      queryClient.setQueryData(['layout'], layout)
      queryClient.invalidateQueries({ queryKey: ['layout'] })
      toast.success('Contract fields saved. Everyone sees the new layout now.')
    },
    onError: (e) => {
      if (e instanceof ApiError) setProblems(Object.keys(e.fields).length ? Object.values(e.fields) : [e.message])
      else setProblems(['Something went wrong. Try again.'])
      window.scrollTo({ top: 0, behavior: 'smooth' })
    },
  })

  const updateField = (key: string, change: Partial<LayoutField>) => setDraft((d) => ({ ...d, fields: d.fields.map((f) => (f.key === key ? { ...f, ...change } : f)) }))
  const moveField = (field: LayoutField, by: number) =>
    setDraft((d) => {
      const inSection = d.fields.filter((f) => f.sectionKey === field.sectionKey)
      const moved = move(inSection, inSection.indexOf(field), by)
      return { ...d, fields: [...d.fields.filter((f) => f.sectionKey !== field.sectionKey), ...moved] }
    })
  const moveSection = (index: number, by: number) => setDraft((d) => ({ ...d, sections: move(d.sections, index, by) }))

  const previewLayout: Layout = { ...draft }
  const ready = lists.data && suppliers.data && people.data

  return (
    <>
      <PageHeader
        title="Contract fields"
        description="What people fill in on a contract, and where. Changes show in the preview straight away; save to make them live."
        help={help.fields}
        action={
          <div className="flex flex-wrap gap-2">
            <Button variant="ghost" icon={<History aria-hidden className="size-4" />} onClick={() => setShowHistory(true)}>
              History
            </Button>
            <Button variant="primary" onClick={() => save.mutate()} loading={save.isPending} disabled={!unsaved}>
              Save changes
            </Button>
          </div>
        }
      />
      {unsaved && (
        <Alert tone="info" title="You have changes that aren't saved yet.">
          They only show in the preview until you click Save changes.{' '}
          <Button variant="link" onClick={() => setDraft(structuredClone(saved))}>
            Undo all changes
          </Button>
        </Alert>
      )}
      {problems.length > 0 && (
        <Alert tone="danger" title="Some changes can't be saved yet.">
          <ul className="ml-5 list-disc">
            {problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        </Alert>
      )}
      <div className="lg:hidden">
        <Segmented label="Show" value={view} onChange={setView} options={[{ value: 'edit', label: 'Arrange' }, { value: 'preview', label: 'Preview' }]} />
      </div>

      <div className="grid items-start gap-6 lg:grid-cols-2">
        <div className={view === 'preview' ? 'hidden lg:flex lg:flex-col lg:gap-4' : 'flex flex-col gap-4'}>
          {draft.sections.map((section, si) => {
            const fields = draft.fields.filter((f) => f.sectionKey === section.key)
            return (
              <Card key={section.key} className={section.isHidden ? 'p-4 opacity-70' : 'p-4'}>
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <h2 className="font-display text-lg font-semibold">
                    {section.title} {section.isHidden && <Badge>Hidden</Badge>}
                  </h2>
                  <div className="flex gap-1">
                    <Button variant="ghost" aria-label={`Move section ${section.title} up`} onClick={() => moveSection(si, -1)} disabled={si === 0} className="px-3">
                      <ArrowUp aria-hidden className="size-4" />
                    </Button>
                    <Button variant="ghost" aria-label={`Move section ${section.title} down`} onClick={() => moveSection(si, 1)} disabled={si === draft.sections.length - 1} className="px-3">
                      <ArrowDown aria-hidden className="size-4" />
                    </Button>
                    <Button variant="ghost" aria-label={`Rename section ${section.title}`} onClick={() => setRenaming(section)} className="px-3">
                      <Pencil aria-hidden className="size-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      className="px-3"
                      aria-label={section.isHidden ? `Show section ${section.title}` : `Hide section ${section.title}`}
                      disabled={!section.isHidden && fields.some((f) => f.isRuleField)}
                      title={!section.isHidden && fields.some((f) => f.isRuleField) ? 'This section has fields the rules need. Move them out first.' : undefined}
                      onClick={() => setDraft((d) => ({ ...d, sections: d.sections.map((s) => (s.key === section.key ? { ...s, isHidden: !s.isHidden } : s)) }))}
                    >
                      {section.isHidden ? <Eye aria-hidden className="size-4" /> : <EyeOff aria-hidden className="size-4" />}
                    </Button>
                  </div>
                </div>
                <ul className="mt-2 divide-y divide-border">
                  {fields.map((f, fi) => (
                    <li key={f.key} className={f.isHidden ? 'flex flex-wrap items-center gap-2 py-2 opacity-60' : 'flex flex-wrap items-center gap-2 py-2'}>
                      <span className="min-w-0 flex-1">
                        <span className="font-medium">{f.label}</span>
                        <span className="mt-0.5 flex flex-wrap gap-1">
                          <Badge tone={f.isBuiltIn ? 'neutral' : 'brand'}>{f.isBuiltIn ? 'Built in' : f.kindName}</Badge>
                          {f.isRequired && <Badge tone="info">Required</Badge>}
                          {f.isRuleField && <Badge icon={<Lock aria-hidden className="size-3" />}>Needed by the rules</Badge>}
                          {f.showInList && <Badge>List column</Badge>}
                          {f.isHidden && <Badge tone="warning">Removed</Badge>}
                        </span>
                      </span>
                      <span className="flex gap-1">
                        <Button variant="ghost" className="px-3" aria-label={`Move ${f.label} up`} onClick={() => moveField(f, -1)} disabled={fi === 0}>
                          <ArrowUp aria-hidden className="size-4" />
                        </Button>
                        <Button variant="ghost" className="px-3" aria-label={`Move ${f.label} down`} onClick={() => moveField(f, 1)} disabled={fi === fields.length - 1}>
                          <ArrowDown aria-hidden className="size-4" />
                        </Button>
                        <Button variant="ghost" className="px-3" aria-label={`Edit ${f.label}`} onClick={() => setEditing({ field: f, sectionKey: f.sectionKey })}>
                          <Pencil aria-hidden className="size-4" />
                        </Button>
                        {!f.isRuleField && (
                          <Button variant="ghost" className="px-3" aria-label={f.isHidden ? `Bring back ${f.label}` : `Remove ${f.label}`} onClick={() => updateField(f.key, { isHidden: !f.isHidden })}>
                            {f.isHidden ? <RotateCcw aria-hidden className="size-4" /> : <EyeOff aria-hidden className="size-4" />}
                          </Button>
                        )}
                      </span>
                    </li>
                  ))}
                  {fields.length === 0 && <li className="py-3 text-sm text-muted-foreground">No fields in this section yet.</li>}
                </ul>
                <Button variant="ghost" icon={<Plus aria-hidden className="size-4" />} onClick={() => setEditing({ field: null, sectionKey: section.key })} className="mt-1">
                  Add field
                </Button>
              </Card>
            )
          })}
          <Button variant="secondary" icon={<Plus aria-hidden className="size-4" />} onClick={() => setRenaming('new')}>
            Add section
          </Button>
        </div>

        <div className={view === 'edit' ? 'hidden lg:block' : 'block'}>
          <div className="lg:sticky lg:top-6">
            <p className="mb-2 text-sm font-medium text-muted-foreground">Preview: the Add contract form as people will see it</p>
            <div className="max-h-[80dvh] overflow-y-auto rounded-[var(--radius-card)] border border-dashed border-input bg-background p-3" aria-label="Preview">
              {ready ? (
                <ContractForm key={JSON.stringify(previewLayout)} lists={lists.data!} suppliers={suppliers.data!} people={people.data!} layout={previewLayout} detail={null} preview />
              ) : (
                <Skeleton className="h-64 w-full" />
              )}
            </div>
          </div>
        </div>
      </div>

      {editing && (
        <FieldDialog
          draft={draft}
          field={editing.field}
          sectionKey={editing.sectionKey}
          onClose={() => setEditing(null)}
          onSave={(f) => {
            setDraft((d) => {
              const exists = d.fields.some((x) => x.key === f.key)
              if (!exists) return { ...d, fields: [...d.fields, f] }
              const before = d.fields.find((x) => x.key === f.key)!
              // Moving to another section puts it at the end of that section.
              if (before.sectionKey !== f.sectionKey) return { ...d, fields: [...d.fields.filter((x) => x.key !== f.key), f] }
              return { ...d, fields: d.fields.map((x) => (x.key === f.key ? f : x)) }
            })
            setEditing(null)
          }}
        />
      )}
      {renaming && (
        <SectionDialog
          section={renaming === 'new' ? null : renaming}
          onClose={() => setRenaming(null)}
          onSave={(title) => {
            setDraft((d) =>
              renaming === 'new'
                ? { ...d, sections: [...d.sections, { key: nextKey('s'), title, isHidden: false }] }
                : { ...d, sections: d.sections.map((s) => (s.key === renaming.key ? { ...s, title } : s)) },
            )
            setRenaming(null)
          }}
        />
      )}
      {showHistory && <HistoryDialog unsaved={unsaved} onClose={() => setShowHistory(false)} />}
    </>
  )
}

function SectionDialog({ section, onClose, onSave }: { section: LayoutSection | null; onClose: () => void; onSave: (title: string) => void }) {
  const [title, setTitle] = useState(section?.title ?? '')
  const [error, setError] = useState<string>()
  const done = () => (title.trim() ? onSave(title.trim()) : setError('Give the section a title.'))
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={section ? `Rename ${section.title}` : 'Add a section'}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={done}>
            {section ? 'Rename' : 'Add section'}
          </Button>
        </>
      }
    >
      <form
        onSubmit={(e) => {
          e.preventDefault()
          done()
        }}
      >
        <Field label="Title" required error={error} help="For example: Insurance.">
          {(p) => <TextInput {...p} autoFocus value={title} maxLength={100} onChange={(e) => setTitle(e.target.value)} />}
        </Field>
      </form>
    </Dialog>
  )
}

function FieldDialog({ draft, field, sectionKey, onClose, onSave }: { draft: Draft; field: LayoutField | null; sectionKey: string; onClose: () => void; onSave: (f: LayoutField) => void }) {
  const [f, setF] = useState<LayoutField>(
    () =>
      field ?? {
        key: nextKey('f'),
        isBuiltIn: false,
        isRuleField: false,
        kind: 'ShortText',
        kindName: 'Short text',
        label: '',
        help: null,
        sectionKey,
        isRequired: false,
        isHidden: false,
        showInList: false,
        filterable: false,
        inExport: true,
        afterEffective: 'Variation',
        choices: [],
        maxLength: null,
        minValue: null,
        maxValue: null,
        hasValues: false,
      },
  )
  const [error, setError] = useState<string>()
  const set = (change: Partial<LayoutField>) => setF((x) => ({ ...x, ...change }))
  const isChoice = f.kind === 'Choice' || f.kind === 'MultiChoice'
  const isNumber = f.kind === 'WholeNumber' || f.kind === 'Decimal' || f.kind === 'Money'
  const done = () => {
    const label = f.label.trim()
    if (!label) return setError('Give the field a label.')
    if (draft.fields.some((x) => x.key !== f.key && x.label.trim().toLowerCase() === label.toLowerCase())) return setError(`There's already a field called ${label}.`)
    if (isChoice && f.choices.filter((c) => !c.hidden && c.label.trim()).length === 0) return setError('Add at least one option to choose from.')
    onSave({ ...f, label, kindName: addedKinds.find((k) => k.kind === f.kind)?.label ?? f.kindName, choices: f.choices.filter((c) => c.label.trim()) })
  }
  const setChoice = (i: number, change: Partial<ChoiceOption>) => set({ choices: f.choices.map((c, j) => (j === i ? { ...c, ...change } : c)) })

  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={field ? `Edit ${field.label}` : 'Add a field'}
      description={field?.isBuiltIn ? 'A built-in field: you can rename it, move it and change its help.' : undefined}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={done}>
            {field ? 'Done' : 'Add field'}
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          done()
        }}
      >
        {error && <Alert tone="danger" title={error} />}
        <Field label="Label" required help="What people see. It's also the column heading in Excel.">
          {(p) => <TextInput {...p} autoFocus value={f.label} maxLength={100} onChange={(e) => set({ label: e.target.value })} />}
        </Field>
        {!f.isBuiltIn && (
          <Field label="Kind of field" help={f.hasValues ? 'Contracts already have values in this field, so its kind can\'t change. Add a new field instead.' : addedKinds.find((k) => k.kind === f.kind)?.description}>
            {(p) => (
              <Select {...p} value={f.kind} disabled={f.hasValues} onChange={(e) => set({ kind: e.target.value as FieldKind })}>
                {addedKinds.map((k) => (
                  <option key={k.kind} value={k.kind}>
                    {k.label}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        )}
        {isChoice && (
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-1 text-sm font-medium">Options</legend>
            {f.choices.map((c, i) => (
              <div key={c.key || i} className="flex items-center gap-2">
                <TextInput aria-label={`Option ${i + 1}`} value={c.label} onChange={(e) => setChoice(i, { label: e.target.value })} className={c.hidden ? 'opacity-60' : undefined} />
                <Button variant="ghost" className="shrink-0" onClick={() => setChoice(i, { hidden: !c.hidden })}>
                  {c.hidden ? 'Show' : 'Hide'}
                </Button>
              </div>
            ))}
            <Button variant="ghost" icon={<Plus aria-hidden className="size-4" />} onClick={() => set({ choices: [...f.choices, { key: '', label: '', hidden: false }] })} className="self-start">
              Add option
            </Button>
            <p className="text-xs text-muted-foreground">Hiding an option takes it out of the choices; contracts that already use it keep it.</p>
          </fieldset>
        )}
        <Field label="Help" help="A short hint shown under the field. Optional.">
          {(p) => <Textarea {...p} value={f.help ?? ''} maxLength={300} onChange={(e) => set({ help: e.target.value || null })} className="min-h-16" />}
        </Field>
        <Field label="Section">
          {(p) => (
            <Select {...p} value={f.sectionKey} onChange={(e) => set({ sectionKey: e.target.value })}>
              {draft.sections.map((s) => (
                <option key={s.key} value={s.key}>
                  {s.title}
                  {s.isHidden ? ' (hidden)' : ''}
                </option>
              ))}
            </Select>
          )}
        </Field>
        {(f.kind === 'ShortText' || f.kind === 'LongText') && (
          <Field label="Longest answer (characters)" help="Optional.">
            {(p) => <TextInput {...p} inputMode="numeric" className="sm:w-40" value={f.maxLength ?? ''} onChange={(e) => set({ maxLength: e.target.value ? Number(e.target.value.replace(/\D/g, '')) : null })} />}
          </Field>
        )}
        {isNumber && (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Smallest allowed" help="Optional.">
              {(p) => <TextInput {...p} inputMode="decimal" value={f.minValue ?? ''} onChange={(e) => set({ minValue: e.target.value === '' ? null : Number(e.target.value) })} />}
            </Field>
            <Field label="Largest allowed" help="Optional.">
              {(p) => <TextInput {...p} inputMode="decimal" value={f.maxValue ?? ''} onChange={(e) => set({ maxValue: e.target.value === '' ? null : Number(e.target.value) })} />}
            </Field>
          </div>
        )}
        <fieldset className="flex flex-col">
          <legend className="mb-1 text-sm font-medium">Settings</legend>
          <Checkbox
            label="Required"
            description={f.isRuleField && f.isRequired ? 'The contract rules need this field.' : 'Checked when a contract leaves Draft, so drafts can stay incomplete.'}
            checked={f.isRequired}
            disabled={f.isRuleField && f.isRequired}
            onChange={(v) => set({ isRequired: v })}
          />
          <Checkbox label="Offer as a column in the Contracts list" checked={f.showInList} onChange={(v) => set({ showInList: v })} />
          <Checkbox label="Can be used to filter contracts" checked={f.filterable} onChange={(v) => set({ filterable: v })} />
          <Checkbox label="Include in Excel exports and imports" checked={f.inExport} onChange={(v) => set({ inExport: v })} />
        </fieldset>
        {!f.isBuiltIn && (
          <fieldset className="flex flex-col gap-1">
            <legend className="mb-1 text-sm font-medium">Once a contract is Effective (locked)</legend>
            {[
              { value: 'Variation', label: 'Change only through a Variation', description: 'Like the contract dates and value.' },
              { value: 'AnyTime', label: 'Can be updated any time', description: 'For things like a site contact or a SharePoint link.' },
            ].map((o) => (
              <label key={o.value} className="flex min-h-11 items-start gap-3 py-1">
                <input type="radio" name="afterEffective" className="mt-1 size-5 accent-[var(--primary)]" checked={f.afterEffective === o.value} onChange={() => set({ afterEffective: o.value as LayoutField['afterEffective'] })} />
                <span className="flex flex-col">
                  <span className="font-medium">{o.label}</span>
                  <span className="text-xs text-muted-foreground">{o.description}</span>
                </span>
              </label>
            ))}
          </fieldset>
        )}
        <button type="submit" hidden />
      </form>
    </Dialog>
  )
}

function HistoryDialog({ unsaved, onClose }: { unsaved: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const history = useQuery({ queryKey: ['layout-history'], queryFn: () => api.get<HistoryItem[]>('/api/layout/history') })
  const restore = useMutation({
    mutationFn: (id: number) => api.post<Layout>(`/api/layout/history/${id}/restore`, {}),
    onSuccess: (layout, id) => {
      queryClient.setQueryData(['layout'], layout)
      queryClient.invalidateQueries({ queryKey: ['layout'] })
      queryClient.invalidateQueries({ queryKey: ['layout-history'] })
      toast.success(`Version ${id} put back. Values entered since are kept.`)
      onClose()
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : 'That didn\'t work. Try again.'),
  })
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()} title="History of the contract fields" description="Every saved version. Putting one back saves it as a new version; values entered since are kept." footer={<Button onClick={onClose}>Close</Button>}>
      {unsaved && <Alert tone="warning" title="You have unsaved changes. Putting back a version throws them away." />}
      {history.isPending ? (
        <Skeleton className="h-32 w-full" />
      ) : history.data && history.data.length > 0 ? (
        <ol className="flex flex-col divide-y divide-border">
          {history.data.map((h, i) => (
            <li key={h.id} className="flex flex-col gap-1 py-3">
              <span className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-medium">
                  Version {h.id}
                  {i === 0 && <span className="ml-2 text-xs text-muted-foreground">(current)</span>}
                </span>
                {i > 0 && (
                  <Button variant="ghost" icon={<RotateCcw aria-hidden className="size-4" />} loading={restore.isPending && restore.variables === h.id} onClick={() => restore.mutate(h.id)}>
                    Put back
                  </Button>
                )}
              </span>
              <span className="text-sm">{h.summary}</span>
              <span className="text-xs text-muted-foreground">
                {h.savedByName}, {formatDateTime(h.savedUtc)}
              </span>
            </li>
          ))}
        </ol>
      ) : (
        <p className="text-sm text-muted-foreground">No saved changes yet. The first time you save, the starting layout is kept here too.</p>
      )}
    </Dialog>
  )
}
