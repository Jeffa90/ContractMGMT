// Add a contract, or edit a draft: one column, short titled sections (product-spec section 10), a sticky save bar.
// Slice 2 will draw these sections and fields from the admin's field layout; the built-in fields here are its starting point.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, Plus } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { Select, Textarea } from '@/components/form'
import { Alert, Button, Card, Checkbox, Field, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import type { ContractDetail, Lists, Supplier } from '@/lib/contracts'
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

function Section({ title, description, children }: { title: string; description?: string; children: React.ReactNode }) {
  return (
    <Card className="p-5 md:p-6">
      <h2 className="font-display text-lg font-semibold">{title}</h2>
      {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
      <div className="mt-4 flex flex-col gap-4">{children}</div>
    </Card>
  )
}

export function ContractFormPage() {
  const { id } = useParams()
  const editing = id !== undefined
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })
  const detail = useQuery({ queryKey: ['contract', Number(id)], queryFn: () => api.get<ContractDetail>(`/api/contracts/${id}`), enabled: editing })

  if (lists.isPending || suppliers.isPending || (editing && detail.isPending)) {
    return (
      <div className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full max-w-3xl" />
      </div>
    )
  }
  if (lists.isError || suppliers.isError || detail.isError) {
    const error = (lists.error ?? suppliers.error ?? detail.error) as Error
    return <Alert tone="danger" title="We couldn't open the form.">{error.message}</Alert>
  }
  return <ContractForm lists={lists.data} suppliers={suppliers.data} detail={editing ? detail.data! : null} />
}

function ContractForm({ lists, suppliers, detail }: { lists: Lists; suppliers: Supplier[]; detail: ContractDetail | null }) {
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
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const [addingSupplier, setAddingSupplier] = useState(false)
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }))

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

  const listSelect = (key: 'agreementTypeId' | 'businessUnitId' | 'siteId', listName: string, label: string, empty: string) => {
    const items = options(listName, form[key])
    return (
      <Field label={label} error={errors[key]} help={items.length === 0 ? `${empty} An admin adds them under Admin, Lists.` : undefined}>
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
      {problem && <Alert tone="danger" title={problem} />}

      <Section title="Basics">
        <Field label="Title" required error={errors.title} help='For example "Freight services 2026".'>
          {(p) => <TextInput {...p} value={form.title} onChange={(e) => set('title', e.target.value)} maxLength={200} autoFocus={!c} />}
        </Field>
        <Field label="Supplier" error={errors.supplierId}>
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
              <Button type="button" icon={<Plus aria-hidden className="size-4" />} onClick={() => setAddingSupplier(true)} className="shrink-0">
                Add new supplier
              </Button>
            </div>
          )}
        </Field>
        <Field label="Category" error={errors.categoryId} help={categories.length === 0 ? 'No categories yet. An admin adds them under Admin, Lists.' : undefined}>
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
        {listSelect('agreementTypeId', 'AgreementType', 'Agreement type', 'No agreement types yet.')}
        <Field label="Description" error={errors.description}>
          {(p) => <Textarea {...p} value={form.description} onChange={(e) => set('description', e.target.value)} maxLength={4000} />}
        </Field>
      </Section>

      <Section title="Owner and access" description="The owner and backup get the reminders and see it under My contracts. Any contract owner can still edit it.">
        <Field label="Owner" required error={errors.ownerId}>
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
        <Field label="Backup owner" error={errors.backupOwnerId}>
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
        <Checkbox
          label="Confidential"
          description="Only the owner and backup, Legal, approvers, admins and auditors can see it."
          checked={form.isConfidential}
          onChange={(v) => set('isConfidential', v)}
        />
        {listSelect('businessUnitId', 'BusinessUnit', 'Business unit', 'No business units yet.')}
        {listSelect('siteId', 'Site', 'Site', 'No sites yet.')}
      </Section>

      <Section title="Value">
        <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
          <Field label="Value" error={errors.value} help="The estimated total value. Numbers only.">
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
        <Field label="SAP outline agreement number" error={errors.sapOutlineAgreement}>
          {(p) => <TextInput {...p} value={form.sapOutlineAgreement} onChange={(e) => set('sapOutlineAgreement', e.target.value)} maxLength={50} />}
        </Field>
      </Section>

      <Section title="Dates and notice" description="The notice deadline is worked out for you: the end date (or review date, if evergreen) minus the notice period.">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Start date" error={errors.startDate}>
            {(p) => <TextInput {...p} type="date" value={form.startDate} onChange={(e) => set('startDate', e.target.value)} />}
          </Field>
          <Field label="Effective date" error={errors.effectiveDate}>
            {(p) => <TextInput {...p} type="date" value={form.effectiveDate} onChange={(e) => set('effectiveDate', e.target.value)} />}
          </Field>
          <Field label="End date" error={errors.endDate} help={form.isEvergreen ? 'Evergreen contracts have no end date.' : undefined}>
            {(p) => <TextInput {...p} type="date" value={form.isEvergreen ? '' : form.endDate} disabled={form.isEvergreen} onChange={(e) => set('endDate', e.target.value)} />}
          </Field>
          <Field label="Review date" error={errors.reviewDate}>
            {(p) => <TextInput {...p} type="date" value={form.reviewDate} onChange={(e) => set('reviewDate', e.target.value)} />}
          </Field>
        </div>
        <Checkbox label="Evergreen (no end date)" description="It carries on until someone ends it." checked={form.isEvergreen} onChange={(v) => set('isEvergreen', v)} />
        <Field label="Notice period (days)" error={errors.noticePeriodDays} help="How many days' notice must be given before it ends or renews.">
          {(p) => <TextInput {...p} inputMode="numeric" className="tabular sm:w-40" value={form.noticePeriodDays} onChange={(e) => set('noticePeriodDays', e.target.value.replace(/\D/g, ''))} />}
        </Field>
      </Section>

      <Section title="Legal">
        <Checkbox
          label="Needs Legal review"
          description="On by default. Untick only if Legal has agreed it doesn't need a review."
          checked={form.needsLegalReview}
          onChange={(v) => set('needsLegalReview', v)}
        />
      </Section>

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
