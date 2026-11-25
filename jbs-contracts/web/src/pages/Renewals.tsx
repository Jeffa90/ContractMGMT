// Renewals: notice deadlines (not just end dates) coming up, overdue first (product-spec sections 11 and 13.2, the renewal radar).
import { useQuery } from '@tanstack/react-query'
import { CalendarClock, Lock } from 'lucide-react'
import { Link, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/Help'
import { Alert, Badge, Card, EmptyState, Segmented, Skeleton } from '@/components/ui'
import { api } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatDate } from '@/lib/format'
import { useMe } from '@/lib/me'
import { help } from '@/help'

type Item = {
  id: number
  reference: string
  title: string
  supplierName: string | null
  ownerName: string
  isConfidential: boolean
  keyKind: 'Notice' | 'End' | 'Ends'
  keyDate: string
  days: number
  decision: 'Renew' | 'LetItEnd' | 'Renegotiate' | null
  decisionName: string | null
  mine: boolean
}
type Data = { days: number; today: string; items: Item[] }

const windows = ['30', '60', '90', '180'] as const

function keyText(i: Item) {
  const when = i.days < 0 ? `${-i.days === 1 ? '1 day' : `${-i.days} days`} ago` : i.days === 0 ? 'today' : `in ${i.days === 1 ? '1 day' : `${i.days} days`}`
  const what = i.keyKind === 'Notice' ? 'Notice deadline' : 'Ends'
  return `${what} ${formatDate(i.keyDate)}, ${when}`
}

/** One dot per contract along the next N days; overdue ones gather at the left. Decorative: the list below says the same in words. */
function Radar({ items, days }: { items: Item[]; days: number }) {
  const ticks = [0, Math.round(days / 3), Math.round((2 * days) / 3), days]
  return (
    <div aria-hidden className="relative mt-2 mb-6 h-14">
      <div className="absolute top-6 right-0 left-24 h-px bg-border" />
      <div className="absolute top-6 left-0 w-20 border-t-2 border-dashed border-danger/40" />
      <span className="absolute top-9 left-0 text-xs text-danger">Overdue</span>
      {ticks.map((t) => (
        <span key={t} className="absolute top-9 -translate-x-1/2 text-xs text-muted-foreground" style={{ left: `calc(6rem + (100% - 7rem) * ${t / days})` }}>
          {t === 0 ? 'Today' : `${t}d`}
        </span>
      ))}
      {items.map((i, n) => {
        const left = i.days < 0 ? `${0.5 + (n % 6) * 0.7}rem` : `calc(6rem + (100% - 7rem) * ${Math.min(i.days, days) / days})`
        const tone = i.days < 0 ? 'bg-danger border-danger' : i.days <= 30 ? 'bg-warning border-warning' : 'bg-primary border-primary'
        return (
          <span
            key={i.id}
            title={`${i.title}: ${keyText(i)}`}
            className={cn('absolute top-6 size-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2', i.decision ? 'bg-card' : tone, tone.split(' ')[1])}
            style={{ left, marginTop: `${((n % 3) - 1) * 6}px` }}
          />
        )
      })}
    </div>
  )
}

