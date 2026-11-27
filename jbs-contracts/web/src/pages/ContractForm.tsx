// Add a contract, or edit a draft: one column, short titled sections, a sticky save bar.
// Sections and fields come from the layout an admin arranges (Admin, Contract fields): built-in fields keep their own controls
// but take their label, help, section, order and visibility from it; added fields are drawn by FieldInput.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BackLink } from '@/components/BackLink'
import { Plus, X } from 'lucide-react'
import { useMemo, useState, type ReactNode } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { FieldInput } from '@/components/FieldInput'
import { MultiSelect } from '@/components/MultiSelect'
import { CategoryPicker } from '@/components/CategoryPicker'
import { Select, Textarea } from '@/components/form'
import { Alert, Button, Card, Checkbox, Field, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import { categoryPaths, suggestedTitle, type Category, type ContractDetail, type Lists, type Supplier } from '@/lib/contracts'
import { arrange, type FieldValue, type Layout, type LayoutField } from '@/lib/layout'
import { useMe } from '@/lib/me'
import { formatDate } from '@/lib/format'

/** A yyyy-mm-dd date moved by whole months, keeping month ends (31 Mar less 1 month is 28 or 29 Feb), as the server does. */
function addMonths(iso: string, months: number): string {
  const [y, m, d] = iso.split('-').map(Number)
  const first = new Date(Date.UTC(y, m - 1 + months, 1))
  const last = new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth() + 1, 0)).getUTCDate()
  return new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth(), Math.min(d, last))).toISOString().slice(0, 10)
}

