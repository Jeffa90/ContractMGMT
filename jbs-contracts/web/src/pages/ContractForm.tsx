// Add a contract, or edit a draft: one column, short titled sections, a sticky save bar.
// Sections and fields come from the layout an admin arranges (Admin, Contract fields): built-in fields keep their own controls
// but take their label, help, section, order and visibility from it; added fields are drawn by FieldInput.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, Plus } from 'lucide-react'
import { useMemo, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { FieldInput } from '@/components/FieldInput'
import { Select, Textarea } from '@/components/form'
import { Alert, Button, Card, Checkbox, Field, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import type { ContractDetail, Lists, Supplier } from '@/lib/contracts'
import { arrange, type FieldValue, type Layout, type LayoutField } from '@/lib/layout'
import { useMe } from '@/lib/me'

type FormState = {
  title: string
  description: string
  supplierId: string
  categoryId: string
  agreementTypeId: string
  ownerId: string
  backupOwnerId: string
  isConfidential: boolean
  businessUnitId: string
  siteId: string
  value: string
  currencyCode: string
  sapOutlineAgreement: string
  startDate: string
  effectiveDate: string
  endDate: string
  isEvergreen: boolean
  noticePeriodDays: string
  reviewDate: string
  needsLegalReview: boolean
}

const toId = (v: string) => (v ? Number(v) : null)
const toText = (v: number | null | undefined) => (v === null || v === undefined ? '' : String(v))

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card className="p-5 md:p-6">
      <h2 className="font-display text-lg font-semibold">{title}</h2>
      <div className="mt-4 flex flex-col gap-4">{children}</div>
    </Card>
  )
}

export function ContractFormPage() {
  const { id } = useParams()
  const editing = id !== undefined
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })
  const layout = useQuery({ queryKey: ['layout'], queryFn: () => api.get<Layout>('/api/layout') })
  const people = useQuery({ queryKey: ['people-picker'], queryFn: () => api.get<{ id: number; name: string }[]>('/api/people/picker') })
  const detail = useQuery({ queryKey: ['contract', Number(id)], queryFn: () => api.get<ContractDetail>(`/api/contracts/${id}`), enabled: editing })

  if (lists.isPending || suppliers.isPending || layout.isPending || people.isPending || (editing && detail.isPending)) {
    return (
      <div className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full max-w-3xl" />
      </div>
    )
  }
  const error = lists.error ?? suppliers.error ?? layout.error ?? people.error ?? detail.error
  if (error) return <Alert tone="danger" title="We couldn't open the form.">{(error as Error).message}</Alert>
  return <ContractForm lists={lists.data!} suppliers={suppliers.data!} layout={layout.data!} people={people.data!} detail={editing ? detail.data! : null} />
}