export function RenewalsPage() {
  const { person } = useMe()
  const [params, setParams] = useSearchParams()
  const days = (windows as readonly string[]).includes(params.get('days') ?? '') ? params.get('days')! : '90'
  const whose = params.get('whose') ?? (person.roles.contractOwner ? 'mine' : 'all')
  const set = (key: string, value: string) =>
    setParams((p) => {
      const n = new URLSearchParams(p)
      n.set(key, value)
      return n
    })
  const data = useQuery({
    queryKey: ['renewals', days, whose],
    queryFn: () => api.get<Data>(`/api/renewals?days=${days}&mine=${whose === 'mine'}`),
    placeholderData: (previous) => previous,
  })
  const items = data.data?.items ?? []
  const n = Number(days)
  const groups = [
    { title: 'Overdue', test: (i: Item) => i.days < 0 },
    { title: 'Next 30 days', test: (i: Item) => i.days >= 0 && i.days <= 30 },
    { title: '31 to 60 days', test: (i: Item) => i.days > 30 && i.days <= 60 },
    { title: '61 to 90 days', test: (i: Item) => i.days > 60 && i.days <= 90 },
    { title: '91 to 180 days', test: (i: Item) => i.days > 90 },
  ]
    .map((g) => ({ ...g, items: items.filter(g.test) }))
    .filter((g) => g.items.length > 0)

  return (
    <>
      <PageHeader title="Renewals" description="Notice deadlines and end dates coming up. Decide before the deadline: renew, let it end or renegotiate." help={help.renewals} />
      <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center">
        <Segmented label="Show the next" value={days} onChange={(v) => set('days', v)} options={windows.map((w) => ({ value: w, label: `${w} days` }))} />
        {person.roles.contractOwner && (
          <Segmented
            label="Whose"
            value={whose}
            onChange={(v) => set('whose', v)}
            options={[
              { value: 'mine', label: 'My contracts' },
              { value: 'all', label: 'All' },
            ]}
          />
        )}
      </div>

      {data.isPending ? (
        <Skeleton className="h-48 w-full" />
      ) : data.isError ? (
        <Alert tone="danger" title="We couldn't load renewals.">{(data.error as Error).message}</Alert>
      ) : items.length === 0 ? (
        <Card>
          <EmptyState icon={<CalendarClock />} title={`Nothing coming up in the next ${days} days.`} />
        </Card>
      ) : (
        <>
          <Card className="px-5 pt-4 md:px-6">
            <p className="text-sm text-muted-foreground">
              {items.length} {items.length === 1 ? 'contract' : 'contracts'}: {items.filter((i) => i.days < 0).length} overdue, {items.filter((i) => !i.decision).length} with no decision yet.
              Hollow dots have a decision.
            </p>
            <Radar items={items} days={n} />
          </Card>
          {groups.map((g) => (
            <section key={g.title} aria-labelledby={`g-${g.title}`} className="flex flex-col gap-2">
              <h2 id={`g-${g.title}`} className={cn('font-display text-lg font-semibold', g.title === 'Overdue' && 'text-danger')}>
                {g.title} <span className="tabular text-sm font-normal text-muted-foreground">({g.items.length})</span>
              </h2>
              <Card>
                <ul className="divide-y divide-border">
                  {g.items.map((i) => (
                    <li key={i.id} className="flex flex-wrap items-center justify-between gap-3 p-4">
                      <div className="min-w-0">
                        <Link to={`/contracts/${i.id}`} className="font-medium hover:underline">
                          {i.title}
                        </Link>
                        <p className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
                          {i.reference} · {i.supplierName ?? 'No supplier'} · {i.ownerName}
                          {i.isConfidential && (
                            <>
                              {' · '}
                              <Lock aria-hidden className="size-3" /> Confidential
                            </>
                          )}
                        </p>
                        <p className={cn('mt-1 text-sm font-medium', i.days < 0 ? 'text-danger' : i.days <= 30 ? 'text-warning' : undefined)}>{keyText(i)}</p>
                      </div>
                      {i.decision ? (
                        <Badge tone={i.decision === 'LetItEnd' ? 'warning' : 'success'}>{i.decisionName}</Badge>
                      ) : i.mine || person.roles.contractOwner ? (
                        <Link
                          to={`/contracts/${i.id}?decide=1`}
                          className="inline-flex min-h-11 items-center rounded-[var(--radius-control)] border border-input bg-card px-4 text-sm font-medium hover:bg-muted"
                        >
                          Record decision
                        </Link>
                      ) : (
                        <Badge>No decision yet</Badge>
                      )}
                    </li>
                  ))}
                </ul>
              </Card>
            </section>
          ))}
        </>
      )}
    </>
  )
}
