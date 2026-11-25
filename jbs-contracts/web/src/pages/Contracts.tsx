import { useQuery } from '@tanstack/react-query'
import { FilePlus2, FileText, Lock, Search } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { ExcelButtons } from '@/components/ExcelImport'
import { StageBadge } from '@/components/form'
import { PageHeader } from '@/components/Help'
import { Alert, Card, EmptyState, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api } from '@/lib/api'
import type { ContractListItem } from '@/lib/contracts'
import { daysUntil, formatDate } from '@/lib/format'
import { useMe } from '@/lib/me'
import { help } from '@/help'

type View = 'mine' | 'all' | 'drafts' | 'closed'

/** The one date that matters most: the notice deadline, else the end date. */
function KeyDate({ c }: { c: ContractListItem }) {
  if (c.stage === 'Withdrawn') return <span className="text-muted-foreground">Withdrawn</span>
  const [label, value] = c.noticeDeadline ? ['Notice', c.noticeDeadline] : c.endDate ? ['Ends', c.endDate] : c.isEvergreen ? ['Evergreen', null] : [null, null]
  if (!label) return <span className="text-muted-foreground">No dates yet</span>
  if (!value) return <span>{label}</span>
  const days = c.stage === 'Effective' ? daysUntil(value) : Infinity // deadlines only matter once Effective
  return (
    <span className={days < 0 ? 'font-medium text-danger' : days <= 30 ? 'font-medium text-warning' : undefined}>
      {label} {formatDate(value)}
    </span>
  )
}

export function ContractsPage() {
  const { person } = useMe()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const view = (params.get('view') as View) || (person.roles.contractOwner ? 'mine' : 'all')
  const q = params.get('q') ?? ''
  const [search, setSearch] = useState(q)

  // Search as you type (a short pause), kept in the address so Back returns to the same list.
  useEffect(() => {
    const t = setTimeout(() => {
      if (search !== q) setParams((p) => { const n = new URLSearchParams(p); if (search) n.set('q', search); else n.delete('q'); return n }, { replace: true })
    }, 250)
    return () => clearTimeout(t)
  }, [search, q, setParams])

  const contracts = useQuery({
    queryKey: ['contracts', view, q],
    queryFn: () => api.get<ContractListItem[]>(`/api/contracts?view=${view}&q=${encodeURIComponent(q)}`),
    placeholderData: (previous) => previous,
  })
  const rows = contracts.data ?? []

  return (
    <>
      <PageHeader
        title="Contracts"
        description="Every contract at JBS. Click one to open it."
        help={help.contracts}
        action={
          person.roles.contractOwner ? (
            <Link
              to="/contracts/new"
              className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] bg-primary px-4 font-medium text-primary-foreground hover:bg-primary-hover"
            >
              <FilePlus2 aria-hidden className="size-5" />
              Add contract
            </Link>
          ) : undefined
        }
      />

      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex flex-col gap-3 md:flex-row md:items-center">
          <div className="relative md:w-80">
            <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-5 -translate-y-1/2 text-muted-foreground" />
            <TextInput type="search" aria-label="Find a contract" placeholder="Title, reference, supplier or SAP number" value={search} onChange={(e) => setSearch(e.target.value)} className="pl-10" />
          </div>
          <Segmented
            label="Show"
            value={view}
            onChange={(v) => setParams((p) => { const n = new URLSearchParams(p); n.set('view', v); return n })}
            options={[
              ...(person.roles.contractOwner ? [{ value: 'mine' as View, label: 'My contracts' }] : []),
              { value: 'all', label: 'All' },
              { value: 'drafts', label: 'Drafts' },
              { value: 'closed', label: 'Closed' },
            ]}
          />
        </div>
        <ExcelButtons kind="contracts" canImport={person.roles.contractOwner} invalidate={[['contracts'], ['suppliers'], ['contract']]} />
      </div>

      {contracts.isPending ? (
        <Card className="flex flex-col gap-4 p-4">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="flex items-center gap-4">
              <Skeleton className="h-4 w-64" />
              <Skeleton className="h-4 w-32" />
              <Skeleton className="h-5 w-24 rounded-full" />
            </div>
          ))}
        </Card>
      ) : contracts.isError ? (
        <Alert tone="danger" title="We couldn't load contracts.">{(contracts.error as Error).message}</Alert>
      ) : rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<FileText />}
            title={
              q
                ? `No contracts match "${q}".`
                : view === 'mine'
                  ? "You don't own any contracts yet. Contracts you add, or are made owner or backup of, appear here."
                  : view === 'drafts'
                    ? 'No drafts right now.'
                    : view === 'closed'
                      ? 'No closed contracts yet.'
                      : 'No contracts yet. Contracts you add will appear here.'
            }
            action={
              person.roles.contractOwner && !q && view !== 'closed' ? (
                <Link to="/contracts/new" className="inline-flex min-h-11 items-center rounded-[var(--radius-control)] bg-primary px-4 font-medium text-primary-foreground hover:bg-primary-hover">
                  Add contract
                </Link>
              ) : undefined
            }
          />
        </Card>
      ) : (
        <>
          <Card className="hidden overflow-hidden md:block">
            <table className="w-full text-left">
              <thead className="border-b border-border bg-muted/50 text-xs text-muted-foreground">
                <tr>
                  <th scope="col" className="px-4 py-3 font-medium">Contract</th>
                  <th scope="col" className="px-4 py-3 font-medium">Supplier</th>
                  <th scope="col" className="px-4 py-3 font-medium">Stage</th>
                  <th scope="col" className="px-4 py-3 font-medium">Owner</th>
                  <th scope="col" className="px-4 py-3 font-medium">Key date</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((c) => (
                  <tr key={c.id} className="cursor-pointer border-b border-border last:border-0 hover:bg-muted/50" onClick={() => navigate(`/contracts/${c.id}`)}>
                    <td className="px-4 py-3">
                      <Link to={`/contracts/${c.id}`} className="font-medium hover:underline" onClick={(e) => e.stopPropagation()}>
                        {c.title}
                      </Link>
                      <span className="flex items-center gap-1 text-xs text-muted-foreground">
                        {c.reference}
                        {c.isConfidential && (
                          <>
                            {' · '}
                            <Lock aria-hidden className="size-3" /> Confidential
                          </>
                        )}
                      </span>
                    </td>
                    <td className="px-4 py-3">{c.supplierName ?? <span className="text-muted-foreground">Not chosen</span>}</td>
                    <td className="px-4 py-3">
                      <StageBadge stage={c.stage} name={c.stageName} />
                    </td>
                    <td className="px-4 py-3">{c.ownerName}</td>
                    <td className="tabular px-4 py-3">
                      <KeyDate c={c} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
          <div className="flex flex-col gap-2 md:hidden">
            {rows.map((c) => (
              <Link key={c.id} to={`/contracts/${c.id}`} className="block rounded-[var(--radius-card)] border border-border bg-card p-4">
                <span className="flex items-start justify-between gap-2">
                  <span className="font-medium">{c.title}</span>
                  <StageBadge stage={c.stage} name={c.stageName} />
                </span>
                <span className="mt-1 block text-xs text-muted-foreground">
                  {c.reference} · {c.supplierName ?? 'No supplier yet'}
                </span>
                <span className="tabular mt-2 block text-sm">
                  <KeyDate c={c} />
                </span>
              </Link>
            ))}
          </div>
          <p className="text-xs text-muted-foreground">
            {rows.length} {rows.length === 1 ? 'contract' : 'contracts'}
          </p>
        </>
      )}
    </>
  )
}
