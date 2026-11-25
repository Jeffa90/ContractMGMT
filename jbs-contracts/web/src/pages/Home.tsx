import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, Circle, FilePlus2, ListChecks, UserPlus } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { HelpButton } from '@/components/Help'
import { Card, EmptyState } from '@/components/ui'
import { api, type Person } from '@/lib/api'
import type { ContractListItem, Lists } from '@/lib/contracts'
import { firstName, formatLongDate, greeting } from '@/lib/format'
import { useMe } from '@/lib/me'
import { help } from '@/help'

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
  const isAdmin = person.roles.admin
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

  return (
    <>
      <header>
        <div className="flex items-center gap-1">
          <h1 className="font-display text-xl font-semibold md:text-2xl">
            {greeting(now)}, {firstName(person.name)}
          </h1>
          <HelpButton topic={help.home} />
        </div>
        <p className="mt-1 text-muted-foreground">{formatLongDate(now)}</p>
      </header>

      {showSteps ? (
        <Card className="p-5 md:p-6">
          <h2 className="font-display text-lg font-semibold">Get started</h2>
          <p className="mt-1 text-muted-foreground">Three steps to set up JBS Contracts.</p>
          <ol className="mt-2 divide-y divide-border">
            {steps!.map((s) => (
              <Step key={s.title} {...s} />
            ))}
          </ol>
        </Card>
      ) : (
        <Card>
          <EmptyState
            icon={<CheckCircle2 />}
            title="You're all done. Nothing needs you right now."
            action={
              person.roles.contractOwner ? (
                <Link to="/contracts?view=mine" className="font-medium text-primary underline-offset-4 hover:underline">
                  See my contracts
                </Link>
              ) : (
                <Link to="/contracts?view=all" className="font-medium text-primary underline-offset-4 hover:underline">
                  See all contracts
                </Link>
              )
            }
          />
        </Card>
      )}
    </>
  )
}