type FormState = {
  title: string
  description: string
  supplierId: string
  categoryId: string
  /** Other categories besides the main one (D-115). */
  otherCategoryIds: number[]
  agreementTypeId: string
  ownerId: string
  backupOwnerId: string
  isConfidential: boolean
  businessUnitIds: number[]
  siteIds: number[]
  entityIds: number[]
  functionIds: number[]
  value: string
  currencyCode: string
  sapOutlineAgreement: string
  startDate: string
  effectiveDate: string
  endDate: string
  isEvergreen: boolean
  autoRenews: boolean
  continuesUntilReplaced: boolean
  renewalTermMonths: string
  noticePeriodDays: string
  reviewDate: string
  /** '' none, 'date' a date, or a number of months before the end date (D-105). */
  reviewWhen: string
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
  const lists = useQuery({
    queryKey: ['lists'],
    queryFn: () => api.get<Lists>('/api/lists'),
  })
  const suppliers = useQuery({
    queryKey: ['suppliers'],
    queryFn: () => api.get<Supplier[]>('/api/suppliers'),
  })
  const layout = useQuery({
    queryKey: ['layout'],
    queryFn: () => api.get<Layout>('/api/layout'),
  })
  const people = useQuery({
    queryKey: ['people-picker'],
    queryFn: () => api.get<{ id: number; name: string }[]>('/api/people/picker'),
  })
  const detail = useQuery({
    queryKey: ['contract', Number(id)],
    queryFn: () => api.get<ContractDetail>(`/api/contracts/${id}`),
    enabled: editing,
  })

  if (lists.isPending || suppliers.isPending || layout.isPending || people.isPending || (editing && detail.isPending)) {
    return (
      <div className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full max-w-3xl" />
      </div>
    )
  }
  const error = lists.error ?? suppliers.error ?? layout.error ?? people.error ?? detail.error
  if (error)
    return (
      <Alert tone="danger" title="We couldn't open the form.">
        {(error as Error).message}
      </Alert>
    )
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
  const location = useLocation()
  const queryClient = useQueryClient()
  const c = detail?.contract
  const [form, setForm] = useState<FormState>(() => ({
    title: c?.title ?? '',
    description: c?.description ?? '',
    supplierId: toText(c?.supplierId),
    categoryId: toText(c?.categoryId),
    otherCategoryIds: c?.otherCategoryIds ?? [],
    agreementTypeId: toText(c?.agreementTypeId),
    ownerId: toText(c?.ownerId ?? person.id),
    backupOwnerId: toText(c?.backupOwnerId),
    isConfidential: c?.isConfidential ?? false,
    businessUnitIds: c?.businessUnitIds ?? [],
    siteIds: c?.siteIds ?? [],
    entityIds: c?.entityIds ?? [],
    functionIds: c?.functionIds ?? [],
    value: toText(c?.value),
    currencyCode: c?.currencyCode ?? 'AUD',
    sapOutlineAgreement: c?.sapOutlineAgreement ?? '',
    startDate: c?.startDate ?? '',
    effectiveDate: c?.effectiveDate ?? '',
    endDate: c?.endDate ?? '',
    isEvergreen: c?.isEvergreen ?? false,
    autoRenews: c?.autoRenews ?? false,
    continuesUntilReplaced: c?.continuesUntilReplaced ?? false,
    renewalTermMonths: toText(c?.renewalTermMonths),
    noticePeriodDays: toText(c?.noticePeriodDays),
    reviewDate: c?.reviewMonthsBeforeEnd ? '' : (c?.reviewDate ?? ''),
    reviewWhen: c?.reviewMonthsBeforeEnd ? String(c.reviewMonthsBeforeEnd) : c?.reviewDate ? 'date' : '',
    needsLegalReview: c?.needsLegalReview ?? true,
  }))
  const [extra, setExtra] = useState<Record<string, FieldValue>>(() =>
    Object.fromEntries(Object.entries(detail?.fieldValues ?? {}).map(([k, v]) => [k, v.value])),
  )
  // The suggested title (D-131) fills the title until someone types their own; a draft keeps following it while its title still matches.
  const [titleAuto, setTitleAuto] = useState(
    () => !c || c.title === suggestedTitle(c.supplierName, c.agreementTypeName, c.categoryId ?? null, lists.categories),
  )
  const [touched, setTouched] = useState<Set<string>>(new Set())
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const [addingSupplier, setAddingSupplier] = useState(false)
  const [addingCategory, setAddingCategory] = useState(false)
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }))
  const setField = (key: string, value: FieldValue) => {
    setExtra((x) => ({ ...x, [key]: value }))
    setTouched((t) => new Set(t).add(key))
  }

  // Pickers offer items in use, plus whatever this contract already has (a hidden item stays on contracts that use it).
  const keep = (id: number, current: string) => String(id) === current
  const options = (listName: string, current: string) => lists.items.filter((i) => i.listName === listName && (i.isActive || keep(i.id, current)))
  const paths = useMemo(() => new Map(categoryPaths(lists.categories).map((p) => [p.id, p.path])), [lists.categories])
  const activeSuppliers = suppliers.filter((s) => s.isActive || keep(s.id, form.supplierId))
  const suggestion = suggestedTitle(
    suppliers.find((s) => String(s.id) === form.supplierId)?.name,
    lists.items.find((i) => String(i.id) === form.agreementTypeId)?.value,
    toId(form.categoryId),
    lists.categories,
  )
  // While the admin has titles made from the details (D-132), it's always the suggestion; the server makes it the same way.
  const titleLocked = layout.titleFromDetails === true
  const title = titleLocked || titleAuto ? suggestion : form.title
  const owners = lists.owners

  const save = useMutation({
    mutationFn: () => {
      // Only added fields someone changed are sent, so a hidden field's value is never touched.
      const fields = Object.fromEntries([...touched].map((k) => [k, extra[k] === '' ? null : extra[k]]))
      const body = {
        title,
        description: form.description || null,
        supplierId: toId(form.supplierId),
        categoryId: toId(form.categoryId),
        otherCategoryIds: form.otherCategoryIds,
        agreementTypeId: toId(form.agreementTypeId),
        ownerId: toId(form.ownerId),
        backupOwnerId: toId(form.backupOwnerId),
        isConfidential: form.isConfidential,
        businessUnitIds: form.businessUnitIds,
        siteIds: form.siteIds,
        entityIds: form.entityIds,
        functionIds: form.functionIds,
        value: form.value.trim() === '' ? null : Number(form.value.replace(/[$,\s]/g, '')),
        currencyCode: form.currencyCode,
        sapOutlineAgreement: form.sapOutlineAgreement || null,
        startDate: form.startDate || null,
        effectiveDate: form.effectiveDate || null,
        endDate: form.isEvergreen ? null : form.endDate || null,
        isEvergreen: form.isEvergreen,
        autoRenews: !form.isEvergreen && form.autoRenews,
        continuesUntilReplaced: !form.isEvergreen && !form.autoRenews && form.continuesUntilReplaced,
        renewalTermMonths: !form.isEvergreen && form.autoRenews && form.renewalTermMonths.trim() !== '' ? Number(form.renewalTermMonths) : null,
        noticePeriodDays: form.noticePeriodDays.trim() === '' ? null : Number(form.noticePeriodDays),
        reviewDate: form.reviewWhen === 'date' ? form.reviewDate || null : null,
        reviewMonthsBeforeEnd: !form.isEvergreen && /^\d+$/.test(form.reviewWhen) ? Number(form.reviewWhen) : null,
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
      // Back from the contract shouldn't open the form again: an edit returns to where it came from; a new draft replaces the form.
      if (c && location.key !== 'default') navigate(-1)
      else navigate(`/contracts/${saved.id}`, { replace: true })
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        setErrors(error.fields)
        setProblem(error.message)
      } else setProblem('Something went wrong. Try again.')
      window.scrollTo({ top: 0, behavior: 'smooth' })
    },
  })

  // Several values each (D-114): a drop-down that stays open while you tick.
  const listMulti = (key: 'businessUnitIds' | 'siteIds' | 'entityIds' | 'functionIds', listName: string, f: LayoutField, empty: string) => {
    const items = lists.items.filter((i) => i.listName === listName && (i.isActive || form[key].includes(i.id)))
    return (
      <Field
        label={f.label}
        required={f.isRequired}
        error={errors[key]}
        help={items.length === 0 ? `${empty} An admin adds them under Admin, Lists.` : (f.help ?? 'Choose one or more.')}
      >
        {(p) => (
          <MultiSelect
            {...p}
            options={items.map((i) => ({
              id: i.id,
              label: i.value,
              hidden: !i.isActive,
            }))}
            selected={form[key]}
            onChange={(ids) => set(key, ids)}
            disabled={items.length === 0}
          />
        )}
      </Field>
    )
  }

  const listSelect = (key: 'agreementTypeId', listName: string, f: LayoutField, empty: string) => {
    const items = options(listName, form[key])
    return (
      <Field
        label={f.label}
        required={f.isRequired}
        error={errors[key]}
        help={items.length === 0 ? `${empty} An admin adds them under Admin, Lists.` : (f.help ?? undefined)}
      >
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
          <div className="flex flex-col gap-1">
            <Field
              label={f.label}
              required
              error={errors.title}
              help={
                [
                  titleLocked &&
                    (suggestion
                      ? 'Made from the supplier, agreement type and category, so every title follows the same pattern.'
                      : 'Made from the supplier, agreement type and category once you choose them below (Supplier_Agreement type_What it covers).'),
                  !titleLocked &&
                    titleAuto &&
                    (suggestion
                      ? 'Filled in from the supplier, agreement type and category. Type over it to change it.'
                      : 'Filled in as Supplier_Agreement type_What it covers once you choose them below, or type your own.'),
                  !titleLocked && help, // the field's own help (for example how to word a title) doesn't apply while it's made for them
                ]
                  .filter(Boolean)
                  .join(' ') || undefined
              }
            >
              {(p) => (
                <TextInput
                  {...p}
                  value={title}
                  readOnly={titleLocked}
                  placeholder={titleLocked ? 'Filled in from the details below' : undefined}
                  className={titleLocked ? 'bg-muted' : undefined}
                  onChange={(e) => {
                    setTitleAuto(false)
                    set('title', e.target.value)
                  }}
                  maxLength={200}
                  autoFocus={!c && !preview && !titleLocked}
                />
              )}
            </Field>
            {!titleLocked && !titleAuto && suggestion && suggestion !== form.title && (
              <button type="button" className="self-start text-left text-sm text-primary hover:underline" onClick={() => setTitleAuto(true)}>
                Use the suggestion: {suggestion}
              </button>
            )}
          </div>
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
                <Button
                  type="button"
                  icon={<Plus aria-hidden className="size-4" />}
                  onClick={() => setAddingSupplier(true)}
                  className="shrink-0"
                  disabled={preview}
                >
                  Add new supplier
                </Button>
              </div>
            )}
          </Field>
        )
      case 'category':
        // The main category (its main spend) and any others (D-115).
        return (
          <div className="flex flex-col gap-4">
            <CategoryPicker
              categories={lists.categories}
              value={toId(form.categoryId)}
              onChange={(id) => set('categoryId', toText(id))}
              label={`${f.label} (main spend)`}
              required={f.isRequired}
              error={errors.categoryId}
              help={lists.categories.length === 0 ? 'No categories yet. An admin adds them under Admin, Lists.' : help}
            />
            <div className="flex flex-col gap-2">
              <p className="text-sm font-medium">Other categories</p>
              <p className="text-xs text-muted-foreground">
                If it also covers other spend, for example casings and premixes. The main category sets the supplier tier.
              </p>
              {form.otherCategoryIds.length > 0 && (
                <ul className="flex flex-wrap gap-2">
                  {form.otherCategoryIds.map((id) => (
                    <li key={id} className="flex items-center gap-1 rounded-full bg-muted py-1 pr-1 pl-3 text-sm">
                      {paths.get(id) ?? 'A removed category'}
                      <button
                        type="button"
                        className="flex size-8 items-center justify-center rounded-full hover:bg-card"
                        aria-label={`Remove ${paths.get(id) ?? 'this category'}`}
                        onClick={() =>
                          set(
                            'otherCategoryIds',
                            form.otherCategoryIds.filter((x) => x !== id),
                          )
                        }
                      >
                        <X aria-hidden className="size-4" />
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              {errors.otherCategoryIds && <p className="text-xs font-medium text-danger">{errors.otherCategoryIds}</p>}
              <div>
                <Button type="button" icon={<Plus aria-hidden className="size-4" />} onClick={() => setAddingCategory(true)} disabled={preview}>
                  Add another category
                </Button>
              </div>
            </div>
          </div>
        )
      case 'agreementType':
        return listSelect('agreementTypeId', 'AgreementType', f, 'No agreement types yet.')
      case 'businessUnit':
        return listMulti('businessUnitIds', 'BusinessUnit', f, 'No business units yet.')
      case 'site':
        return listMulti('siteIds', 'Site', f, 'No sites yet.')
      case 'entity':
        return listMulti('entityIds', 'Entity', f, 'No entities yet.')
      case 'function':
        return listMulti('functionIds', 'Function', f, 'No functions yet.')
      case 'description':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.description} help={help}>
            {(p) => <Textarea {...p} value={form.description} onChange={(e) => set('description', e.target.value)} maxLength={4000} />}
          </Field>
        )
      case 'owner':
        return (
          <Field
            label={f.label}
            required
            error={errors.ownerId}
            help={help ?? 'The owner and backup get the reminders and see it under My contracts. Any contract owner can still edit it.'}
          >
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
      case 'reviewDate': {
        // A period before the end date (it moves with the end date), or a date (D-105). Evergreen contracts have no end date.
        const periods = [...new Set([1, 3, 6, 12, ...(/^\d+$/.test(form.reviewWhen) ? [Number(form.reviewWhen)] : [])])].sort((a, b) => a - b)
        const when = form.isEvergreen && /^\d+$/.test(form.reviewWhen) ? '' : form.reviewWhen
        const months = /^\d+$/.test(when) ? Number(when) : null
        const worked =
          months === null
            ? undefined
            : form.endDate
              ? `Review on ${formatDate(addMonths(form.endDate, -months))}. It moves if the end date does.`
              : 'Worked out once there is an end date.'
        return (
          <div className="flex flex-col gap-3">
            <Field label={f.label} required={f.isRequired} error={errors.reviewDate} help={worked ?? help}>
              {(p) => (
                <Select {...p} className="sm:w-80" value={when} onChange={(e) => set('reviewWhen', e.target.value)}>
                  <option value="">No review date</option>
                  {!form.isEvergreen &&
                    periods.map((m) => (
                      <option key={m} value={String(m)}>
                        {m === 1 ? '1 month' : `${m} months`} before the end date
                      </option>
                    ))}
                  <option value="date">On a date</option>
                </Select>
              )}
            </Field>
            {when === 'date' && (
              <Field label="Review on">
                {(p) => <TextInput {...p} type="date" className="sm:w-60" value={form.reviewDate} onChange={(e) => set('reviewDate', e.target.value)} />}
              </Field>
            )}
          </div>
        )
      }
      case 'startDate':
      case 'effectiveDate': {
        const k = f.key as 'startDate' | 'effectiveDate'
        return (
          <Field label={f.label} required={f.isRequired} error={errors[k]} help={help}>
            {(p) => <TextInput {...p} type="date" className="sm:w-60" value={form[k]} onChange={(e) => set(k, e.target.value)} />}
          </Field>
        )
      }
      case 'endDate':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.endDate} help={form.isEvergreen ? 'Evergreen contracts have no end date.' : help}>
            {(p) => (
              <TextInput
                {...p}
                type="date"
                className="sm:w-60"
                value={form.isEvergreen ? '' : form.endDate}
                disabled={form.isEvergreen}
                onChange={(e) => set('endDate', e.target.value)}
              />
            )}
          </Field>
        )
      case 'evergreen':
        return <Checkbox label={f.label} description={help} checked={form.isEvergreen} onChange={(v) => set('isEvergreen', v)} />
      case 'autoRenews':
        return (
          <Checkbox
            label={f.label}
            description={form.isEvergreen ? 'Evergreen contracts have no end date to renew.' : help}
            checked={!form.isEvergreen && form.autoRenews}
            disabled={form.isEvergreen}
            onChange={(v) => {
              set('autoRenews', v)
              if (v) set('continuesUntilReplaced', false) // one or the other
            }}
          />
        )
      case 'continuesUntilReplaced':
        // Keeps its end date and carries on after it until replaced (D-121): not with Evergreen or Renews automatically.
        return (
          <Checkbox
            label={f.label}
            description={form.isEvergreen ? 'Evergreen contracts have no end date to carry on past.' : help}
            checked={!form.isEvergreen && form.continuesUntilReplaced}
            disabled={form.isEvergreen}
            onChange={(v) => {
              set('continuesUntilReplaced', v)
              if (v) set('autoRenews', false)
            }}
          />
        )
      case 'renewalTermMonths':
        // Only asked for when it renews automatically.
        if (form.isEvergreen || !form.autoRenews) return null
        return (
          <Field label={f.label} required error={errors.renewalTermMonths} help={help}>
            {(p) => (
              <TextInput
                {...p}
                inputMode="numeric"
                className="tabular sm:w-40"
                value={form.renewalTermMonths}
                onChange={(e) => set('renewalTermMonths', e.target.value.replace(/\D/g, '').slice(0, 3))}
              />
            )}
          </Field>
        )
      case 'noticePeriodDays':
        return (
          <Field label={f.label} required={f.isRequired} error={errors.noticePeriodDays} help={help}>
            {(p) => (
              <TextInput
                {...p}
                inputMode="numeric"
                className="tabular sm:w-40"
                value={form.noticePeriodDays}
                onChange={(e) => set('noticePeriodDays', e.target.value.replace(/\D/g, ''))}
              />
            )}
          </Field>
        )
      case 'needsLegalReview':
        return <Checkbox label={f.label} description={help} checked={form.needsLegalReview} onChange={(v) => set('needsLegalReview', v)} />
      default:
        return null
    }
  }

  const picks = {
    people,
    suppliers: activeSuppliers.map((s) => ({ id: s.id, name: s.name })),
  }
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
      <BackLink fallback={c ? `/contracts/${c.id}` : '/contracts'} />
      <header>
        <h1 className="font-display text-xl font-semibold md:text-2xl">{c ? `Edit ${c.reference}` : 'Add a contract'}</h1>
        <p className="mt-1 text-muted-foreground">
          {c
            ? 'Change the details while it is a draft.'
            : titleLocked
              ? 'Choose the supplier, agreement type and category: the title is made from them. Fill in the rest as you go.'
              : 'Only a title is needed to save a draft. Fill in the rest as you go.'}
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
      {addingCategory && (
        <AddCategoryDialog
          categories={lists.categories}
          taken={[toId(form.categoryId), ...form.otherCategoryIds]}
          onClose={() => setAddingCategory(false)}
          onAdd={(id) => {
            set('otherCategoryIds', [...form.otherCategoryIds, id])
            setAddingCategory(false)
          }}
        />
      )}
    </form>
  )
}

