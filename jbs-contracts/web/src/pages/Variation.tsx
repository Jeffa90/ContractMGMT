// A Variation of an Effective contract (product-spec section 7): what changes, old and new side by side, its Legal back-and-forth and
// its approval. Nothing changes on the contract until it's approved.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BackLink } from '@/components/BackLink'
import { ArrowLeftRight, BadgeCheck, Check, FileText, Paperclip, Send, Undo2, Upload, UserCheck, X } from 'lucide-react'
import { useRef, useState } from 'react'
import { useParams } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { DropTarget } from '@/components/DropZone'
import { FieldInput } from '@/components/FieldInput'
import { Textarea } from '@/components/form'
import { PageHeader } from '@/components/Help'
import { ConversationCard } from '@/components/LegalReview'
import { Alert, Badge, Button, Card, Checkbox, EmptyState, Field, Skeleton } from '@/components/ui'
import { errorText, fieldErrors } from '@/lib/actions'
import { api, ApiError } from '@/lib/api'
import { cn } from '@/lib/cn'
import { uploadDocument, type ContractDetail, type VariationAction, type VariationDetail } from '@/lib/contracts'
import { formatDateTime, timeAgo } from '@/lib/format'
import type { FieldValue, LayoutField } from '@/lib/layout'
import { help } from '@/help'

const steps = [
  { stage: 'Draft', label: 'Draft' },
  { stage: 'LegalReview', label: 'Legal review' },
  { stage: 'Approval', label: 'Approval' },
  { stage: 'Approved', label: 'Approved' },
] as const

export function VariationPage() {
  const { id, vid } = useParams()
  const query = useQuery({ queryKey: ['variation', Number(vid)], queryFn: () => api.get<VariationDetail>(`/api/contracts/${id}/variations/${vid}`) })
  if (query.isPending) return <Skeleton className="h-96 w-full" />
  if (query.isError) return <Alert tone={(query.error as ApiError).status === 404 ? 'warning' : 'danger'} title={(query.error as Error).message} />
  return <Variation data={query.data} />
}

function useVAction(data: VariationDetail, onDone?: () => void) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ path, body }: { path: string; body?: object; success: string }) =>
      api.post(`/api/contracts/${data.contract.id}/variations/${data.variation.id}/${path}`, { ...body, rowVersion: data.variation.rowVersion }),
    onSuccess: (_, v) => {
      queryClient.invalidateQueries({ queryKey: ['variation', data.variation.id] })
      queryClient.invalidateQueries({ queryKey: ['contract', data.contract.id] })
      queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success(v.success)
      onDone?.()
    },
  })
}

type Dialogs = 'sendToLegal' | 'sendToOwner' | 'clear' | 'sendForApproval' | 'approve' | 'sendBack' | 'withdraw' | null

