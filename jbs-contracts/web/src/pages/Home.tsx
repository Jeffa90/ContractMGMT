import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, UserPlus } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { HelpButton } from '@/components/Help'
import { Card, EmptyState } from '@/components/ui'
import { api, type Person } from '@/lib/api'
import { firstName, formatLongDate, greeting } from '@/lib/format'
import { useMe } from '@/lib/me'
import { help } from '@/help'

export function HomePage() {
  const { person } = useMe()
  const [now] = useState(() => new Date())
  const people = useQuery({
    queryKey: ['people'],
    queryFn: () => api.get<Person[]>('/api/people'),
    enabled: person.roles.admin,
  })
  // First-run checklist for admins: shown until someone other than the admins has been added.
  const needsPeople = person.roles.admin && people.data !== undefined && people.data.filter((p) => p.isActive).length <= 1

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

      {needsPeople ? (
        <Card className="p-5 md:p-6">
          <h2 className="font-display text-lg font-semibold">Get started</h2>
          <p className="mt-1 text-muted-foreground">Add the people who'll use JBS Contracts, and tick what each of them can do.</p>
          <Link
            to="/admin/people?add=1"
            className="mt-4 inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] bg-primary px-4 font-medium text-primary-foreground hover:bg-primary-hover"
          >
            <UserPlus aria-hidden className="size-5" />
            Add people
          </Link>
        </Card>
      ) : (
        <Card>
          <EmptyState icon={<CheckCircle2 />} title="You're all done. Nothing needs you right now." />
        </Card>
      )}
    </>
  )
}
