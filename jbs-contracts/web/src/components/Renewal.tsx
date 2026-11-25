// Renewal on an Effective contract: record a decision (Renew, Let it end, Renegotiate), end early, review done, and an admin's
// archive once Ended (product-spec section 8; D-67 to D-69). Also the reminders already sent.
import { Archive, BellRing, CalendarCheck, CalendarX2, RefreshCw } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { toast } from 'sonner'
import { errorText, useAction } from '@/lib/actions'
import { ApiError } from '@/lib/api'
import type { ContractDetail, RenewalDecision } from '@/lib/contracts'
import { formatDate, formatDateTime } from '@/lib/format'
import { Dialog } from './Dialog'
import { Textarea } from './form'
import { Alert, Badge, Button, Card, Field, TextInput } from './ui'

const decisions: { value: RenewalDecision; label: string; description: string }[] = [
  { value: 'Renew', label: 'Renew', description: 'Starts a new draft linked to this one, with the same details. When it becomes Effective, this one ends.' },
  { value: 'LetItEnd', label: 'Let it end', description: 'It ends on its end date (or a date you choose). Give the supplier notice before the deadline.' },
  { value: 'Renegotiate', label: 'Renegotiate', description: 'You are changing terms with the supplier. Changes are made through a Variation.' },
]

const reminderNames: Record<ContractDetail['reminders'][number]['kind'], string> = {
  Notice: 'Notice deadline',
  Escalation: 'Escalation to the Group Head',
  End: 'End date',
  Review: 'Review date',
}

export function RenewalCard({ data, openDecision }: { data: ContractDetail; openDecision?: boolean }) {
  const c = data.contract
  const r = data.renewal
  const [open, setOpen] = useState<'decision' | 'end' | 'review' | 'archive' | null>(openDecision && data.actions.includes('recordDecision') ? 'decision' : null)
  const has = (a: string) => data.actions.includes(a as never)
  if (!['Effective', 'Ended', 'Archived'].includes(c.stage)) return null
  const close = () => setOpen(null)

  return (
    <Card className="flex flex-col gap-4 p-5 md:p-6">
      <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
        <RefreshCw aria-hidden className="size-5" /> Renewal
      </h2>
      {c.stage !== 'Effective' ? (
        <p className="text-sm">
          <strong>Ended</strong>
          {c.endedOn && ` on ${formatDate(c.endedOn)}`}
          {c.endedReason && `: ${c.endedReason}`}
          {c.renewedById && (
            <>
              {' '}
              <Link to={`/contracts/${c.renewedById}`} className="font-medium text-primary hover:underline">
                Open {c.renewedByReference}
              </Link>
            </>
          )}
        </p>
      ) : r.decision ? (
        <div className="flex flex-col gap-1 text-sm">
          <p className="flex flex-wrap items-center gap-2">
            <Badge tone={r.decision === 'LetItEnd' ? 'warning' : 'success'}>{r.decisionName}</Badge>
            <span className="text-muted-foreground">
              decided by {r.decidedByName}
              {r.decidedUtc && `, ${formatDateTime(r.decidedUtc)}`}
            </span>
          </p>
          {r.note && <p>{r.note}</p>}
          {r.plannedEndDate && <p>It ends on {formatDate(r.plannedEndDate)}.</p>}
          {r.decision === 'LetItEnd' && !r.plannedEndDate && c.endDate && <p>It ends on its end date, {formatDate(c.endDate)}.</p>}
          {c.renewedById && (
            <p>
              Renewal:{' '}
              <Link to={`/contracts/${c.renewedById}`} className="font-medium text-primary hover:underline">
                {c.renewedByReference}
              </Link>{' '}
              ({c.renewedByStageName}). This contract ends when the renewal becomes Effective.
            </p>
          )}
          <p className="text-muted-foreground">Notice and end-date reminders have stopped.</p>
        </div>
      ) : (
        <p className="text-sm">
          No decision yet.{' '}
          {c.noticeDeadline ? `Decide before the notice deadline, ${formatDate(c.noticeDeadline)}.` : c.endDate ? `It ends on ${formatDate(c.endDate)}.` : ''}
        </p>
      )}
      {c.stage === 'Effective' && (c.reviewDate || c.lastReviewedOn) && (
        <p className="text-sm text-muted-foreground">
          {c.reviewDate ? `Next review ${formatDate(c.reviewDate)}.` : 'No review date.'}
          {c.lastReviewedOn && ` Last reviewed ${formatDate(c.lastReviewedOn)}.`}
        </p>
      )}

      {(has('recordDecision') || has('archive')) && (
        <div className="flex flex-wrap gap-2">
          {has('recordDecision') && (
            <Button variant={r.decision ? 'secondary' : 'primary'} icon={<RefreshCw aria-hidden className="size-4" />} onClick={() => setOpen('decision')}>
              {r.decision ? 'Change decision' : 'Record decision'}
            </Button>
          )}
          {has('reviewDone') && (
            <Button variant="ghost" icon={<CalendarCheck aria-hidden className="size-4" />} onClick={() => setOpen('review')}>
              Review done
            </Button>
          )}
          {has('endEarly') && (
            <Button variant="ghost" className="text-danger" icon={<CalendarX2 aria-hidden className="size-4" />} onClick={() => setOpen('end')}>
              End early
            </Button>
          )}
          {has('archive') && (
            <Button icon={<Archive aria-hidden className="size-4" />} onClick={() => setOpen('archive')}>
              Archive
            </Button>
          )}
        </div>
      )}

      {data.reminders.length > 0 && (
        <details className="text-sm">
          <summary className="flex cursor-pointer items-center gap-2 text-muted-foreground">
            <BellRing aria-hidden className="size-4" /> Reminders sent ({data.reminders.length})
          </summary>
          <ul className="mt-2 flex flex-col gap-1 pl-6">
            {data.reminders.map((m) => (
              <li key={`${m.kind}-${m.dueDate}-${m.daysBefore}`}>
                {reminderNames[m.kind]}: {m.daysBefore} days before {formatDate(m.dueDate)}, to {m.recipients}{' '}
                <span className="text-muted-foreground">({formatDateTime(m.sentUtc)})</span>
              </li>
            ))}
          </ul>
        </details>
      )}

      {open === 'decision' && <DecisionDialog data={data} onClose={close} />}
      {open === 'end' && <EndDialog data={data} onClose={close} />}
      {open === 'review' && <ReviewDialog data={data} onClose={close} />}
      {open === 'archive' && <ArchiveDialog data={data} onClose={close} />}
    </Card>
  )
}

function DecisionDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const c = data.contract
  const navigate = useNavigate()
  const [decision, setDecision] = useState<RenewalDecision | ''>(data.renewal.decision ?? '')
  const [note, setNote] = useState(data.renewal.note ?? '')
  const [date, setDate] = useState(data.renewal.plannedEndDate ?? '')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const action = useAction(data)
  const needsDate = decision === 'LetItEnd' && (c.isEvergreen || !c.endDate)
  const save = () => {
    if (!decision) return setErrors({ decision: 'Choose a decision.' })
    if (needsDate && !date) return setErrors({ date: 'Enter the date it will end, once notice is given.' })
    action.mutate(
      { path: 'renewal-decision', body: { decision, note, date: needsDate ? date : null }, success: 'Decision recorded. Its reminders have stopped.' },
      {
        onSuccess: (result) => {
          onClose()
          const renewalId = (result as { result?: { renewalId?: number } }).result?.renewalId
          if (renewalId) {
            toast.success('The renewal draft is ready. Check its details, then send it on.')
            navigate(`/contracts/${renewalId}`)
          }
        },
        onError: (e) => setErrors(e instanceof ApiError && Object.keys(e.fields).length > 0 ? e.fields : { form: errorText(e) }),
      },
    )
  }
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Renewal decision"
      description={c.noticeDeadline ? `Notice deadline: ${formatDate(c.noticeDeadline)}. Recording a decision stops the notice and end-date reminders.` : 'Recording a decision stops the notice and end-date reminders.'}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={action.isPending} onClick={save}>
            {decision === 'Renew' && !c.renewedById ? 'Renew and start the draft' : 'Save decision'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {errors.form && <Alert tone="danger" title={errors.form} />}
        <fieldset className="flex flex-col gap-2" aria-describedby={errors.decision ? 'decision-error' : undefined}>
          <legend className="mb-1 text-sm font-medium">What happens to this contract?</legend>
          {decisions.map((d) => (
            <label key={d.value} className="flex cursor-pointer items-start gap-3 rounded-[var(--radius-control)] border border-border p-3 has-[:checked]:border-primary has-[:checked]:bg-primary-soft/40">
              <input type="radio" name="decision" value={d.value} checked={decision === d.value} onChange={() => setDecision(d.value)} className="mt-0.5 size-5 accent-[var(--primary)]" />
              <span className="flex flex-col">
                <span className="font-medium">{d.label}</span>
                <span className="text-xs text-muted-foreground">{d.description}</span>
              </span>
            </label>
          ))}
          {errors.decision && (
            <p id="decision-error" className="text-xs font-medium text-danger">
              {errors.decision}
            </p>
          )}
        </fieldset>
        {needsDate && (
          <Field label="Date it ends" required error={errors.date} help={c.isEvergreen ? "It's evergreen, so say when it will end once notice is given." : 'It has no end date, so say when it will end.'}>
            {(p) => <TextInput {...p} type="date" value={date} onChange={(e) => setDate(e.target.value)} className="max-w-56" />}
          </Field>
        )}
        <Field label="Note (optional)" error={errors.note}>
          {(p) => <Textarea {...p} value={note} onChange={(e) => setNote(e.target.value)} placeholder="For example: rates are competitive; renew for two years." maxLength={1000} />}
        </Field>
      </div>
    </Dialog>
  )
}

function EndDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const [reason, setReason] = useState('')
  const [date, setDate] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="End this contract early?"
      description="Today or an earlier date ends it now. A later date ends it then, and its reminders stop."
      footer={
        <>
          <Button onClick={onClose}>Keep it</Button>
          <Button
            variant="danger"
            loading={action.isPending}
            onClick={() => {
              const problems: Record<string, string> = {}
              if (!reason.trim()) problems.note = 'Say why it is ending.'
              if (!date) problems.date = 'Enter the date it ends.'
              setErrors(problems)
              if (Object.keys(problems).length > 0) return
              action.mutate({ path: 'end', body: { note: reason, date }, success: 'Saved.' }, { onError: (e) => setErrors(e instanceof ApiError ? e.fields : { note: errorText(e) }) })
            }}
          >
            End it
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label="Why is it ending?" required error={errors.note}>
          {(p) => <Textarea {...p} autoFocus value={reason} onChange={(e) => setReason(e.target.value)} placeholder="For example: the supplier went into administration." maxLength={1000} />}
        </Field>
        <Field label="Date it ends" required error={errors.date}>
          {(p) => <TextInput {...p} type="date" value={date} onChange={(e) => setDate(e.target.value)} className="max-w-56" />}
        </Field>
      </div>
    </Dialog>
  )
}

function ReviewDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const [date, setDate] = useState('')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Review done"
      description="Records today's review. Reminders for the old review date stop; a next review date gets its own reminders."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => action.mutate({ path: 'review-done', body: { date: date || null }, success: 'Review recorded.' }, { onError: (e) => setError(errorText(e, 'date')) })}
          >
            Save
          </Button>
        </>
      }
    >
      <Field label="Next review date (optional)" error={error} help="Leave it empty if there's no further review.">
        {(p) => <TextInput {...p} type="date" value={date} onChange={(e) => setDate(e.target.value)} className="max-w-56" />}
      </Field>
    </Dialog>
  )
}

function ArchiveDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={`Archive ${data.contract.title}?`}
      description="It becomes read only and moves to Closed. Nothing is deleted."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={action.isPending} onClick={() => action.mutate({ path: 'archive', success: 'Archived.' }, { onError: (e) => toast.error(errorText(e)) })}>
            Archive
          </Button>
        </>
      }
    >
      <p className="text-sm">Its documents, history and audit are kept.</p>
    </Dialog>
  )
}