function Variation({ data }: { data: VariationDetail }) {
  const v = data.variation
  const c = data.contract
  const [open, setOpen] = useState<Dialogs>(null)
  const take = useVAction(data)
  const has = (a: VariationAction) => data.actions.includes(a)
  const close = () => setOpen(null)
  const shown = steps.filter((s) => s.stage !== 'LegalReview' || v.needsLegalReview || v.legalClearedUtc)
  const current = v.stage === 'Withdrawn' ? -1 : shown.findIndex((s) => s.stage === v.stage)
  const primary = (['sendForApproval', 'approve', 'take', 'clear', 'sendToLegal'] as const).find(has)

  return (
    <>
      <BackLink fallback={`/contracts/${c.id}`} />
      <PageHeader title={`Variation ${v.number}`} description={`${c.title} · ${c.supplierName ?? 'No supplier'} · owner ${c.ownerName}`} help={help.variation} />

      {v.stage === 'Withdrawn' && <Alert tone="info" title="This Variation was withdrawn. Nothing changed on the contract.">{v.closedReason}</Alert>}
      {data.approvals[0]?.decision === 'SentBack' && v.stage === 'Draft' && (
        <Alert tone="warning" title={`${data.approvals[0].byName} sent it back, ${formatDateTime(data.approvals[0].atUtc)}`}>
          {data.approvals[0].reason}
        </Alert>
      )}

      <Card className="flex flex-col gap-4 p-5 md:p-6">
        {v.stage !== 'Withdrawn' && (
          <ol className="flex flex-col gap-2 sm:flex-row sm:items-center" aria-label="Where this Variation is up to">
            {shown.map((s, i) => (
              <li key={s.stage} className="flex items-center sm:flex-1" aria-current={i === current ? 'step' : undefined}>
                <span
                  className={cn(
                    'inline-flex size-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold',
                    i < current && 'border-primary bg-primary text-primary-foreground',
                    i === current && 'border-primary bg-primary-soft text-primary-soft-foreground',
                    i > current && 'border-border text-muted-foreground',
                  )}
                >
                  {i < current || v.stage === 'Approved' ? <Check aria-hidden className="size-4" /> : i + 1}
                </span>
                <span className={cn('ml-2 text-sm', i === current ? 'font-semibold' : 'text-muted-foreground')}>{s.label}</span>
                {i < shown.length - 1 && <span aria-hidden className="mx-3 hidden h-px flex-1 bg-border sm:block" />}
              </li>
            ))}
          </ol>
        )}
        <div className="flex flex-col gap-1 text-sm">
          <p>
            <strong>Why:</strong> {v.reason}
          </p>
          {v.stage === 'LegalReview' && (
            <p className="flex flex-wrap items-center gap-2">
              <Badge tone={v.whoseTurn === 'Legal' ? 'info' : 'warning'}>{v.whoseTurn === 'Legal' ? "Legal's turn" : "Owner's turn"}</Badge>
              {v.whoseTurn === 'Legal' ? (v.legalPersonName ? `${v.legalPersonName} has it` : 'New: nobody in Legal has picked it up yet') : `With ${c.ownerName} and the contract owners`}
              {v.turnSinceUtc && <span className="text-muted-foreground">since {timeAgo(v.turnSinceUtc)}</span>}
            </p>
          )}
          {v.legalOutcome && (
            <p>
              <strong>Cleared by Legal</strong> ({v.legalClearedByName}): {v.legalOutcome}
            </p>
          )}
          <p className="text-muted-foreground">
            {v.stage === 'Draft'
              ? v.needsLegalReview && !v.legalClearedUtc
                ? 'Next: choose what changes, then Send to Legal.'
                : 'Next: send it for approval. The Group Head approves it and the changes apply.'
              : v.stage === 'Approval'
                ? 'Waiting for the Group Head (or a backup approver). When approved, every change applies to the contract at once.'
                : v.stage === 'Approved'
                  ? 'Approved: the changes below now apply to the contract.'
                  : ''}
          </p>
          {data.cannotApproveReason && <p className="text-muted-foreground">{data.cannotApproveReason}</p>}
        </div>
        {data.actions.some((a) => a !== 'edit') && (
          <div className="flex flex-wrap gap-2">
            {has('sendToLegal') && (
              <Button variant={primary === 'sendToLegal' ? 'primary' : 'secondary'} icon={<Send aria-hidden className="size-4" />} onClick={() => setOpen('sendToLegal')}>
                {v.legalClearedUtc ? 'Send back to Legal' : 'Send to Legal'}
              </Button>
            )}
            {has('take') && (
              <Button
                variant={primary === 'take' ? 'primary' : 'secondary'}
                icon={<UserCheck aria-hidden className="size-4" />}
                loading={take.isPending}
                onClick={() => take.mutate({ path: 'legal/take', success: "It's yours." }, { onError: (e) => toast.error(errorText(e)) })}
              >
                I'll take it
              </Button>
            )}
            {has('clear') && (
              <Button variant={primary === 'clear' ? 'primary' : 'secondary'} icon={<Check aria-hidden className="size-4" />} onClick={() => setOpen('clear')}>
                Legal cleared
              </Button>
            )}
            {has('sendToOwner') && (
              <Button icon={<ArrowLeftRight aria-hidden className="size-4" />} onClick={() => setOpen('sendToOwner')}>
                Send to owner
              </Button>
            )}
            {has('sendForApproval') && (
              <Button variant="primary" icon={<Send aria-hidden className="size-4" />} onClick={() => setOpen('sendForApproval')}>
                Send for approval
              </Button>
            )}
            {has('approve') && (
              <Button variant="primary" icon={<BadgeCheck aria-hidden className="size-4" />} onClick={() => setOpen('approve')}>
                Approve
              </Button>
            )}
            {has('sendBack') && (
              <Button icon={<Undo2 aria-hidden className="size-4" />} onClick={() => setOpen('sendBack')}>
                Send back
              </Button>
            )}
            {has('withdraw') && (
              <Button variant="ghost" className="text-danger" icon={<X aria-hidden className="size-4" />} onClick={() => setOpen('withdraw')}>
                Withdraw
              </Button>
            )}
          </div>
        )}
      </Card>

      <ChangesEditor data={data} />
      <DocumentsCard data={data} />
      <ConversationCard data={{ conversation: data.conversation } as ContractDetail} />

      {open === 'sendToLegal' && (
        <NoteDialog data={data} onClose={close} title={v.legalClearedUtc ? 'Send back to Legal' : 'Send to Legal'} label="Note for Legal (optional)" path="send-to-legal" submit="Send to Legal" success="Sent to Legal." required={false} />
      )}
      {open === 'sendToOwner' && <NoteDialog data={data} onClose={close} title="Send to owner" label="What's needed" path="legal/send-to-owner" submit="Send to owner" success="Sent to the owner." />}
      {open === 'clear' && <NoteDialog data={data} onClose={close} title="Legal cleared" label="Outcome" path="legal/clear" submit="Clear it" success="Cleared." />}
      {open === 'sendBack' && <NoteDialog data={data} onClose={close} title="Send back" label="What needs fixing" path="send-back" submit="Send back" success="Sent back to the owner." />}
      {open === 'withdraw' && <NoteDialog data={data} onClose={close} title="Withdraw this Variation?" label="Why" path="withdraw" submit="Withdraw" success="Withdrawn. Nothing changed on the contract." danger />}
      {open === 'sendForApproval' && (
        <ConfirmDialog data={data} onClose={close} title="Send for approval?" path="send-for-approval" submit="Send for approval" success="Sent for approval. The Group Head has been emailed." />
      )}
      {open === 'approve' && <ConfirmDialog data={data} onClose={close} title="Approve this Variation?" path="approve" submit="Approve" success="Approved. The changes now apply to the contract." />}
    </>
  )
}

