// Where a contract is up to and what happens next, with the actions for the person looking (product-spec sections 4, 5 and 13.1),
// and the Legal conversation: every hand-over, numbered, as it was.
import { ArrowLeftRight, BadgeCheck, CheckCircle2, FileDown, Hourglass, Lock, MessagesSquare, Paperclip, Scale, Send, Undo2, UserCheck, X } from 'lucide-react'
import { useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { ApiError } from '@/lib/api'
import { errorText, useAction } from '@/lib/actions'
import { uploadDocument, type ContractAction, type ContractDetail, type LegalEntry } from '@/lib/contracts'
import { formatBytes, formatDate, formatDateTime, timeAgo } from '@/lib/format'
import { useMe } from '@/lib/me'
import { Dialog } from './Dialog'
import { DropZone } from './DropZone'
import { documentExtensions } from '@/lib/files'
import { Select, Textarea } from './form'
import { Alert, Badge, Button, Card, Field, TextInput } from './ui'

type Dialogs = 'sendToLegal' | 'sendToOwner' | 'clear' | 'status' | 'assign' | 'readyForSigning' | 'sendForApproval' | 'sendBackToLegal' | 'backToDraft' | 'approve' | 'sendBack' | null

/** "Legal's turn" headline, status and what happens next. */
function Situation({ data }: { data: ContractDetail }) {
  const c = data.contract
  const l = data.legal
  if (c.stage === 'Draft')
    return (
      <p className="text-sm">
        <strong>Next:</strong>{' '}
        {c.needsLegalReview ? 'fill in the details, then Send to Legal.' : 'fill in the details, then Ready for signing (Legal review is unticked).'}
      </p>
    )
  if (c.stage === 'LegalReview') {
    const legalsTurn = l.whoseTurn === 'Legal'
    return (
      <div className="flex flex-col gap-2">
        <p className="flex flex-wrap items-center gap-2">
          <Badge tone={legalsTurn ? 'info' : 'warning'} icon={legalsTurn ? <Scale aria-hidden className="size-3.5" /> : <UserCheck aria-hidden className="size-3.5" />}>
            {legalsTurn ? "Legal's turn" : "Owner's turn"}
          </Badge>
          <span className="font-medium">
            {legalsTurn ? (l.legalPersonName ? `${l.legalPersonName} has it` : 'New: nobody in Legal has picked it up yet') : `With ${c.ownerName} and the contract owners`}
          </span>
          {l.turnSinceUtc && (
            <span className="text-sm text-muted-foreground">
              since {formatDateTime(l.turnSinceUtc)} ({timeAgo(l.turnSinceUtc)})
            </span>
          )}
        </p>
        {l.statusName && (
          <p className="text-sm">
            <span className="text-muted-foreground">Status:</span> <strong>{l.statusName}</strong>
            {l.note && <span className="text-muted-foreground">: {l.note}</span>}
          </p>
        )}
        <p className="text-sm">
          <strong>Next:</strong>{' '}
          {legalsTurn
            ? "Legal reviews it, then sends it back to the owner with what's needed, or clears it."
            : 'the owner works through what Legal asked (often with the supplier), then sends it back to Legal.'}
        </p>
      </div>
    )
  }
  if (c.stage === 'Signing')
    return (
      <div className="flex flex-col gap-2">
        {l.outcome && (
          <p className="text-sm">
            <CheckCircle2 aria-hidden className="mr-1 inline size-4 text-success" />
            <strong>Cleared by Legal</strong>
            {l.clearedByName && ` (${l.clearedByName}${l.clearedUtc ? `, ${formatDateTime(l.clearedUtc)}` : ''})`}: {l.outcome}
          </p>
        )}
        <p className="text-sm">
          <strong>Next:</strong> sign it in DocuSign, then Send for approval with the signed copy and the effective date. The Group Head confirms it and it becomes
          Effective.
        </p>
      </div>
    )
  if (c.stage === 'Approval')
    return (
      <div className="flex flex-col gap-2">
        <p className="flex flex-wrap items-center gap-2">
          <Badge tone="warning" icon={<Hourglass aria-hidden className="size-3.5" />}>
            Waiting for approval
          </Badge>
          <span className="text-sm text-muted-foreground">
            since {formatDateTime(c.stageSinceUtc)} ({timeAgo(c.stageSinceUtc)})
          </span>
        </p>
        <p className="text-sm">
          <strong>Next:</strong> the Group Head (or a backup approver) checks the signed copy and details, then approves it. It was already approved and signed in
          DocuSign; this confirms it and puts it on the register. Effective from {formatDate(c.effectiveDate)}.
        </p>
        {data.cannotApproveReason && <p className="text-sm text-muted-foreground">{data.cannotApproveReason}</p>}
      </div>
    )
  if (c.stage === 'Effective' && c.importedUtc)
    return (
      <p className="flex flex-wrap items-center gap-2 text-sm">
        <Lock aria-hidden className="size-4 text-muted-foreground" />
        <span>
          <strong>Effective and locked.</strong> Imported from {c.importedFrom} on {formatDateTime(c.importedUtc)}
          {c.legacyReference ? ` (old reference ${c.legacyReference})` : ''}; registered {c.registeredUtc ? formatDateTime(c.registeredUtc) : 'before then'}.
        </span>
      </p>
    )
  if (c.stage === 'Effective' && c.registeredUtc)
    return (
      <p className="flex flex-wrap items-center gap-2 text-sm">
        <Lock aria-hidden className="size-4 text-muted-foreground" />
        <span>
          <strong>Effective and locked.</strong> Approved and registered by {c.registeredByName}
          {c.registeredAsBackup ? ' (as a backup approver)' : ''} on {formatDateTime(c.registeredUtc)}.
        </span>
      </p>
    )
  return null
}

export function ProgressPanel({ data, tracker }: { data: ContractDetail; tracker: ReactNode }) {
  const [open, setOpen] = useState<Dialogs>(null)
  const take = useAction(data)
  const has = (a: ContractAction) => data.actions.includes(a)
  const close = () => setOpen(null)
  // At most one primary button: the step that moves the contract on.
  const primary: ContractAction | undefined = (['sendToLegal', 'readyForSigning', 'take', 'clear', 'sendForApproval', 'approve'] as const).find(has)

  return (
    <Card className="flex flex-col gap-5 p-5 md:p-6">
      {tracker}
      <Situation data={data} />
      {data.missingRequired.length > 0 && (has('sendToLegal') || has('readyForSigning')) && (
        <Alert tone="warning" title={`Before it can be sent on, fill in: ${data.missingRequired.join(', ')}.`}>
          Use Edit details at the top of the page.
        </Alert>
      )}
      {data.actions.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {has('sendToLegal') && (
            <Button variant={primary === 'sendToLegal' ? 'primary' : 'secondary'} icon={<Send aria-hidden className="size-4" />} onClick={() => setOpen('sendToLegal')}>
              Send to Legal
            </Button>
          )}
          {has('readyForSigning') && (
            <Button variant="primary" icon={<Send aria-hidden className="size-4" />} onClick={() => setOpen('readyForSigning')}>
              Ready for signing
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
            <Button variant={primary === 'clear' ? 'primary' : 'secondary'} icon={<CheckCircle2 aria-hidden className="size-4" />} onClick={() => setOpen('clear')}>
              Legal cleared
            </Button>
          )}
          {has('sendToOwner') && (
            <Button icon={<ArrowLeftRight aria-hidden className="size-4" />} onClick={() => setOpen('sendToOwner')}>
              Send to owner
            </Button>
          )}
          {has('updateStatus') && (
            <Button variant="ghost" onClick={() => setOpen('status')}>
              Update status
            </Button>
          )}
          {has('assign') && (
            <Button variant="ghost" onClick={() => setOpen('assign')}>
              Give to someone in Legal
            </Button>
          )}
          {has('sendForApproval') && (
            <Button variant="primary" icon={<Send aria-hidden className="size-4" />} onClick={() => setOpen('sendForApproval')}>
              Send for approval
            </Button>
          )}
          {has('sendBackToLegal') && (
            <Button variant="ghost" icon={<Undo2 aria-hidden className="size-4" />} onClick={() => setOpen('sendBackToLegal')}>
              Send back to Legal
            </Button>
          )}
          {has('backToDraft') && (
            <Button variant="ghost" icon={<Undo2 aria-hidden className="size-4" />} onClick={() => setOpen('backToDraft')}>
              Back to draft
            </Button>
          )}
          {has('approve') && (
            <Link
              to={`/contracts/${data.contract.id}/approve`}
              className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] bg-primary px-4 text-sm font-medium text-primary-foreground hover:bg-primary-hover"
            >
              <BadgeCheck aria-hidden className="size-4" /> Review and approve
            </Link>
          )}
        </div>
      )}
      {open === 'sendToLegal' && (
        <HandOverDialog
          data={data}
          onClose={close}
          title="Send to Legal"
          description={data.legal.legalPersonName ? `It goes back to ${data.legal.legalPersonName}, who gets an email.` : 'Everyone in Legal gets an email; one of them picks it up.'}
          path="send-to-legal"
          noteLabel="Note for Legal"
          notePlaceholder="For example: the supplier accepted a 60-day cap. Their new version is attached."
          noteRequired={false}
          documentKind="Draft"
          submit="Send to Legal"
          success="Sent to Legal."
        />
      )}
      {open === 'sendToOwner' && (
        <HandOverDialog
          data={data}
          onClose={close}
          title="Send to owner"
          description={`${data.contract.ownerName}${data.contract.backupOwnerName ? ` and ${data.contract.backupOwnerName}` : ''} get an email. It comes back to you when they send it to Legal.`}
          path="legal/send-to-owner"
          noteLabel="What's needed"
          notePlaceholder="For example: the supplier won't accept clause 12, please negotiate."
          noteRequired
          documentKind="LegalMarkup"
          submit="Send to owner"
          success="Sent to the owner."
        />
      )}
      {open === 'clear' && <ClearDialog data={data} onClose={close} />}
      {open === 'status' && <StatusDialog data={data} onClose={close} />}
      {open === 'assign' && <AssignDialog data={data} onClose={close} />}
      {open === 'readyForSigning' && <ReadyForSigningDialog data={data} onClose={close} />}
      {open === 'sendForApproval' && <SendForApprovalDialog data={data} onClose={close} />}
      {open === 'sendBackToLegal' && (
        <ReasonDialog
          data={data}
          onClose={close}
          title="Send back to Legal"
          description={`Its details are frozen in Signing. Legal${data.legal.legalPersonName ? ` (${data.legal.legalPersonName})` : ''} gets it back with your reason and clears it again.`}
          label="What needs to change"
          placeholder="For example: the supplier wants to change the start date."
          path="send-back-to-legal"
          submit="Send back to Legal"
          success="Sent back to Legal."
        />
      )}
      {open === 'backToDraft' && (
        <ReasonDialog
          data={data}
          onClose={close}
          title="Back to draft"
          description="Its details are frozen in Signing. Taking it back to Draft lets you change them; then send it on again."
          label="What needs to change"
          placeholder="For example: the supplier changed the rates."
          path="back-to-draft"
          submit="Back to draft"
          success="It's back in Draft."
        />
      )}
    </Card>
  )
}