/** Adds one of a contract's other categories (D-115), picked the same way as the main one. */
function AddCategoryDialog({
  categories,
  taken,
  onClose,
  onAdd,
}: {
  categories: Category[]
  taken: (number | null)[]
  onClose: () => void
  onAdd: (id: number) => void
}) {
  const [id, setId] = useState<number | null>(null)
  const [error, setError] = useState<string | undefined>()
  const byId = new Map(categories.map((c) => [c.id, c]))
  const add = () => {
    if (id === null || byId.get(id)?.parentId === null) setError('Choose a category, and a subcategory if you like.')
    else if (taken.includes(id)) setError('The contract already has that one.')
    else onAdd(id)
  }
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Add another category"
      description="Type to find it, or choose its family, then its category."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={add}>
            Add
          </Button>
        </>
      }
    >
      <CategoryPicker
        categories={categories}
        value={id}
        onChange={(v) => {
          setId(v)
          setError(undefined)
        }}
        label="Category"
        required
        error={error}
      />
    </Dialog>
  )
}

/** Add or edit a supplier. Used in place from the contract form ("Add new supplier") and from the Suppliers page. */
export function SupplierDialog({ supplier, onClose, onSaved }: { supplier: Supplier | null; onClose: () => void; onSaved: (s: Supplier) => void }) {
  const { person } = useMe()
  const queryClient = useQueryClient()
  const [form, setForm] = useState({
    name: supplier?.name ?? '',
    abn: supplier?.abn ?? '',
    vendorNumber: supplier?.vendorNumber ?? '',
    notes: supplier?.notes ?? '',
  })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (isActive?: boolean) => {
      const body = {
        ...form,
        isActive: isActive ?? supplier?.isActive ?? true,
        rowVersion: supplier?.rowVersion,
      }
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
        <Field label="ABN (Australian suppliers)" error={errors.abn} help="11 digits; spaces are fine. Leave it blank for an overseas supplier, and put its own company number in Notes if you like.">
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
