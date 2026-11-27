// Changing a locked contract (product-spec section 7): Variations, hand-over, Confidential (admins), any-time fields and the admin's
// emergency unlock, with its banner.
import { useQuery } from '@tanstack/react-query'
import { FilePen, Lock, LockOpen, Pencil, ShieldAlert, UserRoundCog } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { toast } from 'sonner'
import { errorText, fieldErrors, useAction } from '@/lib/actions'
import { api } from '@/lib/api'
import type { ContractDetail, Lists } from '@/lib/contracts'
import { formatDateTime } from '@/lib/format'
import type { FieldValue, Layout, LayoutField } from '@/lib/layout'
import { Dialog } from './Dialog'
import { FieldInput } from './FieldInput'
import { Select, StageBadge, Textarea } from './form'
import { Alert, Badge, Button, Card, Checkbox, Field, TextInput } from './ui'

export function UnlockBanner({ data }: { data: ContractDetail }) {
  const u = data.contract.unlock
  const [closing, setClosing] = useState(false)
  const action = useAction(data, () => setClosing(false))
  if (!u) return null
  return (
    <Alert tone="warning" title={`Unlocked for an emergency correction by ${u.byName}, until ${formatDateTime(u.expiresUtc)}`}>
      <p>{u.reason}</p>
      <p className="mt-1 text-xs">Only admins can change it while it's unlocked, and every change records this unlock.</p>
      {data.actions.includes('closeUnlock') && (
        <div className="mt-2">
          <Button icon={<Lock aria-hidden className="size-4" />} loading={action.isPending && closing} onClick={() => {
            setClosing(true)
            action.mutate({ path: 'unlock/close', success: 'Locked again.' }, { onError: (e) => toast.error(errorText(e)) })
          }}>
            Lock it again now
          </Button>
        </div>
      )}
    </Alert>
  )
}

/**
 * The SAP outline agreement number on the contract page, with Add or Change from Signing on (D-124): it often only exists once the
 * contract is signed, and it's a reference, not a term, so it changes without a Variation.
 */
export function SapNumberValue({ data }: { data: ContractDetail }) {
  const value = data.contract.sapOutlineAgreement
  const [open, setOpen] = useState(false)
  return (
    <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
      {value ?? <span className="text-muted-foreground">Not filled in yet</span>}
      <Button variant="ghost" className="min-h-9 px-2 text-primary" icon={<Pencil aria-hidden className="size-4" />} onClick={() => setOpen(true)}>
        {value ? 'Change' : 'Add'}
        <span className="sr-only"> {data.sapLabel}</span>
      </Button>
      {open && <SapNumberDialog data={data} onClose={() => setOpen(false)} />}
    </span>
  )
}

function SapNumberDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const label = data.sapLabel ?? 'SAP outline agreement number'
  const [value, setValue] = useState(data.contract.sapOutlineAgreement ?? '')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={data.contract.sapOutlineAgreement ? `Change the ${label}` : `Add the ${label}`}
      description="It's a reference, so it changes without a Variation. The change shows in Activity."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => action.mutate({ path: 'sap-number', body: { sapOutlineAgreement: value }, success: 'Saved.' }, { onError: (e) => setError(errorText(e, 'sapOutlineAgreement')) })}
          >
            Save
          </Button>
        </>
      }
    >
      <Field label={label} error={error} help="Leave it empty to clear it.">
        {(p) => <TextInput {...p} autoFocus value={value} onChange={(e) => setValue(e.target.value)} maxLength={50} />}
      </Field>
    </Dialog>
  )
}

type Open = 'start' | 'handOver' | 'confidential' | 'anyTime' | 'unlock' | null

export function StartVariationButton({ data, variant = 'secondary' }: { data: ContractDetail; variant?: 'primary' | 'secondary' }) {
  const [open, setOpen] = useState(false)
  if (!data.actions.includes('startVariation')) return null
  return (
    <>
      <Button variant={variant} icon={<FilePen aria-hidden className="size-4" />} onClick={() => setOpen(true)}>
        Start a Variation
      </Button>
      {open && <StartVariationDialog data={data} onClose={() => setOpen(false)} />}
    </>
  )
}