function HandOverDialog(props: {
  data: ContractDetail
  onClose: () => void
  title: string
  description: string
  path: string
  noteLabel: string
  notePlaceholder: string
  noteRequired: boolean
  documentKind: string
  submit: string
  success: string
}) {
  const { data, onClose } = props
  const [note, setNote] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const [error, setError] = useState<string | undefined>()
  const [uploading, setUploading] = useState(false)
  const action = useAction(data, onClose)
  const queryClient = useQueryClient()
  // Files already uploaded on an earlier try (and the contract's row version since), so trying again doesn't add them twice.
  const uploaded = useRef(new Map<File, number>())
  const rowVersion = useRef<string | undefined>(undefined)

  const send = async () => {
    setError(undefined)
    if (props.noteRequired && !note.trim()) return setError(`Add a note: ${props.noteLabel.toLowerCase()}.`)
    const waiting = files.filter((f) => !uploaded.current.has(f))
    if (waiting.length > 0) {
      setUploading(true)
      try {
        // Uploaded first, one at a time (they stay on the contract), then linked to this hand-over.
        for (const f of waiting) {
          const done = await uploadDocument(data.contract.id, f, props.documentKind)
          uploaded.current.set(f, done.versionId)
          rowVersion.current = done.rowVersion
        }
      } catch (e) {
        return setError(errorText(e))
      } finally {
        setUploading(false)
        void queryClient.invalidateQueries({ queryKey: ['contract', data.contract.id] })
      }
    }
    const documentVersionIds = files.map((f) => uploaded.current.get(f)!)
    action.mutate({ path: props.path, body: { note, documentVersionIds }, rowVersion: rowVersion.current, success: props.success }, { onError: (e) => setError(errorText(e, 'note')) })
  }

  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={props.title}
      description={props.description}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={uploading || action.isPending} onClick={send}>
            {props.submit}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label={props.noteLabel} required={props.noteRequired} error={error}>
          {(p) => <Textarea {...p} autoFocus value={note} onChange={(e) => setNote(e.target.value)} placeholder={props.notePlaceholder} maxLength={2000} />}
        </Field>
        <div className="flex flex-col gap-2">
          <span className="text-sm font-medium">Documents (optional)</span>
          {files.length > 0 && (
            <ul className="flex flex-col gap-1">
              {files.map((f, i) => (
                <li key={`${f.name}-${i}`} className="flex items-center justify-between gap-2 rounded-[var(--radius-control)] bg-muted px-3 py-2 text-sm">
                  <span className="min-w-0 truncate">
                    {f.name} <span className="text-muted-foreground">({formatBytes(f.size)})</span>
                  </span>
                  <button type="button" aria-label={`Remove ${f.name}`} className="inline-flex size-8 items-center justify-center rounded hover:bg-card" onClick={() => setFiles(files.filter((_, j) => j !== i))}>
                    <X aria-hidden className="size-4" />
                  </button>
                </li>
              ))}
            </ul>
          )}
          <DropZone
            multiple
            extensions={documentExtensions}
            what="files"
            label="Attach files"
            files={files}
            icon={<Paperclip aria-hidden className="size-4" />}
            onFiles={(more) => setFiles([...files, ...more])}
          />
          <p className="text-xs text-muted-foreground">They're added to the contract's Documents and linked to this hand-over.</p>
        </div>
      </div>
    </Dialog>
  )
}

function ClearDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Legal cleared"
      description="The contract moves to Signing. The owner and backup get an email, and the approver sees your outcome."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => {
              if (!note.trim()) return setError('Add a short outcome.')
              action.mutate({ path: 'legal/clear', body: { note }, success: 'Cleared. It has moved to Signing.' }, { onError: (e) => setError(errorText(e, 'note')) })
            }}
          >
            Clear it
          </Button>
        </>
      }
    >
      <Field label="Outcome" required error={error} help="A sentence or two for the owner and the approver.">
        {(p) => <Textarea {...p} autoFocus value={note} onChange={(e) => setNote(e.target.value)} placeholder="For example: Cleared. Liability cap agreed at $2m." maxLength={2000} />}
      </Field>
    </Dialog>
  )
}

function StatusDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const [status, setStatus] = useState(data.legal.status === 'WaitingOnSupplier' ? 'WaitingOnSupplier' : 'Reviewing')
  const [note, setNote] = useState(data.legal.note ?? '')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Update status"
      description="Lets the owner see where it's up to. It stays with Legal."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => action.mutate({ path: 'legal/status', body: { status, note }, success: 'Status updated.' }, { onError: (e) => setError(errorText(e, 'note')) })}
          >
            Save status
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <fieldset className="flex flex-col gap-1">
          <legend className="mb-1 text-sm font-medium">Status</legend>
          {[
            { value: 'Reviewing', label: 'Reviewing' },
            { value: 'WaitingOnSupplier', label: 'Waiting on supplier' },
          ].map((o) => (
            <label key={o.value} className="flex min-h-11 cursor-pointer items-center gap-3">
              <input type="radio" name="legal-status" value={o.value} checked={status === o.value} onChange={() => setStatus(o.value)} className="size-5 accent-[var(--primary)]" />
              {o.label}
            </label>
          ))}
        </fieldset>
        <Field label="Short note (optional)" error={error}>
          {(p) => <Textarea {...p} value={note} onChange={(e) => setNote(e.target.value)} placeholder="For example: insurance certificate requested" maxLength={500} />}
        </Field>
      </div>
    </Dialog>
  )
}

function AssignDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const choices = data.legalPeople.filter((p) => p.id !== data.legal.legalPersonId)
  const [personId, setPersonId] = useState(choices[0]?.id ?? 0)
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  const name = choices.find((p) => p.id === personId)?.name
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Give to someone in Legal"
      description="They get an email. Whose turn it is doesn't change."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            disabled={!personId}
            loading={action.isPending}
            onClick={() => action.mutate({ path: 'legal/assign', body: { personId }, success: `Given to ${name}.` }, { onError: (e) => setError(errorText(e, 'personId')) })}
          >
            Give it to them
          </Button>
        </>
      }
    >
      {choices.length === 0 ? (
        <Alert tone="info" title="There's nobody else in Legal to give it to." />
      ) : (
        <Field label="Who" error={error}>
          {(p) => (
            <Select {...p} value={personId} onChange={(e) => setPersonId(Number(e.target.value))}>
              {choices.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
      )}
    </Dialog>
  )
}

function ReadyForSigningDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const [error, setError] = useState<string | undefined>()
  const action = useAction(data, onClose)
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Ready for signing?"
      description="It goes straight to Signing without a Legal review, because Needs Legal review is unticked. The details can't be changed after this."
      footer={
        <>
          <Button onClick={onClose}>Not yet</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => action.mutate({ path: 'ready-for-signing', success: 'It has moved to Signing.' }, { onError: (e) => setError(errorText(e)) })}
          >
            Ready for signing
          </Button>
        </>
      }
    >
      {error ? <Alert tone="danger" title={error} /> : <p className="text-sm">Next you'll sign it in DocuSign and add the signed copy.</p>}
    </Dialog>
  )
}

