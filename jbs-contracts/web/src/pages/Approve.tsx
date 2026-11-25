// Approve (works on a phone): a one-page summary for the Group Head or a backup approver (product-spec section 11; D-59).
// The contract was approved and signed in DocuSign; this confirms it and puts it on the register.
import { useQuery } from '@tanstack/react-query'
import { BadgeCheck, ChevronLeft, Download, FileWarning, Lock, Undo2 } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { StageBadge } from '@/components/form'
import { PageHeader } from '@/components/Help'
import { ReasonDialog } from '@/components/LegalReview'
import { Alert, Button, Card, Skeleton } from '@/components/ui'
import { errorText, useAction } from '@/lib/actions'
import { api, ApiError } from '@/lib/api'
import type { ContractDetail } from '@/lib/contracts'
import { formatDate, formatDateTime, formatMoney } from '@/lib/format'
import { help } from '@/help'

function Item({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5 border-b border-border py-3 last:border-0">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="font-medium">{children}</dd>
    </div>
  )
}

export function ApprovePage() {
  const { id } = useParams()
  const contractId = Number(id)
  const detail = useQuery({ queryKey: ['contract', contractId], queryFn: () => api.get<ContractDetail>(`/api/contracts/${contractId}`) })
  const back = (
    <Link to={`/contracts/${contractId}`} className="inline-flex items-center gap-1 self-start text-sm font-medium text-primary hover:underline">
      <ChevronLeft aria-hidden className="size-4" /> Open the full contract
    </Link>
  )
  if (detail.isPending)
    return (
      <div className="mx-auto flex w-full max-w-xl flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full" />
      </div>
    )
  if (detail.isError)
    return (
      <div className="mx-auto flex w-full max-w-xl flex-col gap-4">
        <Alert tone={(detail.error as ApiError).status === 404 ? 'warning' : 'danger'} title={(detail.error as Error).message} />
      </div>
    )
  return <Approve data={detail.data} back={back} />
}

function Approve({ data, back }: { data: ContractDetail; back: ReactNode }) {
  const c = data.contract
  const navigate = useNavigate()
  const [sendingBack, setSendingBack] = useState(false)
  const approve = useAction(data, () => navigate(`/contracts/${c.id}`))
  const signed = data.documents.filter((d) => d.kind === 'SignedCopy')
  const canAct = data.actions.includes('approve')
  const earlier = data.approvals.filter((a) => a.decision === 'SentBack')

  return (
    <div className="mx-auto flex w-full max-w-xl flex-col gap-4">
      {back}
      <PageHeader title={`Approve ${c.title}`} description={`${c.reference} · ${c.supplierName ?? 'No supplier'}`} help={help.approve} />

      {c.stage !== 'Approval' ? (
        <Alert tone="info" title="This contract isn't waiting for approval.">
          It's in <StageBadge stage={c.stage} name={c.stageName} />
          {c.registeredUtc && ` (registered by ${c.registeredByName}, ${formatDateTime(c.registeredUtc)})`}.
        </Alert>
      ) : data.cannotApproveReason ? (
        <Alert tone="info" title={data.cannotApproveReason} />
      ) : !canAct ? (
        <Alert tone="info" title="Only approvers can approve contracts." />
      ) : (
        <p className="text-sm">
          This contract was approved and signed in DocuSign. Check the signed copy and the details, then approve it to put it on the register as{' '}
          <strong>Effective from {formatDate(c.effectiveDate)}</strong>. It is then locked.
        </p>
      )}

      <Card className="px-5">
        <h2 className="sr-only">Details</h2>
        <dl>
          <Item label="Supplier">{c.supplierName ?? 'Not chosen'}</Item>
          <Item label="Owner">
            {c.ownerName}
            {c.backupOwnerName && <span className="block text-sm font-normal text-muted-foreground">Backup: {c.backupOwnerName}</span>}
          </Item>
          <Item label="Value">{c.value !== null ? formatMoney(c.value, c.currencyCode) : 'Not filled in'}</Item>
          <Item label="Effective date">{formatDate(c.effectiveDate) || 'Not filled in'}</Item>
          <Item label="End date">{c.isEvergreen ? 'Evergreen (no end date)' : formatDate(c.endDate) || 'Not filled in'}</Item>
          {c.noticeDeadline && <Item label="Notice deadline">{formatDate(c.noticeDeadline)}</Item>}
          <Item label="Category">{c.categoryDisplay ?? 'Not chosen'}</Item>
          {c.isConfidential && (
            <Item label="Confidential">
              <span className="inline-flex items-center gap-1">
                <Lock aria-hidden className="size-4" /> Yes
              </span>
            </Item>
          )}
          <Item label="Legal">
            {data.legal.outcome ? (
              <>
                {data.legal.outcome}
                <span className="block text-sm font-normal text-muted-foreground">
                  Cleared by {data.legal.clearedByName}
                  {data.legal.clearedUtc && `, ${formatDateTime(data.legal.clearedUtc)}`}
                </span>
              </>
            ) : (
              <span className="font-normal">No Legal review: the owner unticked Needs Legal review.</span>
            )}
          </Item>
        </dl>
      </Card>

      <Card className="flex flex-col gap-2 p-5">
        <h2 className="font-display text-lg font-semibold">Signed copy</h2>
        {signed.length === 0 ? (
          <p className="flex items-center gap-2 text-sm text-danger">
            <FileWarning aria-hidden className="size-4" /> There's no signed copy on the contract. Send it back.
          </p>
        ) : (
          signed.map((d) => (
            <a
              key={d.id}
              href={`/api/documents/${d.versions[0].id}/file`}
              className="flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] border border-border px-3 font-medium text-primary hover:bg-muted"
            >
              <Download aria-hidden className="size-4 shrink-0" />
              <span className="min-w-0 truncate">{d.versions[0].fileName}</span>
            </a>
          ))
        )}
      </Card>

      {earlier.length > 0 && (
        <Card className="p-5">
          <h2 className="font-display text-lg font-semibold">Sent back before</h2>
          <ul className="mt-2 flex flex-col gap-2 text-sm">
            {earlier.map((a) => (
              <li key={a.atUtc}>
                {a.byName}, {formatDateTime(a.atUtc)}: {a.reason}
              </li>
            ))}
          </ul>
        </Card>
      )}

      {canAct && (
        <div className="sticky bottom-0 -mx-4 flex flex-col gap-2 border-t border-border bg-background p-4 sm:static sm:mx-0 sm:flex-row sm:border-0 sm:bg-transparent sm:p-0">
          <Button
            variant="primary"
            size="lg"
            icon={<BadgeCheck aria-hidden className="size-5" />}
            loading={approve.isPending}
            onClick={() => approve.mutate({ path: 'approval/approve', success: `${c.reference} is approved and Effective.` }, { onError: (e) => toast.error(errorText(e)) })}
          >
            Approve and register
          </Button>
          <Button size="lg" icon={<Undo2 aria-hidden className="size-5" />} onClick={() => setSendingBack(true)}>
            Send back
          </Button>
        </div>
      )}
      {sendingBack && (
        <ReasonDialog
          data={data}
          onClose={() => setSendingBack(false)}
          onDone={() => navigate(`/contracts/${c.id}`)}
          title="Send back"
          description={`It goes back to Signing, and ${c.ownerName} gets an email with your reason.`}
          label="What needs fixing"
          placeholder="For example: the signed copy is missing page 4."
          path="approval/send-back"
          submit="Send back"
          success="Sent back to the owner."
        />
      )}
    </div>
  )
}
