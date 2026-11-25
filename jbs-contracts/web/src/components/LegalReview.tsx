// Where a contract is up to and what happens next, with the actions for the person looking (product-spec sections 4, 5 and 13.1),
// and the Legal conversation: every hand-over, numbered, as it was.
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowLeftRight, CheckCircle2, MessagesSquare, Paperclip, Scale, Send, UserCheck, X } from 'lucide-react'
import { useRef, useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { uploadDocument, type ContractAction, type ContractDetail, type LegalEntry } from '@/lib/contracts'
import { formatBytes, formatDateTime, timeAgo } from '@/lib/format'
import { Dialog } from './Dialog'
import { Select, Textarea } from './form'
import { Alert, Badge, Button, Card, Field } from './ui'

type Dialogs = 'sendToLegal' | 'sendToOwner' | 'clear' | 'status' | 'assign' | 'readyForSigning' | null

/** Runs a contract action with the page's row version, then refreshes the contract and lists. */
function useAction(data: ContractDetail, onDone?: () => void) {
  const queryClient = useQueryClient()
  const c = data.contract
  return useMutation({
    mutationFn: ({ path, body, rowVersion }: { path: string; body?: object; success: string; rowVersion?: string }) =>
      api.post(`/api/contracts/${c.id}/${path}`, { ...body, rowVersion: rowVersion ?? c.rowVersion }),
    onSuccess: (_, v) => {
      queryClient.invalidateQueries({ queryKey: ['contract', c.id] })
      queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success(v.success)
      onDone?.()
    },
  })
}

function errorText(e: unknown, field?: string) {
  if (e instanceof ApiError) return (field && e.fields[field]) || e.message
  return "That didn't work. Try again."
}

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
          <strong>Next:</strong> sign it in DocuSign, then add the signed copy under Documents (kind: Signed copy). After that it goes to the Group Head for approval.
        </p>
      </div>
    )
  return null
}

export function ProgressPanel({ data, tracker }: { data: ContractDetail; tracker: ReactNode }) {
  const [open, setOpen] = useState<Dialogs>(null)
  const take = useAction(data)
  const has = (a: ContractAction) => data.actions.includes(a)
  const close = () => setOpen(null)
  // At most one primary button: the step that moves the contract on.
  const primary: ContractAction | undefined = (['sendToLegal', 'readyForSigning', 'take', 'clear'] as const).find(has)

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
  const input = useRef<HTMLInputElement>(null)
  const action = useAction(data, onClose)

  const send = async () => {
    setError(undefined)
    if (props.noteRequired && !note.trim()) return setError(`Add a note: ${props.noteLabel.toLowerCase()}.`)
    const documentVersionIds: number[] = []
    let rowVersion: string | undefined
    if (files.length > 0) {
      setUploading(true)
      try {
        // Uploaded first, one at a time (they stay on the contract), then linked to this hand-over.
        for (const f of files) {
          const uploaded = await uploadDocument(data.contract.id, f, props.documentKind)
          documentVersionIds.push(uploaded.versionId)
          rowVersion = uploaded.rowVersion
        }
      } catch (e) {
        setUploading(false)
        return setError(errorText(e))
      }
      setUploading(false)
    }
    action.mutate({ path: props.path, body: { note, documentVersionIds }, rowVersion, success: props.success }, { onError: (e) => setError(errorText(e, 'note')) })
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
          <div>
            <Button variant="secondary" icon={<Paperclip aria-hidden className="size-4" />} onClick={() => input.current?.click()}>
              Attach files
            </Button>
          </div>
          <input
            ref={input}
            type="file"
            multiple
            hidden
            accept=".pdf,.doc,.docx,.xls,.xlsx,.msg,.eml,.txt,.csv,.png,.jpg,.jpeg"
            onChange={(e) => {
              setFiles([...files, ...Array.from(e.target.files ?? [])])
              e.target.value = ''
            }}
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

const entryText: Record<LegalEntry['action'], (e: LegalEntry) => string> = {
  SentToLegal: (e) => `${e.byName} sent it to Legal${e.legalPersonName ? ` (${e.legalPersonName})` : ''}`,
  Taken: (e) => `${e.byName} took it`,
  Assigned: (e) => (e.legalPersonName === e.byName ? `${e.byName} took it` : `${e.byName} gave it to ${e.legalPersonName}`),
  StatusChanged: (e) => `${e.byName} set the status to ${e.statusName}`,
  SentToOwner: (e) => `${e.byName} sent it back to the owner`,
  Cleared: (e) => `${e.byName} cleared it`,
}

export function ConversationCard({ data }: { data: ContractDetail }) {
  if (data.conversation.length === 0) return null
  return (
    <Card className="p-5 md:p-6">
      <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
        <MessagesSquare aria-hidden className="size-5" /> Legal conversation
        <span className="tabular text-sm font-normal text-muted-foreground">({data.conversation.length})</span>
      </h2>
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
