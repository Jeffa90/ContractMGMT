import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BackLink } from '@/components/BackLink'
import { Check, Download, FileText, History, Lock, Pencil, Sparkles, Trash2, Upload } from 'lucide-react'
import { useRef, useState, type ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { DropTarget } from '@/components/DropZone'
import { Select, StageBadge, Textarea } from '@/components/form'
import { HelpButton } from '@/components/Help'
import { ConversationCard, ProgressPanel } from '@/components/LegalReview'
import { ChangesCard, UnlockBanner } from '@/components/LockedChanges'
import { RenewalCard } from '@/components/Renewal'
import { Alert, Button, Card, EmptyState, Field, Skeleton } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import { cn } from '@/lib/cn'
import { documentKinds, journey, uploadDocument, withAddedKinds, type Contract, type ContractDetail, type ContractDocument, type Lists } from '@/lib/contracts'
import { daysUntil, formatBytes, formatDate, formatDateTime, formatMoney, timeAgo } from '@/lib/format'
import { arrange, type Layout, type LayoutField } from '@/lib/layout'
import { useMe } from '@/lib/me'
import { undoApproval } from '@/lib/actions'
import { help } from '@/help'

function Tracker({ c }: { c: Contract }) {
  const current = journey.findIndex((j) => j.stage === c.stage)
  return (
    <ol className="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-0" aria-label="Where this contract is up to">
      {journey.map((j, i) => {
        const done = current >= 0 && i < current
        const here = i === current
        return (
          <li key={j.stage} className="flex items-center sm:flex-1" aria-current={here ? 'step' : undefined}>
            <span
              className={cn(
                'inline-flex size-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold',
                done && 'border-primary bg-primary text-primary-foreground',
                here && 'border-primary bg-primary-soft text-primary-soft-foreground',
                !done && !here && 'border-border text-muted-foreground',
              )}
            >
              {done ? <Check aria-hidden className="size-4" /> : i + 1}
            </span>
            <span className={cn('ml-2 text-sm', here ? 'font-semibold' : 'text-muted-foreground')}>
              {j.label}
              {here && <span className="sr-only"> (current)</span>}
              {done && <span className="sr-only"> (done)</span>}
            </span>
            {i < journey.length - 1 && <span aria-hidden className="mx-3 hidden h-px flex-1 bg-border sm:block" />}
          </li>
        )
      })}
    </ol>
  )
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5 py-2 sm:flex-row sm:gap-4">
      <dt className="text-sm text-muted-foreground sm:w-56 sm:shrink-0">{label}</dt>
      <dd className="text-sm">{children || <span className="text-muted-foreground">Not filled in</span>}</dd>
    </div>
  )
}

/** Plain words for a built-in field on the contract page. */
function builtInDisplay(key: string, c: Contract): ReactNode {
  switch (key) {
    case 'title': return c.title
    case 'supplier': return c.supplierName
    case 'category':
      // The main category, then any others (D-115).
      return c.categoryDisplay && (
        <>
          {c.categoryDisplay.replaceAll(' > ', ' › ')}
          {c.otherCategories.length > 0 && (
            <span className="mt-1 block text-muted-foreground">Also: {c.otherCategories.map((o) => o.replaceAll(' > ', ' › ')).join('; ')}</span>
          )}
        </>
      )
    case 'agreementType': return c.agreementTypeName
    case 'description': return c.description && <span className="whitespace-pre-wrap">{c.description}</span>
    case 'owner': return c.ownerName
    case 'backupOwner': return c.backupOwnerName
    case 'confidential': return c.isConfidential ? 'Yes: only the owner and backup, Legal, approvers, admins and auditors see it' : 'No'
    case 'businessUnit': return c.businessUnitName
    case 'site': return c.siteName
    case 'entity': return c.entityName
    case 'function': return c.functionName
    case 'value': return c.value !== null && <span className="tabular">{formatMoney(c.value, c.currencyCode)}</span>
    case 'sapOutlineAgreement': return c.sapOutlineAgreement
    case 'startDate': return formatDate(c.startDate)
    case 'effectiveDate': return formatDate(c.effectiveDate)
    case 'endDate': return c.isEvergreen ? 'Evergreen (no end date)' : formatDate(c.endDate)
    case 'evergreen': return c.isEvergreen ? 'Yes' : 'No'
    case 'autoRenews': return c.autoRenews ? 'Yes: if no one gives notice, the end date moves on by the renewal term' : 'No'
    case 'renewalTermMonths': return c.autoRenews && c.renewalTermMonths !== null ? (c.renewalTermMonths === 1 ? '1 month' : `${c.renewalTermMonths} months`) : ''
    case 'noticePeriodDays': return c.noticePeriodDays !== null ? `${c.noticePeriodDays} days${c.noticeDeadline ? ` (notice by ${formatDate(c.noticeDeadline)})` : ''}` : ''
    case 'reviewDate': return c.reviewMonthsBeforeEnd ? `${formatDate(c.reviewDate) || 'Once there is an end date'} (${c.reviewMonthsBeforeEnd === 1 ? '1 month' : `${c.reviewMonthsBeforeEnd} months`} before the end date)` : formatDate(c.reviewDate)
    case 'needsLegalReview': return c.needsLegalReview ? 'Yes' : 'No'
    default: return null
  }
}

