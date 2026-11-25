import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Search, UserPlus, Users } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { toast } from 'sonner'
import { Dialog } from '@/components/Dialog'
import { ExcelButtons } from '@/components/ExcelImport'
import { PageHeader } from '@/components/Help'
import { Alert, Avatar, Badge, Button, Card, Checkbox, EmptyState, Field, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError, type Person, type Roles } from '@/lib/api'
import { timeAgo } from '@/lib/format'
import { useMe } from '@/lib/me'
import { help } from '@/help'

const roleList: { key: keyof Roles; label: string; description: string }[] = [
  { key: 'contractOwner', label: 'Contract owner', description: 'Raises contracts and works them through to Effective.' },
  { key: 'legal', label: 'Legal', description: 'Reviews contracts and sends them back and forth with the owner.' },
  { key: 'approver', label: 'Approver', description: 'Approves contracts into the register.' },
  { key: 'procurement', label: 'Procurement', description: 'Looks after suppliers: their documents, details, contacts and business reviews.' },
  { key: 'admin', label: 'Admin', description: 'Manages people and settings.' },
  { key: 'auditor', label: 'Auditor', description: 'Reads everything, including history. Changes nothing.' },
]

function RoleBadges({ roles }: { roles: Roles }) {
  const items = [
    roles.groupHead ? 'Group Head' : roles.approver ? 'Approver' : null,
    roles.contractOwner ? 'Contract owner' : null,
    roles.legal ? 'Legal' : null,
    roles.procurement ? 'Procurement' : null,
    roles.admin ? 'Admin' : null,
    roles.auditor ? 'Auditor' : null,
  ].filter((x): x is string => x !== null)
  if (items.length === 0) return <span className="text-muted-foreground">Can look only</span>
  return (
    <span className="flex flex-wrap gap-1">
      {items.map((r) => (
        <Badge key={r} tone="brand">
          {r}
        </Badge>
      ))}
    </span>
  )
}

