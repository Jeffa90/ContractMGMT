import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  AlarmClock,
  AlertTriangle,
  ArrowLeftRight,
  BadgeCheck,
  CalendarClock,
  CheckCircle2,
  Circle,
  FileEdit,
  FilePlus2,
  FileSignature,
  Hourglass,
  Inbox,
  ListChecks,
  Scale,
  Search,
  SlidersHorizontal,
  Undo2,
  UserPlus,
  UserX,
} from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { HelpButton } from '@/components/Help'
import { Alert, Button, Card, Checkbox, EmptyState, Skeleton } from '@/components/ui'
import { api, type Person } from '@/lib/api'
import type { ContractListItem, Lists } from '@/lib/contracts'
import { firstName, formatDate, formatLongDate, greeting } from '@/lib/format'
import { groupTitles, type HomeData, type Tile, type TileGroup } from '@/lib/home'
import { useMe } from '@/lib/me'
import { openSearch } from '@/lib/search'
import { help } from '@/help'

const tileIcons: Record<string, ReactNode> = {
  'back-from-legal': <ArrowLeftRight aria-hidden className="size-5" />,
  'sent-back': <Undo2 aria-hidden className="size-5" />,
  'signed-copy-due': <FileSignature aria-hidden className="size-5" />,
  drafts: <FileEdit aria-hidden className="size-5" />,
  'legal-your-turn': <Scale aria-hidden className="size-5" />,
  'legal-new': <Inbox aria-hidden className="size-5" />,
  'to-approve': <BadgeCheck aria-hidden className="size-5" />,
  'notice-30': <AlarmClock aria-hidden className="size-5" />,
  'ending-90': <CalendarClock aria-hidden className="size-5" />,
  'legal-waiting-owner': <Hourglass aria-hidden className="size-5" />,
  'owner-inactive': <UserX aria-hidden className="size-5" />,
}

function TileCard({ tile }: { tile: Tile }) {
  return (
    <Link
      to={`/contracts?view=all&tile=${tile.key}`}
      className="group flex flex-col gap-2 rounded-[var(--radius-card)] border border-border bg-card p-4 transition-colors hover:border-primary/50 hover:bg-muted/40"
    >
      <span className="flex items-center justify-between text-muted-foreground">
        {tileIcons[tile.key]}
        <span className="text-xs opacity-0 transition-opacity group-hover:opacity-100">Open</span>
      </span>
      <span className="tabular font-display text-3xl font-semibold">{tile.count}</span>
      <span className="font-medium">{tile.label}</span>
      <span className="text-xs text-muted-foreground">{tile.help}</span>
    </Link>
  )
}

function Urgent({ items }: { items: HomeData['urgent'] }) {
  const [showAll, setShowAll] = useState(false)
  if (items.length === 0) return null
  const shown = showAll ? items : items.slice(0, 5)
  return (
    <section aria-labelledby="urgent" className="flex flex-col gap-3">
      <h2 id="urgent" className="font-display text-lg font-semibold">
        Urgent
      </h2>
      <Card>
        <ul className="divide-y divide-border">
          {shown.map((u) => {
            const overdue = u.days < 0
            return (
              <li key={u.id} className="flex flex-wrap items-center justify-between gap-3 p-4">
                <div className="flex min-w-0 items-start gap-3">
                  {overdue ? (
                    <AlertTriangle aria-hidden className="mt-0.5 size-5 shrink-0 text-danger" />
                  ) : (
                    <AlarmClock aria-hidden className="mt-0.5 size-5 shrink-0 text-warning" />
                  )}
                  <div className="min-w-0">
                    <p className="font-medium">
                      <span className="sr-only">{overdue ? 'Overdue: ' : 'This week: '}</span>
                      {u.title} <span className="text-sm font-normal text-muted-foreground">{u.reference}{u.supplierName ? ` · ${u.supplierName}` : ''}</span>
                    </p>
                    <p className={overdue ? 'text-sm font-medium text-danger' : 'text-sm font-medium text-warning'}>
                      {overdue
                        ? `The notice deadline was ${-u.days === 1 ? 'yesterday' : `${-u.days} days ago`} (${formatDate(u.deadline)})`
                        : u.days === 0
                          ? `The notice deadline is today (${formatDate(u.deadline)})`
                          : `The notice deadline is in ${u.days === 1 ? '1 day' : `${u.days} days`} (${formatDate(u.deadline)})`}
                    </p>
                  </div>
                </div>
                <Link to={`/contracts/${u.id}`} className="inline-flex min-h-11 items-center rounded-[var(--radius-control)] border border-input bg-card px-4 text-sm font-medium hover:bg-muted">
                  Open
                </Link>
              </li>
            )
          })}
        </ul>
      </Card>
      {items.length > 5 && !showAll && (
        <Button variant="link" className="self-start" onClick={() => setShowAll(true)}>
          Show all ({items.length})
        </Button>
      )}
    </section>
  )
}