function SendForApprovalDialog({ data, onClose }: { data: ContractDetail; onClose: () => void }) {
  const c = data.contract
  // Required and never filled in for you (D-61): only what's already on the contract.
  const [effectiveDate, setEffectiveDate] = useState(c.effectiveDate ?? '')
  // Optional: the SAP number often only exists once it's signed (D-124).
  const [sap, setSap] = useState(c.sapOutlineAgreement ?? '')
  const [file, setFile] = useState<File | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [uploading, setUploading] = useState(false)
  const action = useAction(data, onClose)
  const queryClient = useQueryClient()
  // The signed copy already uploaded on an earlier try, so fixing the date and trying again doesn't add a second one.
  const uploaded = useRef<{ file: File; rowVersion: string } | null>(null)
  const send = async () => {
    const problems: Record<string, string> = {}
    if (!data.hasSignedCopy && !file) problems.signedCopy = 'Choose the signed copy (the PDF from DocuSign).'
    if (!effectiveDate) problems.effectiveDate = 'Enter the date the contract takes effect.'
    setErrors(problems)
    if (Object.keys(problems).length > 0) return
    if (file && uploaded.current?.file !== file) {
      setUploading(true)
      try {
        uploaded.current = { file, rowVersion: (await uploadDocument(c.id, file, 'SignedCopy')).rowVersion }
      } catch (e) {
        return setErrors({ signedCopy: errorText(e) })
      } finally {
        setUploading(false)
        void queryClient.invalidateQueries({ queryKey: ['contract', c.id] })
      }
    }
    action.mutate(
      { path: 'send-for-approval', body: { effectiveDate, ...(data.sapLabel ? { sapOutlineAgreement: sap } : {}) }, rowVersion: uploaded.current?.rowVersion, success: 'Sent for approval. The Group Head has been emailed.' },
      { onError: (e) => setErrors(e instanceof ApiError && Object.keys(e.fields).length > 0 ? e.fields : { form: errorText(e) }) },
    )
  }
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Send for approval"
      description="The Group Head checks the signed copy and details, then approves it to make it Effective."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={uploading || action.isPending} onClick={send}>
            Send for approval
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {errors.form && <Alert tone="danger" title={errors.form} />}
        {data.hasSignedCopy ? (
          <p className="flex items-center gap-2 text-sm">
            <CheckCircle2 aria-hidden className="size-4 text-success" /> The signed copy is on the contract.
          </p>
        ) : (
          <Field label="Signed copy" required error={errors.signedCopy} help="The signed PDF from DocuSign. It's added to the contract's Documents.">
            {() => (
              <DropZone extensions={['.pdf', '.doc', '.docx', '.png', '.jpg', '.jpeg']} what="the signed copy (PDF, Word or image)" label="Choose the signed copy" file={file} onFile={setFile} />
            )}
          </Field>
        )}
        <Field label="Effective date" required error={errors.effectiveDate} help="The date the contract takes effect, as signed.">
          {(p) => <TextInput {...p} type="date" value={effectiveDate} onChange={(e) => setEffectiveDate(e.target.value)} className="max-w-56" />}
        </Field>
        {data.sapLabel && (
          <Field label={`${data.sapLabel} (optional)`} error={errors.sapOutlineAgreement} help="Add it now if you have it, or later on the contract's page.">
            {(p) => <TextInput {...p} value={sap} onChange={(e) => setSap(e.target.value)} maxLength={50} className="max-w-72" />}
          </Field>
        )}
      </div>
    </Dialog>
  )
}