function FieldDisplay({ field, display }: { field: LayoutField; display: string }) {
  if (field.kind === 'Link') return <a href={display} target="_blank" rel="noopener noreferrer" className="break-all text-primary hover:underline">{display}</a>
  if (field.kind === 'Email') return <a href={`mailto:${display}`} className="text-primary hover:underline">{display}</a>
  if (field.kind === 'LongText') return <span className="whitespace-pre-wrap">{display}</span>
  return <>{display}</>
}

function DetailSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card className="p-5 md:p-6">
      <h2 className="font-display text-lg font-semibold">{title}</h2>
      <dl className="mt-2 divide-y divide-border">{children}</dl>
    </Card>
  )
}

export function ContractDetailPage() {
  const { id } = useParams()
  const contractId = Number(id)
  const detail = useQuery({ queryKey: ['contract', contractId], queryFn: () => api.get<ContractDetail>(`/api/contracts/${contractId}`) })
  const layout = useQuery({ queryKey: ['layout'], queryFn: () => api.get<Layout>('/api/layout') })

  if (detail.isPending) {
    return (
      <div className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-4 w-32" />
        <Skeleton className="h-8 w-80" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    )
  }
  if (detail.isError) {
    const error = detail.error as ApiError
    return (
      <div className="flex flex-col gap-4">
        <BackLink fallback="/contracts" />
        <Alert tone={error.status === 404 ? 'warning' : 'danger'} title={error.message} />
      </div>
    )
  }
  if (layout.isPending) return <Skeleton className="h-64 w-full" />
  return <Detail data={detail.data} layout={layout.data ?? { versionId: 0, sections: [], fields: [] }} />
}