function ChooseDialog({ data, onClose }: { data: HomeData; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [hidden, setHidden] = useState(new Set(data.preferences.hiddenTiles))
  const [first, setFirst] = useState(data.preferences.firstGroup)
  const save = useMutation({
    mutationFn: () => api.put('/api/home/preferences', { hiddenTiles: [...hidden], firstGroup: first }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['home'] })
      toast.success('Saved. Your Home shows what you chose.')
      onClose()
    },
    onError: () => toast.error("That didn't save. Try again."),
  })
  const groups = (['turn', 'coming', 'admin'] as TileGroup[]).filter((g) => data.tiles.some((t) => t.group === g))
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Choose what I see"
      description="Tiles only show while something is waiting. Untick any you don't want on your Home."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>
            Save
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {groups.map((g) => (
          <fieldset key={g} className="flex flex-col">
            <legend className="mb-1 text-sm font-medium">{groupTitles[g]}</legend>
            {data.tiles
              .filter((t) => t.group === g)
              .map((t) => (
                <Checkbox
                  key={t.key}
                  label={t.label}
                  description={t.help}
                  checked={!hidden.has(t.key)}
                  onChange={(show) => {
                    const next = new Set(hidden)
                    if (show) next.delete(t.key)
                    else next.add(t.key)
                    setHidden(next)
                  }}
                />
              ))}
          </fieldset>
        ))}
        {groups.includes('turn') && groups.includes('coming') && (
          <fieldset className="flex flex-col gap-1">
            <legend className="mb-1 text-sm font-medium">Show first</legend>
            {(['turn', 'coming'] as const).map((g) => (
              <label key={g} className="flex min-h-11 cursor-pointer items-center gap-3">
                <input type="radio" name="first-group" checked={first === g} onChange={() => setFirst(g)} className="size-5 accent-[var(--primary)]" />
                {groupTitles[g]}
              </label>
            ))}
          </fieldset>
        )}
      </div>
    </Dialog>
  )
}

function Step({ done, to, icon, title, detail }: { done: boolean; to: string; icon: ReactNode; title: string; detail: string }) {
  return (
    <li className="flex items-start gap-3 py-3">
      {done ? <CheckCircle2 aria-hidden className="mt-0.5 size-5 shrink-0 text-success" /> : <Circle aria-hidden className="mt-0.5 size-5 shrink-0 text-muted-foreground" />}
      <div className="flex min-w-0 flex-1 flex-wrap items-center justify-between gap-2">
        <div>
          <p className={done ? 'text-muted-foreground line-through' : 'font-medium'}>
            {title}
            {done && <span className="sr-only"> (done)</span>}
          </p>
          {!done && <p className="text-sm text-muted-foreground">{detail}</p>}
        </div>
        {!done && (
          <Link to={to} className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] border border-input bg-card px-4 font-medium hover:bg-muted">
            {icon}
            {title}
          </Link>
        )}
      </div>
    </li>
  )
}