export function ChangesCard({ data, layout }: { data: ContractDetail; layout: Layout }) {
  const c = data.contract
  const [open, setOpen] = useState<Open>(null)
  const has = (a: string) => data.actions.includes(a as never)
  const close = () => setOpen(null)
  if (!['Effective', 'Ended'].includes(c.stage) && data.variations.length === 0) return null
  return (
    <Card className="flex flex-col gap-4 p-5 md:p-6">
      <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
        <FilePen aria-hidden className="size-5" /> Changes
      </h2>
      {c.stage === 'Effective' && (
        <p className="text-sm text-muted-foreground">
          It's locked. Dates, notice period, value, business unit, site and most added fields change through a <strong>Variation</strong>, approved like the
          contract. Title, supplier and category don't change: start a new contract instead.
        </p>
      )}
      {data.variations.length > 0 && (
        <ul className="divide-y divide-border rounded-[var(--radius-control)] border border-border">
          {data.variations.map((v) => (
            <li key={v.id} className="flex flex-wrap items-center justify-between gap-2 p-3">
              <div className="min-w-0">
                <Link to={`/contracts/${c.id}/variations/${v.id}`} className="font-medium text-primary hover:underline">
                  Variation {v.number}
                </Link>{' '}
                <span className="text-sm text-muted-foreground">· {v.reason}</span>
                {v.applied && v.applied.length > 0 && (
                  <p className="text-xs text-muted-foreground">{v.applied.map((a) => `${a.label}: ${a.old} → ${a.new}`).join('; ')}</p>
                )}
              </div>
              <Badge tone={v.stage === 'Approved' ? 'success' : v.stage === 'Withdrawn' ? 'neutral' : 'info'}>{v.stageName}</Badge>
            </li>
          ))}
        </ul>
      )}
      {c.openVariationId && (
        <p className="text-sm">
          A Variation is in progress.{' '}
          <Link to={`/contracts/${c.id}/variations/${c.openVariationId}`} className="font-medium text-primary hover:underline">
            Open it
          </Link>
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        {has('startVariation') && (
          <Button icon={<FilePen aria-hidden className="size-4" />} onClick={() => setOpen('start')}>
            Start a Variation
          </Button>
        )}
        {has('handOver') && (
          <Button variant="ghost" icon={<UserRoundCog aria-hidden className="size-4" />} onClick={() => setOpen('handOver')}>
            Hand over
          </Button>
        )}
        {has('anyTimeFields') && (
          <Button variant="ghost" onClick={() => setOpen('anyTime')}>
            Edit any-time fields
          </Button>
        )}
        {has('confidential') && (
          <Button variant="ghost" icon={<ShieldAlert aria-hidden className="size-4" />} onClick={() => setOpen('confidential')}>
            {c.isConfidential ? 'Stop being Confidential' : 'Make Confidential'}
          </Button>
        )}
        {has('unlock') && (
          <Button variant="ghost" className="text-danger" icon={<LockOpen aria-hidden className="size-4" />} onClick={() => setOpen('unlock')}>
            Emergency unlock
          </Button>
        )}
      </div>
      {open === 'start' && <StartVariationDialog data={data} onClose={close} />}
      {open === 'handOver' && <HandOverDialog data={data} onClose={close} />}
      {open === 'confidential' && <ConfidentialDialog data={data} onClose={close} />}
      {open === 'anyTime' && <AnyTimeDialog data={data} layout={layout} onClose={close} />}
      {open === 'unlock' && <UnlockDialog data={data} onClose={close} />}
    </Card>
  )
}

function StartVariationDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const navigate = useNavigate()
  const [reason, setReason] = useState('')
  const [legal, setLegal] = useState(true)
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Start a Variation"
      description="Next you choose what changes. Nothing changes on the contract until the Variation is approved."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => {
              if (!reason.trim()) return setError('Say why it is changing.')
              action.mutate(
                { path: 'variations', body: { reason, needsLegalReview: legal }, success: 'Variation started. Now choose what changes.' },
                {
                  onSuccess: (r) => {
                    const id = (r as { result?: { variationId?: number } }).result?.variationId
                    onClose()
                    if (id) navigate(`/contracts/${data.contract.id}/variations/${id}`)
                  },
                  onError: (e) => setError(errorText(e, 'reason')),
                },
              )
            }}
          >
            Start
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label="Why is it changing?" required error={error}>
          {(p) => <Textarea {...p} autoFocus value={reason} onChange={(e) => setReason(e.target.value)} placeholder="For example: the supplier agreed a 12-month extension." maxLength={2000} />}
        </Field>
        <Checkbox label="Needs Legal review" description="Untick only if Legal has agreed it doesn't need a review." checked={legal} onChange={setLegal} />
      </div>
    </Dialog>
  )
}

function HandOverDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const c = data.contract
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const [owner, setOwner] = useState(String(c.ownerId))
  const [backup, setBackup] = useState(c.backupOwnerId ? String(c.backupOwnerId) : '')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const action = useAction(data, onClose)
  const owners = lists.data?.owners ?? []
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Hand over"
      description="The new owner and backup get an email, and its reminders from now on."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() =>
              action.mutate(
                { path: 'hand-over', body: { ownerId: Number(owner), backupOwnerId: backup ? Number(backup) : null }, success: 'Handed over.' },
                { onError: (e) => setErrors(fieldErrors(e, 'ownerId')) },
              )
            }
          >
            Hand over
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label="Owner" required error={errors.ownerId}>
          {(p) => (
            <Select {...p} value={owner} onChange={(e) => setOwner(e.target.value)}>
              {owners.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label="Backup" error={errors.backupOwnerId}>
          {(p) => (
            <Select {...p} value={backup} onChange={(e) => setBackup(e.target.value)}>
              <option value="">No backup</option>
              {owners
                .filter((o) => String(o.id) !== owner)
                .map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
            </Select>
          )}
        </Field>
      </div>
    </Dialog>
  )
}

function ConfidentialDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const to = !data.contract.isConfidential
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={to ? 'Make it Confidential?' : 'Stop it being Confidential?'}
      description={to ? 'Only its owner and backup, Legal, approvers, admins and auditors will see it.' : 'Everyone added to JBS Contracts will see it.'}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => {
              if (!reason.trim()) return setError('Say why.')
              action.mutate({ path: 'confidential', body: { isConfidential: to, note: reason }, success: 'Saved.' }, { onError: (e) => setError(errorText(e, 'note')) })
            }}
          >
            Save
          </Button>
        </>
      }
    >
      <Field label="Why?" required error={error}>
        {(p) => <Textarea {...p} autoFocus value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000} />}
      </Field>
    </Dialog>
  )
}

function AnyTimeDialog({ data, layout, onClose }: { data: ContractDetail; layout: Layout; onClose: () => void }) {
  const fields = layout.fields.filter((f) => !f.isBuiltIn && !f.isHidden && f.afterEffective === 'AnyTime')
  const people = useQuery({ queryKey: ['people-picker'], queryFn: () => api.get<{ id: number; name: string }[]>('/api/people/picker') })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<{ id: number; name: string; isActive: boolean }[]>('/api/suppliers') })
  const [values, setValues] = useState<Record<string, FieldValue>>(() => Object.fromEntries(fields.map((f) => [f.key, (data.fieldValues[f.key]?.value ?? null) as FieldValue])))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Edit any-time fields"
      description="Your admin lets these change at any time, without a Variation. Each change is recorded."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => action.mutate({ path: 'any-time-fields', body: { fields: values }, success: 'Saved.' }, { onError: (e) => setErrors(fieldErrors(e, 'form')) })}
          >
            Save
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {errors.form && <Alert tone="danger" title={errors.form} />}
        {fields.map((f: LayoutField) => (
          <FieldInput
            key={f.key}
            field={f}
            value={values[f.key] ?? null}
            onChange={(v) => setValues({ ...values, [f.key]: v })}
            error={errors[`field.${f.key}`]}
            picks={{ people: people.data ?? [], suppliers: (suppliers.data ?? []).filter((s) => s.isActive) }}
          />
        ))}
      </div>
    </Dialog>
  )
}

function UnlockDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const [reason, setReason] = useState('')
  const [hours, setHours] = useState('1')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Emergency unlock"
      description="For correcting a mistake that a Variation can't fix. Only admins can edit while it's unlocked; it locks again by itself."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="danger"
            loading={action.isPending}
            onClick={() => {
              if (!reason.trim()) return setError('Say why it needs unlocking.')
              action.mutate({ path: 'unlock', body: { note: reason, hours: Number(hours) }, success: 'Unlocked. Use Edit details to correct it.' }, { onError: (e) => setError(errorText(e, 'note')) })
            }}
          >
            Unlock
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label="Why does it need unlocking?" required error={error}>
          {(p) => <Textarea {...p} autoFocus value={reason} onChange={(e) => setReason(e.target.value)} placeholder="For example: the supplier name was mistyped at registration." maxLength={1000} />}
        </Field>
        <Field label="For how long">
          {(p) => (
            <Select {...p} value={hours} onChange={(e) => setHours(e.target.value)} className="max-w-48">
              {[1, 2, 3, 4].map((h) => (
                <option key={h} value={h}>
                  {h} hour{h > 1 ? 's' : ''}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <p className="text-sm text-muted-foreground">
          The stage and registered date never change. <StageBadge stage={data.contract.stage} name={data.contract.stageName} /> stays as it is.
        </p>
      </div>
    </Dialog>
  )
}