/** The fields a Variation can change: now and new, side by side. Editable on a draft Legal hasn't cleared, or on the owner's turn. */
function ChangesEditor({ data }: { data: VariationDetail }) {
  const queryClient = useQueryClient()
  const editable = data.actions.includes('edit')
  const v = data.variation
  const initial = () => Object.fromEntries(data.fields.map((f) => [f.key, (f.changed ? f.proposed : f.current) as FieldValue]))
  const [values, setValues] = useState<Record<string, FieldValue>>(initial)
  const [legal, setLegal] = useState(v.needsLegalReview)
  const [errors, setErrors] = useState<Record<string, string>>(data.problems)
  const people = useQuery({ queryKey: ['people-picker'], queryFn: () => api.get<{ id: number; name: string }[]>('/api/people/picker'), enabled: editable })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<{ id: number; name: string; isActive: boolean }[]>('/api/suppliers'), enabled: editable })
  const same = (a: FieldValue, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null) || String(a ?? '') === String(b ?? '')
  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/contracts/${data.contract.id}/variations/${v.id}`, {
        rowVersion: v.rowVersion,
        needsLegalReview: legal,
        changes: Object.fromEntries(data.fields.filter((f) => !same(values[f.key], f.current)).map((f) => [f.key, values[f.key]])),
      }),
    onSuccess: () => {
      setErrors({})
      queryClient.invalidateQueries({ queryKey: ['variation', v.id] })
      toast.success('Changes saved. Nothing changes on the contract until it is approved.')
    },
    onError: (e) => setErrors(fieldErrors(e, 'form')),
  })

  if (!editable)
    return (
      <Card className="p-5 md:p-6">
        <h2 className="font-display text-lg font-semibold">What changes</h2>
        {data.changes.length === 0 ? (
          <p className="mt-2 text-sm text-muted-foreground">No changes.</p>
        ) : (
          <table className="mt-3 w-full text-left text-sm">
            <thead className="text-xs text-muted-foreground">
              <tr>
                <th scope="col" className="py-2 font-medium">Field</th>
                <th scope="col" className="py-2 font-medium">Now</th>
                <th scope="col" className="py-2 font-medium">{v.stage === 'Approved' ? 'Changed to' : 'New'}</th>
              </tr>
            </thead>
            <tbody>
              {data.changes.map((ch) => (
                <tr key={ch.key} className="border-t border-border">
                  <td className="py-2 pr-3 font-medium">{ch.label}</td>
                  <td className="py-2 pr-3 text-muted-foreground">{ch.old}</td>
                  <td className="py-2 font-medium">{ch.new}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    )

  return (
    <Card className="flex flex-col gap-4 p-5 md:p-6">
      <div>
        <h2 className="font-display text-lg font-semibold">What changes</h2>
        <p className="text-sm text-muted-foreground">Change only what the Variation changes. The contract keeps its current values until the Variation is approved.</p>
      </div>
      {errors.form && <Alert tone="danger" title={errors.form} />}
      <div className="flex flex-col divide-y divide-border">
        {data.fields.map((f) => {
          const field: LayoutField = {
            key: f.key, isBuiltIn: false, isRuleField: false, kind: f.kind as LayoutField['kind'], kindName: '', label: `New ${f.label.charAt(0).toLowerCase()}${f.label.slice(1)}`,
            help: f.help, sectionKey: '', isRequired: false, isHidden: false, showInList: false, filterable: false, canFilter: false, inExport: false,
            afterEffective: 'Variation', choices: f.choices, maxLength: f.maxLength, minValue: f.minValue, maxValue: f.maxValue, hasValues: false,
          }
          const changed = !same(values[f.key], f.current)
          return (
            <div key={f.key} className={cn('grid gap-3 py-3 md:grid-cols-2 md:items-start', changed && 'rounded-[var(--radius-control)] bg-primary-soft/30 px-2')}>
              <div className="text-sm">
                <p className="font-medium">{f.label}</p>
                <p className="text-muted-foreground">Now: {f.currentDisplay || 'not filled in'}</p>
                {changed && <Badge tone="brand">Changing</Badge>}
              </div>
              <FieldInput
                field={field}
                value={values[f.key] ?? null}
                onChange={(val) => setValues({ ...values, [f.key]: val })}
                error={errors[`change.${f.key}`]}
                picks={{ people: people.data ?? [], suppliers: (suppliers.data ?? []).filter((s) => s.isActive) }}
              />
            </div>
          )
        })}
      </div>
      {v.stage === 'Draft' && (
        <Checkbox label="Needs Legal review" description="Untick only if Legal has agreed it doesn't need a review." checked={legal} onChange={setLegal} />
      )}
      <div className="flex gap-2">
        <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>
          Save changes
        </Button>
        <Button
          variant="ghost"
          onClick={() => {
            setValues(initial())
            setErrors({})
          }}
        >
          Undo
        </Button>
      </div>
    </Card>
  )
}

function DocumentsCard({ data }: { data: VariationDetail }) {
  const queryClient = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const canUpload = data.actions.includes('edit') || data.actions.includes('sendToOwner') || data.actions.includes('sendForApproval') || data.variation.stage === 'Approval' && data.actions.includes('withdraw')
  const upload = useMutation({
    mutationFn: (file: File) => uploadDocument(data.contract.id, file, 'Variation', undefined, data.variation.id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['variation', data.variation.id] })
      toast.success('Document added.')
    },
    onError: (e) => toast.error(e instanceof ApiError ? (Object.values(e.fields)[0] ?? e.message) : 'The upload failed. Try again.'),
  })
  const addDropped = async (files: File[]) => {
    for (const f of files) {
      try {
        await upload.mutateAsync(f)
      } catch {
        return // the error is shown
      }
    }
  }
  return (
    <DropTarget enabled={canUpload} message="Drop to add to this Variation" onFiles={(f) => void addDropped(f)}>
    <Card className="p-5 md:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="font-display text-lg font-semibold">Documents</h2>
          {canUpload && <p className="hidden text-sm text-muted-foreground md:block">You can also drag files onto this card.</p>}
        </div>
        {canUpload && (
          <Button icon={<Upload aria-hidden className="size-4" />} loading={upload.isPending} onClick={() => input.current?.click()}>
            Add the signed variation (or other document)
          </Button>
        )}
      </div>
      <input
        ref={input}
        type="file"
        hidden
        accept=".pdf,.doc,.docx,.xls,.xlsx,.msg,.eml,.txt,.csv,.png,.jpg,.jpeg"
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) upload.mutate(file)
          e.target.value = ''
        }}
      />
      {data.documents.length === 0 ? (
        <EmptyState icon={<FileText />} title="No documents yet. The signed variation document is optional; the approver decides whether one is needed." />
      ) : (
        <ul className="mt-3 flex flex-col gap-2">
          {data.documents.map((d) => (
            <li key={d.id}>
              <a href={`/api/documents/${d.versions[0].id}/file`} className="inline-flex items-center gap-2 font-medium text-primary hover:underline">
                <Paperclip aria-hidden className="size-4" /> {d.versions[0].fileName}
              </a>{' '}
              <span className="text-xs text-muted-foreground">
                {d.versions[0].uploadedByName}, {formatDateTime(d.versions[0].uploadedUtc)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
    </DropTarget>
  )
}

function NoteDialog(props: { data: VariationDetail; onClose: () => void; title: string; label: string; path: string; submit: string; success: string; required?: boolean; danger?: boolean }) {
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | undefined>()
  const action = useVAction(props.data, props.onClose)
  const required = props.required ?? true
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && props.onClose()}
      title={props.title}
      footer={
        <>
          <Button onClick={props.onClose}>Cancel</Button>
          <Button
            variant={props.danger ? 'danger' : 'primary'}
            loading={action.isPending}
            onClick={() => {
              if (required && !note.trim()) return setError('Add a note.')
              action.mutate({ path: props.path, body: { note }, success: props.success }, { onError: (e) => setError(errorText(e, 'note')) })
            }}
          >
            {props.submit}
          </Button>
        </>
      }
    >
      <Field label={props.label} required={required} error={error}>
        {(p) => <Textarea {...p} autoFocus value={note} onChange={(e) => setNote(e.target.value)} maxLength={2000} />}
      </Field>
    </Dialog>
  )
}

function ConfirmDialog(props: { data: VariationDetail; onClose: () => void; title: string; path: string; submit: string; success: string }) {
  const [error, setError] = useState<string | undefined>()
  const action = useVAction(props.data, props.onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && props.onClose()}
      title={props.title}
      footer={
        <>
          <Button onClick={props.onClose}>Cancel</Button>
          <Button variant="primary" loading={action.isPending} onClick={() => action.mutate({ path: props.path, success: props.success }, { onError: (e) => setError(errorText(e)) })}>
            {props.submit}
          </Button>
        </>
      }
    >
      {error && <Alert tone="danger" title={error} />}
      <ul className="flex flex-col gap-1 text-sm">
        {props.data.changes.map((ch) => (
          <li key={ch.key}>
            <strong>{ch.label}</strong>: {ch.old} → {ch.new}
          </li>
        ))}
      </ul>
    </Dialog>
  )
}
