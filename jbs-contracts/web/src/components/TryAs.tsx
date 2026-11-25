// QA only: an admin tries the app as someone else to test every role (D-81). A bright banner shows while it's on.
import { useQuery } from '@tanstack/react-query'
import { Search, UserRoundSearch, X } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { useMe } from '@/lib/me'
import { Dialog } from './Dialog'
import { Avatar, Button, TextInput } from './ui'

type QaPerson = { id: number; name: string; windowsAccount: string; roles: string; isYou: boolean }

async function switchTo(personId: number | null) {
  try {
    await api.post('/api/qa/try-as', { personId })
    // A fresh start, so every page and permission is the other person's.
    window.location.assign('/')
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : "That didn't work. Try again.")
  }
}

export function TryAsBanner() {
  const me = useMe()
  if (!me.tryingAs) return null
  const name = me.person.name.replace(/ \[tried by .*\]$/, '')
  return (
    <div role="status" className="sticky top-0 z-40 flex flex-wrap items-center justify-center gap-x-3 gap-y-1 bg-warning px-4 py-2 text-sm font-medium text-white">
      <span>
        QA: you're trying JBS Contracts as <strong>{name}</strong>. Everything you do is recorded as {me.tryingAs.realName} trying it.
      </span>
      <button type="button" onClick={() => switchTo(null)} className="inline-flex min-h-9 items-center gap-1 rounded-lg border border-white/70 px-3 hover:bg-white/15">
        <X aria-hidden className="size-4" /> Stop and go back to {me.tryingAs.realName}
      </button>
    </div>
  )
}

export function TryAsDialog({ onClose }: { onClose: () => void }) {
  const people = useQuery({ queryKey: ['qa-people'], queryFn: () => api.get<QaPerson[]>('/api/qa/people') })
  const [search, setSearch] = useState('')
  const q = search.trim().toLowerCase()
  const shown = (people.data ?? []).filter((p) => !q || p.name.toLowerCase().includes(q) || p.roles.toLowerCase().includes(q))
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()} title="Try as someone else" description="QA only. Use the app as that person to test their role; stop any time from the banner.">
      <div className="flex flex-col gap-3">
        <div className="relative">
          <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-5 -translate-y-1/2 text-muted-foreground" />
          <TextInput type="search" aria-label="Find a person" placeholder="Name or role, for example Legal" value={search} onChange={(e) => setSearch(e.target.value)} className="pl-10" autoFocus />
        </div>
        <ul className="flex max-h-[50vh] flex-col overflow-y-auto">
          {shown.map((p) => (
            <li key={p.id}>
              <button type="button" onClick={() => switchTo(p.isYou ? null : p.id)} className="flex w-full min-h-12 items-center gap-3 rounded-[var(--radius-control)] px-2 py-2 text-left hover:bg-muted">
                <Avatar name={p.name} size={32} />
                <span className="min-w-0">
                  <span className="block truncate font-medium">
                    {p.name}
                    {p.isYou && ' (you)'}
                  </span>
                  <span className="block truncate text-xs text-muted-foreground">{p.roles || 'No roles: can look at contracts'}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
        <Button variant="ghost" onClick={onClose} icon={<UserRoundSearch aria-hidden className="size-4" />}>
          Cancel
        </Button>
      </div>
    </Dialog>
  )
}