export function ContractForm({
  lists,
  suppliers,
  layout,
  people,
  detail,
  preview,
}: {
  lists: Lists
  suppliers: Supplier[]
  layout: Layout
  people: { id: number; name: string }[]
  detail: ContractDetail | null
  /** Admin, Contract fields: shows the form as people will see it; nothing is saved. */
  preview?: boolean
}) {
  const { person } = useMe()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const c = detail?.contract
  const [form, setForm] = useState<FormState>(() => ({
    title: c?.title ?? '',
    description: c?.description ?? '',
    supplierId: toText(c?.supplierId),
    categoryId: toText(c?.categoryId),
    agreementTypeId: toText(c?.agreementTypeId),
    ownerId: toText(c?.ownerId ?? person.id),
    backupOwnerId: toText(c?.backupOwnerId),
    isConfidential: c?.isConfidential ?? false,
    businessUnitId: toText(c?.businessUnitId),
    siteId: toText(c?.siteId),
    value: toText(c?.value),
    currencyCode: c?.currencyCode ?? 'AUD',
    sapOutlineAgreement: c?.sapOutlineAgreement ?? '',
    startDate: c?.startDate ?? '',
    effectiveDate: c?.effectiveDate ?? '',
    endDate: c?.endDate ?? '',
    isEvergreen: c?.isEvergreen ?? false,
    noticePeriodDays: toText(c?.noticePeriodDays),
    reviewDate: c?.reviewDate ?? '',
    needsLegalReview: c?.needsLegalReview ?? true,
  }))
  const [extra, setExtra] = useState<Record<string, FieldValue>>(() =>
    Object.fromEntries(Object.entries(detail?.fieldValues ?? {}).map(([k, v]) => [k, v.value])),
  )
  const [touched, setTouched] = useState<Set<string>>(new Set())
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const [addingSupplier, setAddingSupplier] = useState(false)
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }))
  const setField = (key: string, value: FieldValue) => {
    setExtra((x) => ({ ...x, [key]: value }))
    setTouched((t) => new Set(t).add(key))
  }

  // Pickers offer items in use, plus whatever this contract already has (a hidden item stays on contracts that use it).
  const keep = (id: number, current: string) => String(id) === current
  const options = (listName: string, current: string) => lists.items.filter((i) => i.listName === listName && (i.isActive || keep(i.id, current)))
  const categories = useMemo(() => {
    const tops = lists.categories.filter((x) => x.parentId === null && (x.isActive || keep(x.id, form.categoryId)))
    return tops.map((top) => ({ top, subs: lists.categories.filter((x) => x.parentId === top.id && (x.isActive || keep(x.id, form.categoryId))) }))
  }, [lists.categories, form.categoryId])
  const activeSuppliers = suppliers.filter((s) => s.isActive || keep(s.id, form.supplierId))
  const owners = lists.owners

  const save = useMutation({
    mutationFn: () => {
      // Only added fields someone changed are sent, so a hidden field's value is never touched.
      const fields = Object.fromEntries([...touched].map((k) => [k, extra[k] === '' ? null : extra[k]]))
      const body = {
        title: form.title,
        description: form.description || null,
        supplierId: toId(form.supplierId),
        categoryId: toId(form.categoryId),
        agreementTypeId: toId(form.agreementTypeId),
        ownerId: toId(form.ownerId),
        backupOwnerId: toId(form.backupOwnerId),
        isConfidential: form.isConfidential,
        businessUnitId: toId(form.businessUnitId),
        siteId: toId(form.siteId),
        value: form.value.trim() === '' ? null : Number(form.value.replace(/[$,\s]/g, '')),
        currencyCode: form.currencyCode,
        sapOutlineAgreement: form.sapOutlineAgreement || null,
        startDate: form.startDate || null,
        effectiveDate: form.effectiveDate || null,
        endDate: form.isEvergreen ? null : form.endDate || null,
        isEvergreen: form.isEvergreen,
        noticePeriodDays: form.noticePeriodDays.trim() === '' ? null : Number(form.noticePeriodDays),
        reviewDate: form.reviewDate || null,
        needsLegalReview: form.needsLegalReview,
        rowVersion: c?.rowVersion,
        fields,
      }
      return c ? api.put<{ id: number }>(`/api/contracts/${c.id}`, body) : api.post<{ id: number; reference: string }>('/api/contracts', body)
    },
    onSuccess: (saved) => {
      queryClient.invalidateQueries({ queryKey: ['contracts'] })
      queryClient.invalidateQueries({ queryKey: ['contract', saved.id] })
      toast.success(c ? 'Changes saved.' : 'Draft saved. Add documents next.')
      navigate(`/contracts/${saved.id}`)
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        setErrors(error.fields)
        setProblem(error.message)
      } else setProblem('Something went wrong. Try again.')
      window.scrollTo({ top: 0, behavior: 'smooth' })
    },
  })

  const listSelect = (key: 'agreementTypeId' | 'businessUnitId' | 'siteId', listName: string, f: LayoutField, empty: string) => {
    const items = options(listName, form[key])
    return (
      <Field label={f.label} required={f.isRequired} error={errors[key]} help={items.length === 0 ? `${empty} An admin adds them under Admin, Lists.` : (f.help ?? undefined)}>
        {(p) => (
          <Select {...p} value={form[key]} onChange={(e) => set(key, e.target.value)} disabled={items.length === 0}>
            <option value="">Not chosen</option>
            {items.map((i) => (
              <option key={i.id} value={i.id}>
                {i.value}
                {!i.isActive ? ' (hidden)' : ''}
              </option>
            ))}
          </Select>
        )}
      </Field>
    )
  }

  /** The control for a built-in field, labelled from the layout. */
  const builtIn = (f: LayoutField): ReactNode => {
    const help = f.help ?? undefined
    switch (f.key) {
      case 'title':
        return (
          <Field label={f.label} required error={errors.title} help={help}>
            {(p) => <TextInput {...p} value={form.title} onChange={(e) => set('title', e.target.value)} maxLength={200} autoFocus={!c && !preview} />}
          </Field>
        )
      case 'supplier':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.supplierId} help={help}>
            {(p) => (
              <div className="flex flex-col gap-2 sm:flex-row">
                <Select {...p} value={form.supplierId} onChange={(e) => set('supplierId', e.target.value)}>
                  <option value="">Not chosen</option>
                  {activeSuppliers.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                </Select>
                <Button type="button" icon={<Plus aria-hidden className="size-4" />} onClick={() => setAddingSupplier(true)} className="shrink-0" disabled={preview}>
                  Add new supplier
                </Button>
              </div>
            )}
          </Field>
        )
      case 'category':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.categoryId} help={categories.length === 0 ? 'No categories yet. An admin adds them under Admin, Lists.' : help}>
            {(p) => (
              <Select {...p} value={form.categoryId} onChange={(e) => set('categoryId', e.target.value)} disabled={categories.length === 0}>
                <option value="">Not chosen</option>
                {categories.map(({ top, subs }) => (
                  <optgroup key={top.id} label={top.name}>
                    <option value={top.id}>{top.name} (all)</option>
                    {subs.map((s) => (
                      <option key={s.id} value={s.id}>
                        {top.name} › {s.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </Select>
            )}
          </Field>
        )
      case 'agreementType':
        return listSelect('agreementTypeId', 'AgreementType', f, 'No agreement types yet.')
      case 'businessUnit':
        return listSelect('businessUnitId', 'BusinessUnit', f, 'No business units yet.')
      case 'site':
        return listSelect('siteId', 'Site', f, 'No sites yet.')
      case 'description':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.description} help={help}>
            {(p) => <Textarea {...p} value={form.description} onChange={(e) => set('description', e.target.value)} maxLength={4000} />}
          </Field>
        )
      case 'owner':
        return (
          <Field label={f.label} required error={errors.ownerId} help={help ?? 'The owner and backup get the reminders and see it under My contracts. Any contract owner can still edit it.'}>
            {(p) => (
              <Select {...p} value={form.ownerId} onChange={(e) => set('ownerId', e.target.value)}>
                {owners.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        )
      case 'backupOwner':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.backupOwnerId} help={help}>
            {(p) => (
              <Select {...p} value={form.backupOwnerId} onChange={(e) => set('backupOwnerId', e.target.value)}>
                <option value="">No backup</option>
                {owners
                  .filter((o) => String(o.id) !== form.ownerId)
                  .map((o) => (
                    <option key={o.id} value={o.id}>
                      {o.name}
                    </option>
                  ))}
              </Select>
            )}
          </Field>
        )
      case 'confidential':
        return <Checkbox label={f.label} description={help} checked={form.isConfidential} onChange={(v) => set('isConfidential', v)} />
      case 'value':
        return (
          <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
            <Field label={f.label} required={f.isRequired} error={errors.value} help={help ?? 'Numbers only.'}>
              {(p) => <TextInput {...p} inputMode="decimal" className="tabular" value={form.value} onChange={(e) => set('value', e.target.value)} />}
            </Field>
            <Field label="Currency" error={errors.currencyCode}>
              {(p) => (
                <Select {...p} value={form.currencyCode} onChange={(e) => set('currencyCode', e.target.value)}>
                  {options('Currency', form.currencyCode).map((i) => (
                    <option key={i.id} value={i.value}>
                      {i.value}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
          </div>
        )
      case 'sapOutlineAgreement':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.sapOutlineAgreement} help={help}>
            {(p) => <TextInput {...p} value={form.sapOutlineAgreement} onChange={(e) => set('sapOutlineAgreement', e.target.value)} maxLength={50} />}
          </Field>
        )
      case 'startDate':
      case 'effectiveDate':
      case 'reviewDate': {
        const k = f.key as 'startDate' | 'effectiveDate' | 'reviewDate'
        return (
          <Field label={f.label} required={f.isRequired} error={errors[k]} help={help}>
            {(p) => <TextInput {...p} type="date" className="sm:w-60" value={form[k]} onChange={(e) => set(k, e.target.value)} />}
          </Field>
        )
      }
      case 'endDate':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.endDate} help={form.isEvergreen ? 'Evergreen contracts have no end date.' : help}>
            {(p) => <TextInput {...p} type="date" className="sm:w-60" value={form.isEvergreen ? '' : form.endDate} disabled={form.isEvergreen} onChange={(e) => set('endDate', e.target.value)} />}
          </Field>
        )
      case 'evergreen':
        return <Checkbox label={f.label} description={help} checked={form.isEvergreen} onChange={(v) => set('isEvergreen', v)} />
      case 'noticePeriodDays':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.noticePeriodDays} help={help}>
            {(p) => <TextInput {...p} inputMode="numeric" className="tabular sm:w-40" value={form.noticePeriodDays} onChange={(e) => set('noticePeriodDays', e.target.value.replace(/\D/g, ''))} />}
          </Field>
        )
      case 'needsLegalReview':
        return <Checkbox label={f.label} description={help} checked={form.needsLegalReview} onChange={(v) => set('needsLegalReview', v)} />
      default:
        return null
    }
  }

  const picks = { people, suppliers: activeSuppliers.map((s) => ({ id: s.id, name: s.name })) }
  const groups = arrange(layout)

  const body = (
    <>
      {problem && <Alert tone="danger" title={problem} />}
      {groups.map(({ section, fields }) => (
        <Section key={section.key} title={section.title}>
          {fields.map((f) => (
            <div key={f.key}>
              {f.isBuiltIn ? (
                builtIn(f)
              ) : (
                <FieldInput field={f} value={extra[f.key] ?? null} onChange={(v) => setField(f.key, v)} error={errors[`field.${f.key}`]} picks={picks} />
              )}
            </div>
          ))}
        </Section>
      ))}
    </>
  )

  if (preview) return <div className="flex flex-col gap-5">{body}</div>

  return (
    <form
      className="flex max-w-3xl flex-col gap-5 pb-24"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      <Link to={c ? `/contracts/${c.id}` : '/contracts'} className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline">
        <ChevronLeft aria-hidden className="size-4" /> {c ? c.title : 'Contracts'}
      </Link>
      <header>
        <h1 className="font-display text-xl font-semibold md:text-2xl">{c ? `Edit ${c.reference}` : 'Add a contract'}</h1>
        <p className="mt-1 text-muted-foreground">
          {c ? 'Change the details while it is a draft.' : 'Only a title is needed to save a draft. Fill in the rest as you go.'}
        </p>
      </header>
      {body}

      <div className="fixed inset-x-0 bottom-0 z-20 border-t border-border bg-card/95 px-4 py-3 backdrop-blur md:left-auto md:w-[calc(100%-16rem)]">
        <div className="mx-auto flex max-w-3xl justify-end gap-2">
          <Button type="button" onClick={() => navigate(c ? `/contracts/${c.id}` : '/contracts')}>
            Cancel
          </Button>
          <Button type="submit" variant="primary" loading={save.isPending}>
            {c ? 'Save changes' : 'Save draft'}
          </Button>
        </div>
      </div>

      {addingSupplier && (
        <SupplierDialog
          supplier={null}
          onClose={() => setAddingSupplier(false)}
          onSaved={(s) => {
            set('supplierId', String(s.id))
            setAddingSupplier(false)
          }}
        />
      )}
    </form>
  )
}

/** Add or edit a supplier. Used in place from the contract form ("Add new supplier") and from the Suppliers page. */
export function SupplierDialog({ supplier, onClose, onSaved }: { supplier: Supplier | null; onClose: () => void; onSaved: (s: Supplier) => void }) {
  const { person } = useMe()
  const queryClient = useQueryClient()
  const [form, setForm] = useState({ name: supplier?.name ?? '', abn: supplier?.abn ?? '', vendorNumber: supplier?.vendorNumber ?? '', notes: supplier?.notes ?? '' })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (isActive?: boolean) => {
      const body = { ...form, isActive: isActive ?? supplier?.isActive ?? true, rowVersion: supplier?.rowVersion }
      return supplier ? api.put<Supplier>(`/api/suppliers/${supplier.id}`, body) : api.post<Supplier>('/api/suppliers', body)
    },
    onSuccess: (saved) => {
      queryClient.invalidateQueries({ queryKey: ['suppliers'] })
      toast.success(supplier ? `${saved.name} saved.` : `${saved.name} added.`)
      onSaved(saved)
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        setErrors(error.fields)
        setProblem(Object.keys(error.fields).length ? null : error.message)
      } else setProblem('Something went wrong. Try again.')
    },
  })
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={supplier ? supplier.name : 'Add a supplier'}
      footer={
        <>
          {supplier && person.roles.admin && (
            <Button variant="ghost" className="sm:mr-auto" onClick={() => save.mutate(!supplier.isActive)}>
              {supplier.isActive ? 'Hide supplier' : 'Show supplier again'}
            </Button>
          )}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={save.isPending} onClick={() => save.mutate(undefined)}>
            {supplier ? 'Save' : 'Add supplier'}
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate(undefined)
        }}
      >
        {problem && <Alert tone="danger" title={problem} />}
        <Field label="Name" required error={errors.name}>
          {(p) => <TextInput {...p} autoFocus value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />}
        </Field>
        <Field label="ABN" error={errors.abn} help="11 digits. Spaces are fine.">
          {(p) => <TextInput {...p} inputMode="numeric" value={form.abn} onChange={(e) => setForm({ ...form, abn: e.target.value })} />}
        </Field>
        <Field label="SAP vendor number" error={errors.vendorNumber}>
          {(p) => <TextInput {...p} value={form.vendorNumber} onChange={(e) => setForm({ ...form, vendorNumber: e.target.value })} />}
        </Field>
        <Field label="Notes" error={errors.notes}>
          {(p) => <Textarea {...p} value={form.notes} onChange={(e) => setForm({ ...form, notes: e.target.value })} />}
        </Field>
        <button type="submit" hidden />
      </form>
    </Dialog>
  )
}