export function HomePage() {
  const { person } = useMe()
  const [now] = useState(() => new Date())
  const [choosing, setChoosing] = useState(false)
  const isAdmin = person.roles.admin
  const home = useQuery({ queryKey: ['home'], queryFn: () => api.get<HomeData>('/api/home') })
  const people = useQuery({ queryKey: ['people'], queryFn: () => api.get<Person[]>('/api/people'), enabled: isAdmin })
  const lists = useQuery({ queryKey: ['lists'], queryFn: () => api.get<Lists>('/api/lists'), enabled: isAdmin })
  const contracts = useQuery({ queryKey: ['contracts', 'all', ''], queryFn: () => api.get<ContractListItem[]>('/api/contracts?view=all&q='), enabled: isAdmin })

  // First-run checklist for admins, shown until each step is done.
  const steps =
    isAdmin && people.data && lists.data && contracts.data
      ? [
          { done: people.data.filter((p) => p.isActive).length > 1, to: '/admin/people?add=1', icon: <UserPlus aria-hidden className="size-5" />, title: 'Add people', detail: "Add the people who'll use JBS Contracts, and tick what each of them can do." },
          { done: lists.data.categories.length > 0, to: '/admin/lists', icon: <ListChecks aria-hidden className="size-5" />, title: 'Add categories', detail: 'Add your categories (and any agreement types, business units and sites) so contracts can be sorted.' },
          { done: contracts.data.length > 0, to: '/contracts/new', icon: <FilePlus2 aria-hidden className="size-5" />, title: 'Add a contract', detail: 'Add your first contract and its documents.' },
        ]
      : null
  const showSteps = steps !== null && steps.some((s) => !s.done)

  const data = home.data
  const hidden = new Set(data?.preferences.hiddenTiles ?? [])
  const order: TileGroup[] = data?.preferences.firstGroup === 'coming' ? ['coming', 'turn', 'admin'] : ['turn', 'coming', 'admin']
  const groups = order
    .map((g) => ({ group: g, tiles: (data?.tiles ?? []).filter((t) => t.group === g && t.count > 0 && !hidden.has(t.key)) }))
    .filter((g) => g.tiles.length > 0)
  const nothing = data !== undefined && groups.length === 0 && data.urgent.length === 0

  return (
    <>
      <header className="flex flex-col gap-4">
        <div>
          <div className="flex items-center gap-1">
            <h1 className="font-display text-xl font-semibold md:text-2xl">
              {greeting(now)}, {firstName(person.name)}
            </h1>
            <HelpButton topic={help.home} />
          </div>
          <p className="mt-1 text-muted-foreground">{formatLongDate(now)}</p>
        </div>
        <button
          type="button"
          onClick={openSearch}
          className="flex min-h-12 w-full max-w-xl items-center gap-3 rounded-[var(--radius-control)] border border-input bg-card px-4 text-left text-muted-foreground hover:border-primary/50"
        >
          <Search aria-hidden className="size-5 shrink-0" />
          <span className="flex-1">Find a contract or supplier</span>
          <kbd className="hidden rounded border border-border px-1.5 text-xs sm:inline">Ctrl K</kbd>
        </button>
      </header>

      {showSteps && (
        <Card className="p-5 md:p-6">
          <h2 className="font-display text-lg font-semibold">Get started</h2>
          <p className="mt-1 text-muted-foreground">Three steps to set up JBS Contracts.</p>
          <ol className="mt-2 divide-y divide-border">
            {steps!.map((s) => (
              <Step key={s.title} {...s} />
            ))}
          </ol>
        </Card>
      )}

      {home.isPending ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" aria-busy="true">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-36" />
          ))}
        </div>
      ) : home.isError ? (
        <Alert tone="danger" title="We couldn't load your Home.">{(home.error as Error).message}</Alert>
      ) : (
        <>
          {groups.map(({ group, tiles }) => (
            <section key={group} aria-labelledby={`group-${group}`} className="flex flex-col gap-3">
              <h2 id={`group-${group}`} className="font-display text-lg font-semibold">
                {groupTitles[group]}
              </h2>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                {tiles.map((t) => (
                  <TileCard key={t.key} tile={t} />
                ))}
              </div>
            </section>
          ))}
          <Urgent items={data!.urgent} />
          {nothing && !showSteps && (
            <Card>
              <EmptyState
                icon={<CheckCircle2 />}
                title="You're all done. Nothing needs you right now."
                action={
                  <Link to={person.roles.contractOwner ? '/contracts?view=mine' : '/contracts?view=all'} className="font-medium text-primary underline-offset-4 hover:underline">
                    {person.roles.contractOwner ? 'See my contracts' : 'See all contracts'}
                  </Link>
                }
              />
            </Card>
          )}
          {data!.tiles.length > 0 && (
            <div>
              <Button variant="ghost" icon={<SlidersHorizontal aria-hidden className="size-4" />} onClick={() => setChoosing(true)}>
                Choose what I see
              </Button>
            </div>
          )}
          {choosing && <ChooseDialog data={data!} onClose={() => setChoosing(false)} />}
        </>
      )}
    </>
  )
}