function Detail({ data, layout }: { data: ContractDetail; layout: Layout }) {
  const c = data.contract
  // From the Renewals page: open the decision straight away.
  const [params] = useSearchParams()
  const openDecision = params.get('decide') === '1'
  const [withdrawing, setWithdrawing] = useState(false)
  const notice = c.noticeDeadline ? daysUntil(c.noticeDeadline) : null

  return (
    <>
      <BackLink fallback="/contracts" />

      <header className="flex flex-col gap-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h1 className="font-display text-xl font-semibold md:text-2xl">{c.title}</h1>
              <HelpButton topic={help.contract} />
            </div>
            <p className="mt-1 flex flex-wrap items-center gap-2 text-muted-foreground">
              <span>{c.reference}{c.legacyReference ? ` · old reference ${c.legacyReference}` : ""}</span>
              {c.renewsContractId && (
                <Link to={`/contracts/${c.renewsContractId}`} className="text-primary hover:underline">
                  Renews {c.renewsReference}
                </Link>
              )}
              {c.isConfidential && (
                <span className="inline-flex items-center gap-1">
                  <Lock aria-hidden className="size-4" /> Confidential
                </span>
              )}
              {c.isDemo && (
                <span className="inline-flex items-center gap-1" title="Made-up demo data for trying the app on QA. It never sends email.">
                  <Sparkles aria-hidden className="size-4" /> Demo (no emails)
                </span>
              )}
            </p>
          </div>
          {data.canEdit && (
            <Link to={`/contracts/${c.id}/edit`} className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] border border-input bg-card px-4 font-medium hover:bg-muted">
              <Pencil aria-hidden className="size-4" /> Edit details
            </Link>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <StageBadge stage={c.stage} name={c.stageName} />
          <span className="text-muted-foreground">
            since {formatDateTime(c.stageSinceUtc)} ({timeAgo(c.stageSinceUtc)})
          </span>
        </div>
      </header>

      <UnlockBanner data={data} />
      {data.undoApprovalUntilUtc && <UndoApproval data={data} until={data.undoApprovalUntilUtc} />}
      {c.stage === 'Withdrawn' && <Alert tone="info" title="This contract was withdrawn.">{c.withdrawnReason}</Alert>}
      {data.sentBack && (
        <Alert tone="warning" title={`${data.sentBack.byName} sent it back from approval, ${formatDateTime(data.sentBack.atUtc)}`}>
          {data.sentBack.reason}
        </Alert>
      )}
      {notice !== null && c.stage === 'Effective' && !data.renewal.decision && notice <= 30 && (
        <Alert tone={notice < 0 ? 'danger' : 'warning'} title={notice < 0 ? `The notice deadline passed ${-notice} days ago (${formatDate(c.noticeDeadline)}).` : `The notice deadline is in ${notice} days (${formatDate(c.noticeDeadline)}).`} />
      )}

      {c.stage !== 'Withdrawn' && <ProgressPanel data={data} tracker={<Tracker c={c} />} />}

      <RenewalCard data={data} openDecision={openDecision} />

      <ChangesCard data={data} layout={layout} />

      <Card className="p-5 md:p-6">
        <h2 className="sr-only">Essentials</h2>
        <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <dt className="text-xs text-muted-foreground">Supplier</dt>
            <dd className="font-medium">{c.supplierName ?? 'Not chosen'}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Owner</dt>
            <dd className="font-medium">
              {c.ownerName}
              {c.backupOwnerName && <span className="block text-xs font-normal text-muted-foreground">Backup: {c.backupOwnerName}</span>}
            </dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Value</dt>
            <dd className="tabular font-medium">{c.value !== null ? formatMoney(c.value, c.currencyCode) : 'Not filled in'}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Key dates</dt>
            <dd className="tabular font-medium">
              {c.isEvergreen ? 'Evergreen' : c.endDate ? `${c.autoRenews ? 'Renews' : 'Ends'} ${formatDate(c.endDate)}` : 'No end date yet'}
              {c.noticeDeadline && <span className="block text-xs font-normal text-muted-foreground">Notice by {formatDate(c.noticeDeadline)}</span>}
            </dd>
          </div>
        </dl>
      </Card>

      <ConversationCard data={data} />

      <DocumentsCard data={data} />

      {arrange(layout).map(({ section, fields }) => (
        <DetailSection key={section.key} title={section.title}>
          {fields.map((f) => (
            <Row key={f.key} label={f.label}>
              {f.isBuiltIn ? builtInDisplay(f.key, c) : data.fieldValues[f.key]?.display ? <FieldDisplay field={f} display={data.fieldValues[f.key].display} /> : null}
            </Row>
          ))}
        </DetailSection>
      ))}

      <ActivityCard data={data} />

      {data.canWithdraw && (
        <div>
          <Button variant="ghost" className="text-danger" icon={<Trash2 aria-hidden className="size-4" />} onClick={() => setWithdrawing(true)}>
            Withdraw this contract
          </Button>
        </div>
      )}
      {withdrawing && <WithdrawDialog c={c} onClose={() => setWithdrawing(false)} />}
    </>
  )
}

function DocumentsCard({ data }: { data: ContractDetail }) {
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists') })
  const kinds = withAddedKinds(documentKinds, lists.data)
  const c = data.contract
  const queryClient = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const { person } = useMe()
  // Legal mostly adds mark-ups; contract owners mostly add drafts.
  const [kind, setKind] = useState(person.roles.legal && !person.roles.contractOwner ? 'LegalMarkup' : 'Draft')
  const [newVersionOf, setNewVersionOf] = useState<ContractDocument | null>(null)
  const [error, setError] = useState<string | null>(null)
  const upload = useMutation({
    mutationFn: (file: File) => uploadDocument(c.id, file, kind, newVersionOf?.id),
    onSuccess: (result) => {
      setError(null)
      queryClient.invalidateQueries({ queryKey: ['contract', c.id] })
      toast.success(result.version > 1 ? `Version ${result.version} added.` : 'Document added.')
    },
    onError: (e) => setError(e instanceof ApiError ? (Object.values(e.fields)[0] ?? e.message) : 'The upload failed. Try again.'),
    onSettled: () => {
      setNewVersionOf(null)
      if (input.current) input.current.value = ''
    },
  })
  const remove = useMutation({
    mutationFn: (doc: ContractDocument) => api.post(`/api/contracts/${c.id}/documents/${doc.id}/remove`, {}),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['contract', c.id] })
      toast.success('Document removed. Its file and history are kept.')
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : 'That didn\'t work. Try again.'),
  })
  const pick = (doc: ContractDocument | null) => {
    setNewVersionOf(doc)
    input.current?.click()
  }
  // Files dragged onto the card go in one after another, as the kind chosen (D-96).
  const addDropped = async (files: File[]) => {
    setNewVersionOf(null)
    for (const f of files) {
      try {
        await upload.mutateAsync(f)
      } catch {
        return // the error is shown on the card
      }
    }
  }
  const kindName = kinds.find((k) => k.value === kind)?.label ?? 'a document'

  return (
    <DropTarget enabled={data.canUpload} message={`Drop to add as ${kindName}`} onFiles={(f) => void addDropped(f)}>
    <Card className="p-5 md:p-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 className="font-display text-lg font-semibold">Documents</h2>
          {data.canUpload && <p className="hidden text-sm text-muted-foreground md:block">You can also drag files onto this card.</p>}
        </div>
        {data.canUpload && (
          <div className="flex flex-wrap items-end gap-2">
            <Field label="Kind">
              {(p) => (
                <Select {...p} value={kind} onChange={(e) => setKind(e.target.value)} className="min-w-52">
                  {kinds.map((k) => (
                    <option key={k.value} value={k.value}>
                      {k.label}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Button variant="secondary" icon={<Upload aria-hidden className="size-4" />} onClick={() => pick(null)} loading={upload.isPending && !newVersionOf}>
              Add document
            </Button>
          </div>
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
        }}
      />
      {error && (
        <div className="mt-3">
          <Alert tone="danger" title={error} />
        </div>
      )}
      {data.documents.length === 0 ? (
        <EmptyState icon={<FileText />} title={data.canUpload ? 'No documents yet. Add the draft agreement and anything else that goes with it. PDF, Word, Excel, email or images up to 25 MB.' : 'No documents yet.'} />
      ) : (
        <ul className="mt-4 divide-y divide-border">
          {data.documents.map((d) => {
            const latest = d.versions[0]
            return (
              <li key={d.id} className="flex flex-col gap-2 py-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <a href={`/api/documents/${latest.id}/file`} className="font-medium text-primary hover:underline">
                    {d.title}
                  </a>
                  <p className="text-xs text-muted-foreground">
                    {d.kindName}{d.variationNumber ? ` (Variation ${d.variationNumber})` : ''} · version {d.currentVersion} · {formatBytes(latest.sizeBytes)} · {latest.uploadedByName}, {formatDateTime(latest.uploadedUtc)}
                  </p>
                  {d.versions.length > 1 && (
                    <details className="mt-1 text-xs">
                      <summary className="cursor-pointer text-muted-foreground">Earlier versions ({d.versions.length - 1})</summary>
                      <ul className="mt-1 flex flex-col gap-1">
                        {d.versions.slice(1).map((v) => (
                          <li key={v.id}>
                            <a href={`/api/documents/${v.id}/file`} className="text-primary hover:underline">
                              Version {v.versionNumber}: {v.fileName}
                            </a>{' '}
                            <span className="text-muted-foreground">({v.uploadedByName}, {formatDateTime(v.uploadedUtc)})</span>
                          </li>
                        ))}
                      </ul>
                    </details>
                  )}
                </div>
                <div className="flex shrink-0 gap-1">
                  <a href={`/api/documents/${latest.id}/file`} className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] px-3 text-sm font-medium hover:bg-muted" aria-label={`Download ${d.title}`}>
                    <Download aria-hidden className="size-4" /> Download
                  </a>
                  {data.canUpload && (
                    <>
                      <Button variant="ghost" onClick={() => pick(d)} loading={upload.isPending && newVersionOf?.id === d.id} aria-label={`Upload a new version of ${d.title}`}>
                        New version
                      </Button>
                      <Button variant="ghost" className="text-danger" onClick={() => remove.mutate(d)} aria-label={`Remove ${d.title}`}>
                        Remove
                      </Button>
                    </>
                  )}
                </div>
              </li>
            )
          })}
        </ul>
      )}
    </Card>
    </DropTarget>
  )
}

function ActivityCard({ data }: { data: ContractDetail }) {
  const [showAll, setShowAll] = useState(false)
  const items = showAll ? data.activity : data.activity.slice(0, 8)
  return (
    <Card className="p-5 md:p-6">
      <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
        <History aria-hidden className="size-5" /> Activity
      </h2>
      <ol className="mt-3 flex flex-col gap-3">
        {items.map((a) => (
          <li key={a.id} className="flex flex-col border-l-2 border-border pl-3">
            <span className="text-sm">{a.summary}</span>
            <span className="text-xs text-muted-foreground">{formatDateTime(a.occurredUtc)}</span>
          </li>
        ))}
      </ol>
      {data.activity.length > 8 && !showAll && (
        <Button variant="link" className="mt-3" onClick={() => setShowAll(true)}>
          Show all ({data.activity.length})
        </Button>
      )}
    </Card>
  )
}

function WithdrawDialog({ c, onClose }: { c: Contract; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | undefined>()
  const withdraw = useMutation({
    mutationFn: () => api.post(`/api/contracts/${c.id}/withdraw`, { reason, rowVersion: c.rowVersion }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['contract', c.id] })
      queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success('Contract withdrawn.')
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? (e.fields.reason ?? e.message) : 'That didn\'t work. Try again.'),
  })
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={`Withdraw ${c.title}?`}
      description="It stops here and can't be sent on. Its documents and history are kept."
      footer={
        <>
          <Button onClick={onClose}>Keep it</Button>
          <Button variant="danger" loading={withdraw.isPending} onClick={() => withdraw.mutate()}>
            Withdraw
          </Button>
        </>
      }
    >
      <Field label="Why is it being withdrawn?" required error={error}>
        {(p) => <Textarea {...p} autoFocus value={reason} onChange={(e) => setReason(e.target.value)} placeholder="For example: the supplier pulled out" />}
      </Field>
    </Dialog>
  )
}

/** For the approver who approved it, for 24 hours and before anything else happens to it (D-106). */
function UndoApproval({ data, until }: { data: ContractDetail; until: string }) {
  const queryClient = useQueryClient()
  const [busy, setBusy] = useState(false)
  return (
    <Alert tone="info" title="You approved this. Approved by mistake?">
      <div className="flex flex-col items-start gap-2">
        <p>You can undo your approval until {formatDateTime(until)}, as long as nothing else happens to it first. It goes back to waiting for approval.</p>
        <Button
          loading={busy}
          onClick={async () => {
            setBusy(true)
            await undoApproval(queryClient, data.contract.id, data.contract.rowVersion)
            setBusy(false)
          }}
        >
          Undo approval
        </Button>
      </div>
    </Alert>
  )
}
