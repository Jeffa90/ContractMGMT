import { useQuery } from '@tanstack/react-query'
import { FilePlus2, FileText, ListFilter, Lock, Search, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { ContractFilters } from '@/components/ContractFilters'
import { ExcelButtons } from '@/components/ExcelImport'
import { StageBadge } from '@/components/form'
import { PageHeader } from '@/components/Help'
import { Alert, Button, Card, EmptyState, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api } from '@/lib/api'
import { chosenFilters, filterFields, filterPrefix } from '@/lib/filters'
import type { HomeData } from '@/lib/home'
import type { Layout } from '@/lib/layout'
import { isContinuing, type ContractListItem } from '@/lib/contracts'
import { daysUntil, formatDate, waited } from '@/lib/format'
import { useMe } from '@/lib/me'
import { help } from '@/help'

type View = 'mine' | 'all' | 'legal' | 'approval' | 'drafts' | 'closed'

/** During Legal review: whose turn and for how long, e.g. "Legal's turn · Sam Lee · 6 days". */
function Turn({ c }: { c: ContractListItem }) {
  if (c.stage !== 'LegalReview' || !c.whoseTurn) return null
  const who = c.whoseTurn === 'Legal' ? (c.legalPersonName ? `Legal's turn · ${c.legalPersonName}` : 'New for Legal to pick up') : "Owner's turn"
  return (
    <span className="mt-1 block text-xs text-muted-foreground">
      {who}
      {c.turnSinceUtc && ` · ${waited(c.turnSinceUtc)}`}
    </span>
  )
}

/** The one date that matters most: the notice deadline, else the end date. */
function KeyDate({ c }: { c: ContractListItem }) {
  if (c.stage === 'Withdrawn') return <span className="text-muted-foreground">Withdrawn</span>
  // Past its end date and carrying on under the same terms (D-121): not overdue. (Contracts with a decision drop out of the overdue look on Renewals.)
  if (isContinuing(c)) return <span>Continuing until replaced</span>
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
  const view = (params.get('view') as View) || (person.roles.contractOwner ? 'mine' : person.roles.legal ? 'legal' : 'all')
  const q = params.get('q') ?? ''
  const [search, setSearch] = useState(q)

  // Search as you type (a short pause), kept in the address so Back returns to the same list.
  useEffect(() => {
    const t = setTimeout(() => {
      if (search !== q) setParams((p) => { const n = new URLSearchParams(p); if (search) n.set('q', search); else n.delete('q'); return n }, { replace: true })
    }, 250)
    return () => clearTimeout(t)
  }, [search, q, setParams])

  // Filters: fields an admin made filterable (Admin, Contract fields), chosen values kept in the address.
  const layout = useQuery({ queryKey: ['layout'], queryFn: () => api.get<Layout>('/api/layout') })
  const filters = filterFields(layout.data?.fields)
  const chosen = Object.fromEntries(Object.entries(chosenFilters(params)).filter(([k]) => filters.some((f) => f.key === k)))
  const filterCount = Object.keys(chosen).length
  const filterQuery = Object.entries(chosen)
    .map(([k, v]) => `&${encodeURIComponent(filterPrefix + k)}=${encodeURIComponent(v)}`)
    .join('')
  // Open when the address already has filters (before the layout has loaded, so after a reload too).
  const [showFilters, setShowFilters] = useState(() => Object.keys(chosenFilters(params)).length > 0)
  const setFilter = (key: string, value: string) =>
    setParams((p) => {
      const n = new URLSearchParams(p)
      if (value) n.set(filterPrefix + key, value)
      else n.delete(filterPrefix + key)
      return n
    }, { replace: true })
  const clearFilters = () =>
    setParams((p) => {
      const n = new URLSearchParams(p)
      for (const key of [...n.keys()]) if (key.startsWith(filterPrefix)) n.delete(key)
      return n
    }, { replace: true })

  // From a Home tile: just that tile's contracts.
  const tile = params.get('tile')
  const home = useQuery({ queryKey: ['home'], queryFn: () => api.get<HomeData>('/api/home'), enabled: tile !== null })
  const tileLabel = home.data?.tiles.find((t) => t.key === tile)?.label
  const clearTile = () =>
    setParams((p) => {
      const n = new URLSearchParams(p)
      n.delete('tile')
      return n
    })

  const contracts = useQuery({
    queryKey: ['contracts', view, q, filterQuery, tile],
    queryFn: () => api.get<ContractListItem[]>(`/api/contracts?view=${view}&q=${encodeURIComponent(q)}${filterQuery}${tile ? `&tile=${encodeURIComponent(tile)}` : ''}`),
    placeholderData: (previous) => previous,
  })
  const rows = contracts.data ?? []
  // Added fields an admin offered as list columns (Admin, Contract fields).
  const extraColumns = (layout.data?.fields ?? []).filter((f) => !f.isBuiltIn && f.showInList && !f.isHidden)

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

      {/* Wraps onto a second line when there isn't room, so nothing overlaps at any width. */}
      <div className="flex flex-col gap-3 md:flex-row md:flex-wrap md:items-center md:justify-between">
        <div className="flex flex-col gap-3 md:flex-row md:flex-wrap md:items-center">
          <div className="relative md:w-64">
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
              { value: 'legal', label: 'With Legal' },
              ...(person.roles.approver ? [{ value: 'approval' as View, label: 'To approve' }] : []),
              { value: 'drafts', label: 'Drafts' },
              { value: 'closed', label: 'Closed' },
            ]}
          />
          {filters.length > 0 && (
            <Button
              aria-expanded={showFilters}
              aria-controls="contract-filters"
              icon={<ListFilter aria-hidden className="size-5" />}
              onClick={() => setShowFilters((v) => !v)}
            >
              Filters{filterCount > 0 && <span className="tabular rounded-full bg-primary px-2 text-xs text-primary-foreground">{filterCount}</span>}
            </Button>
          )}
        </div>
        <ExcelButtons kind="contracts" canImport={person.roles.admin} invalidate={[['contracts'], ['suppliers'], ['contract']]} />
      </div>

      {tile && tileLabel && (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-muted-foreground">Showing</span>
          <span className="inline-flex items-center gap-1 rounded-full bg-primary-soft py-1 pr-1 pl-3 font-medium text-primary-soft-foreground">
            {tileLabel}
            <button type="button" onClick={clearTile} aria-label={`Stop showing only ${tileLabel}`} className="inline-flex size-7 items-center justify-center rounded-full hover:bg-card">
              <X aria-hidden className="size-4" />
            </button>
          </span>
          <span className="text-muted-foreground">from your Home</span>
        </div>
      )}

      {showFilters && filters.length > 0 && (
        <div id="contract-filters">
          <ContractFilters fields={filters} chosen={chosen} onChange={setFilter} onClear={clearFilters} />
        </div>
      )}

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
              tile
                ? 'Nothing here any more. It may have moved on since you opened your Home.'
                : filterCount > 0
                ? q
                  ? `No contracts match "${q}" with these filters.`
                  : 'No contracts match these filters.'
                : q
                ? `No contracts match "${q}".`
                : view === 'mine'
                  ? "You don't own any contracts yet. Contracts you add, or are made owner or backup of, appear here."
                  : view === 'legal'
                    ? 'Nothing is with Legal right now.'
                    : view === 'approval'
                    ? 'Nothing is waiting for approval.'
                    : view === 'drafts'
                    ? 'No drafts right now.'
                    : view === 'closed'
                      ? 'No closed contracts yet.'
                      : 'No contracts yet. Contracts you add will appear here.'
            }
            action={
              filterCount > 0 ? (
                <Button onClick={clearFilters}>Clear filters</Button>
              ) : person.roles.contractOwner && !q && view !== 'closed' ? (
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
                  {extraColumns.map((f) => (
                    <th key={f.key} scope="col" className="px-4 py-3 font-medium">
                      {f.label}
                    </th>
                  ))}
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
                      <Turn c={c} />
                    </td>
                    <td className="px-4 py-3">{c.ownerName}</td>
                    <td className="tabular px-4 py-3">
                      <KeyDate c={c} />
                    </td>
                    {extraColumns.map((f) => (
                      <td key={f.key} className="px-4 py-3">
                        {c.fields[f.key] ?? ''}
                      </td>
                    ))}
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
                <Turn c={c} />
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