/** A step that needs a reason in a sentence (send back, back to draft). */
export function ReasonDialog(props: {
  data: ContractDetail
  onClose: () => void
  title: string
  description: string
  label: string
  placeholder: string
  path: string
  submit: string
  success: string
  onDone?: () => void
}) {
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | undefined>()
  const action = useAction(props.data, () => {
    props.onClose()
    props.onDone?.()
  })
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && props.onClose()}
      title={props.title}
      description={props.description}
      footer={
        <>
          <Button onClick={props.onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={action.isPending}
            onClick={() => {
              if (!note.trim()) return setError('Add a reason.')
              action.mutate({ path: props.path, body: { note }, success: props.success }, { onError: (e) => setError(errorText(e, 'note')) })
            }}
          >
            {props.submit}
          </Button>
        </>
      }
    >
      <Field label={props.label} required error={error}>
        {(p) => <Textarea {...p} autoFocus value={note} onChange={(e) => setNote(e.target.value)} placeholder={props.placeholder} maxLength={2000} />}
      </Field>
    </Dialog>
  )
}

const entryText: Record<LegalEntry['action'], (e: LegalEntry) => string> = {
  SentToLegal: (e) => `${e.byName} sent it to Legal${e.legalPersonName ? ` (${e.legalPersonName})` : ''}`,
  Taken: (e) => `${e.byName} took it`,
  Assigned: (e) => (e.legalPersonName === e.byName ? `${e.byName} took it` : `${e.byName} gave it to ${e.legalPersonName}`),
  StatusChanged: (e) => `${e.byName} set the status to ${e.statusName}`,
  SentToOwner: (e) => `${e.byName} sent it back to the owner`,
  Cleared: (e) => `${e.byName} cleared it`,
}

export function ConversationCard({ data, recordHref }: { data: ContractDetail; recordHref?: string }) {
  const { person } = useMe()
  if (data.conversation.length === 0) return null
  // The Legal record (D-122): a ZIP of a Word record and the conversation's files, for Legal's matter system. Legal and admins.
  const canDownload = recordHref && (person.roles.legal || person.roles.admin)
  return (
    <Card className="p-5 md:p-6">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
          <MessagesSquare aria-hidden className="size-5" /> Legal conversation
          <span className="tabular text-sm font-normal text-muted-foreground">({data.conversation.length})</span>
        </h2>
        {canDownload && (
          <a
            href={recordHref}
            className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] px-3 text-sm font-medium text-primary hover:bg-muted"
            title="A Word record of the conversation with the contract's basic details, and the attached files, in one ZIP"
          >
            <FileDown aria-hidden className="size-4" /> Download for your matter file
          </a>
        )}
      </div>
      <ol className="mt-4 flex flex-col gap-4">
        {data.conversation.map((e) => (
          <li key={e.number} className="flex gap-3">
            <span aria-hidden className="tabular inline-flex size-7 shrink-0 items-center justify-center rounded-full bg-muted text-xs font-semibold text-muted-foreground">
              {e.number}
            </span>
            <div className="flex min-w-0 flex-col gap-1">
              <p className="text-sm">
                <span className="sr-only">{e.number}. </span>
                <span className="font-medium">{entryText[e.action](e)}</span>{' '}
                <span className="text-muted-foreground">· {formatDateTime(e.atUtc)}</span>
              </p>
              {e.note && <blockquote className="border-l-2 border-primary/40 pl-3 text-sm whitespace-pre-line">{e.note}</blockquote>}
              {e.documents.length > 0 && (
                <ul className="flex flex-wrap gap-2">
                  {e.documents.map((d) => (
                    <li key={d.versionId}>
                      <a href={`/api/documents/${d.versionId}/file`} className="inline-flex items-center gap-1 text-sm text-primary hover:underline">
                        <Paperclip aria-hidden className="size-3.5" />
                        {d.fileName} <span className="text-muted-foreground">(version {d.versionNumber})</span>
                      </a>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </li>
        ))}
      </ol>
    </Card>
  )
}