export function PeoplePage() {
  const { person: me } = useMe()
  const [params, setParams] = useSearchParams()
  const [search, setSearch] = useState(() => params.get('q') ?? '') // ?q= from search (Ctrl+K)
  const [show, setShow] = useState<'active' | 'off'>('active')
  const [editing, setEditing] = useState<Person | 'new' | null>(params.get('add') === '1' ? 'new' : null)
  const people = useQuery({ queryKey: ['people'], queryFn: () => api.get<Person[]>('/api/people') })

  useEffect(() => {
    if (params.get('add')) setParams({}, { replace: true })
  }, [params, setParams])

  const all = useMemo(() => people.data ?? [], [people.data])
  const counts = { active: all.filter((p) => p.isActive).length, off: all.filter((p) => !p.isActive).length }
  const visible = useMemo(() => {
    const q = search.trim().toLowerCase()
    return all
      .filter((p) => (show === 'active' ? p.isActive : !p.isActive))
      .filter((p) => !q || [p.name, p.account, p.email].some((v) => v.toLowerCase().includes(q)))
  }, [all, search, show])

  if (!me.roles.admin) {
    return <Alert tone="warning" title="Only admins can manage people.">Ask your admin if you need someone added.</Alert>
  }

  return (
    <>
      <PageHeader
        title="People"
        description="Who can use JBS Contracts and what they can do."
        help={help.people}
        action={
          <Button variant="primary" icon={<UserPlus aria-hidden className="size-5" />} onClick={() => setEditing('new')}>
            Add person
          </Button>
        }
      />

      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div className="flex flex-col gap-3 md:flex-row md:items-center">
          <div className="relative md:w-80">
            <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-5 -translate-y-1/2 text-muted-foreground" />
            <TextInput
              type="search"
              aria-label="Find a person"
              placeholder="Find by name, logon or email"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-10"
            />
          </div>
          <Segmented
            label="Show"
            value={show}
            onChange={setShow}
            options={[
              { value: 'active', label: 'Active', count: counts.active },
              { value: 'off', label: 'Switched off', count: counts.off },
            ]}
          />
        </div>
        <ExcelButtons kind="people" canImport invalidate={[['people'], ['me'], ['lists']]} />
      </div>

      {people.isPending ? (
        <Card className="flex flex-col gap-3 p-4">
          {[0, 1, 2].map((i) => (
            <div key={i} className="flex items-center gap-3">
              <Skeleton className="size-9 rounded-full" />
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-4 w-48" />
                <Skeleton className="h-3 w-64" />
              </div>
            </div>
          ))}
        </Card>
      ) : people.isError ? (
        <Alert tone="danger" title="We couldn't load people.">
          {(people.error as Error).message}{' '}
          <Button variant="link" onClick={() => people.refetch()}>
            Try again
          </Button>
        </Alert>
      ) : visible.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Users />}
            title={search ? `No one matches "${search}".` : show === 'off' ? 'No one is switched off.' : 'No people yet. People you add will appear here.'}
            action={
              !search && show === 'active' ? (
                <Button variant="primary" onClick={() => setEditing('new')}>
                  Add person
                </Button>
              ) : undefined
            }
          />
        </Card>
      ) : (
        <>
          {/* Desktop: table */}
          <Card className="hidden overflow-hidden md:block">
            <table className="w-full text-left">
              <thead className="border-b border-border bg-muted/50 text-xs text-muted-foreground">
                <tr>
                  <th scope="col" className="px-4 py-3 font-medium">Name</th>
                  <th scope="col" className="px-4 py-3 font-medium">Windows logon</th>
                  <th scope="col" className="px-4 py-3 font-medium">Can do</th>
                  <th scope="col" className="px-4 py-3 font-medium">Last seen</th>
                </tr>
              </thead>
              <tbody>
                {visible.map((p) => (
                  <tr key={p.id} className="cursor-pointer border-b border-border last:border-0 hover:bg-muted/50" onClick={() => setEditing(p)}>
                    <td className="px-4 py-3">
                      <button type="button" className="flex items-center gap-3 text-left" onClick={() => setEditing(p)}>
                        <Avatar name={p.name} />
                        <span>
                          <span className="block font-medium">
                            {p.name}
                            {p.id === me.id && <span className="ml-1 font-normal text-muted-foreground">(you)</span>}
                          </span>
                          <span className="block text-xs text-muted-foreground">{p.email}</span>
                        </span>
                      </button>
                    </td>
                    <td className="px-4 py-3">{p.account}</td>
                    <td className="px-4 py-3">
                      <RoleBadges roles={p.roles} />
                    </td>
                    <td className="tabular px-4 py-3 text-muted-foreground">{p.lastSignInUtc ? timeAgo(p.lastSignInUtc) : 'Not yet'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
          {/* Phone: cards */}
          <div className="flex flex-col gap-2 md:hidden">
            {visible.map((p) => (
              <Card key={p.id} className="p-0">
                <button type="button" className="flex w-full items-start gap-3 p-4 text-left" onClick={() => setEditing(p)}>
                  <Avatar name={p.name} />
                  <span className="flex min-w-0 flex-col gap-1">
                    <span className="font-medium">{p.name}</span>
                    <span className="truncate text-xs text-muted-foreground">{p.account}</span>
                    <RoleBadges roles={p.roles} />
                  </span>
                </button>
              </Card>
            ))}
          </div>
          <p className="text-xs text-muted-foreground">
            {visible.length} {visible.length === 1 ? 'person' : 'people'}
          </p>
        </>
      )}

      {editing && <PersonDialog key={editing === 'new' ? 'new' : editing.id} person={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </>
  )
}

type FormState = { account: string; name: string; email: string; roles: Roles }

function PersonDialog({ person, onClose }: { person: Person | null; onClose: () => void }) {
  const { person: me, defaultDomain } = useMe()
  const queryClient = useQueryClient()
  const isMe = person?.id === me.id
  const stripDomain = (account: string) =>
    defaultDomain && account.toUpperCase().startsWith(`${defaultDomain.toUpperCase()}\\`) ? account.slice(defaultDomain.length + 1) : account
  const [form, setForm] = useState<FormState>(() => ({
    account: person ? stripDomain(person.account) : '',
    name: person?.name ?? '',
    email: person?.email ?? '',
    roles: person?.roles ?? { contractOwner: false, legal: false, approver: false, groupHead: false, admin: false, auditor: false, procurement: false },
  }))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: (isActive: boolean) => {
      const body = { ...form, isActive, rowVersion: person?.rowVersion }
      return person ? api.put<Person>(`/api/people/${person.id}`, body) : api.post<Person>('/api/people', body)
    },
    onSuccess: (saved, isActive) => {
      queryClient.invalidateQueries({ queryKey: ['people'] })
      if (isMe) queryClient.invalidateQueries({ queryKey: ['me'] })
      toast.success(
        !person ? `${saved.name} added. They can open JBS Contracts now.` : person.isActive && !isActive ? `${saved.name}'s access is switched off.` : !person.isActive && isActive ? `${saved.name}'s access is back on.` : `${saved.name} saved.`,
      )
      onClose()
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        setErrors(error.fields)
        setProblem(Object.keys(error.fields).length ? null : error.message)
      } else setProblem('Something went wrong. Try again.')
    },
  })

  const setRole = (key: keyof Roles, value: boolean) =>
    setForm((f) => {
      const roles = { ...f.roles, [key]: value }
      if (key === 'approver' && !value) roles.groupHead = false
      if (key === 'groupHead' && value) roles.approver = true
      return { ...f, roles }
    })

  const active = person?.isActive ?? true
  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={person ? person.name : 'Add a person'}
      description={person ? `Signs in as ${person.account}` : 'They sign in with their Windows account. No password is needed.'}
      footer={
        <>
          {person && !isMe && (
            <Button
              variant={active ? 'ghost' : 'secondary'}
              className={active ? 'text-danger sm:mr-auto' : 'sm:mr-auto'}
              onClick={() => save.mutate(!active)}
              loading={save.isPending && save.variables !== active}
            >
              {active ? 'Switch off access' : 'Switch access back on'}
            </Button>
          )}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={() => save.mutate(active)} loading={save.isPending && save.variables === active}>
            {person ? 'Save' : 'Add person'}
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate(active)
        }}
      >
        {problem && <Alert tone="danger" title={problem} />}
        {!active && <Alert tone="warning" title="This person's access is switched off.">They can't open JBS Contracts until you switch it back on.</Alert>}
        <Field label="Windows logon" required error={errors.account} help={defaultDomain ? `Just their username, for example jsmith. ${defaultDomain}\\ is added for you.` : 'For example AMH\\jsmith.'}>
          {(p) => (
            <div className="flex items-stretch">
              {defaultDomain && !form.account.includes('\\') && (
                <span className="inline-flex items-center rounded-l-[var(--radius-control)] border border-r-0 border-input bg-muted px-3 text-muted-foreground">
                  {defaultDomain}\
                </span>
              )}
              <TextInput
                {...p}
                autoComplete="off"
                spellCheck={false}
                value={form.account}
                onChange={(e) => setForm({ ...form, account: e.target.value })}
                className={defaultDomain && !form.account.includes('\\') ? 'rounded-l-none' : undefined}
              />
            </div>
          )}
        </Field>
        <Field label="Name" required error={errors.name}>
          {(p) => <TextInput {...p} autoComplete="off" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />}
        </Field>
        <Field label="Work email" required error={errors.email} help="Reminders and approvals are sent here.">
          {(p) => <TextInput {...p} type="email" autoComplete="off" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />}
        </Field>
        <fieldset className="flex flex-col">
          <legend className="mb-1 text-sm font-medium">What can they do?</legend>
          <p className="mb-2 text-xs text-muted-foreground">With no ticks, they can look at contracts but not change them.</p>
          {roleList.map((r) => (
            <div key={r.key}>
              <Checkbox
                label={r.label}
                description={r.description}
                checked={form.roles[r.key]}
                onChange={(v) => setRole(r.key, v)}
                disabled={isMe && r.key === 'admin'}
              />
              {r.key === 'approver' && form.roles.approver && (
                <div className="ml-8">
                  <Checkbox
                    label="Group Head (main approver)"
                    description="There is one Group Head. The other approvers are backups. Ticking this moves it from the current Group Head."
                    checked={form.roles.groupHead}
                    onChange={(v) => setRole('groupHead', v)}
                  />
                </div>
              )}
            </div>
          ))}
          {isMe && <p className="mt-1 text-xs text-muted-foreground">You can't remove your own admin access. Ask another admin.</p>}
        </fieldset>
        <button type="submit" hidden />
      </form>
    </Dialog>
  )
}
