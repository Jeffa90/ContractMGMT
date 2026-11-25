// The Feedback page (D-101): your own feedback, or everyone's for admins, with replies, status and archive.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, ArchiveRestore, MessageSquareWarning, Search } from 'lucide-react'
import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { toast } from 'sonner'
import { Select, Textarea } from '@/components/form'
import { FeedbackButton } from '@/components/FeedbackButton'
import { PageHeader } from '@/components/Help'
import { Alert, Badge, Button, Card, Checkbox, EmptyState, Field, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import { feedbackKinds, feedbackStatuses, kindTone, statusTone, type FeedbackDetail, type FeedbackItem, type FeedbackList } from '@/lib/feedback'
import { formatDateTime } from '@/lib/format'
import { help } from '@/help'

export function FeedbackPage() {
  const [params, setParams] = useSearchParams()
  const [all, setAll] = useState(false)
  const [status, setStatus] = useState('')
  const [kind, setKind] = useState('')
  const [q, setQ] = useState('')
  const [archived, setArchived] = useState(false)
  const list = useQuery({ queryKey: ['feedback', all], queryFn: () => api.get<FeedbackList>(`/api/feedback${all ? '?all=true' : ''}`) })
  const openId = Number(params.get('open')) || null

  const items = (list.data?.items ?? []).filter((f) => {
    if (!archived && f.isArchived) return false
    if (status && f.status !== status) return false
    if (kind && f.kind !== kind) return false
    const text = q.trim().toLowerCase()
    return !text || [f.comment, f.screen ?? '', f.createdByName].some((t) => t.toLowerCase().includes(text))
  })

  return (
    <>
      <PageHeader
        title="Feedback"
        description="Feedback and actions. Use the Feedback button on any screen to tell us what's working and what isn't."
        help={help.feedback}
        action={<FeedbackButton inline />}
      />
      {list.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : list.isError ? (
        <Alert tone="danger" title={(list.error as Error).message} />
      ) : (
        <>
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-5">
            {[
              ['Open', list.data.counts.open],
              ['In progress', list.data.counts.inProgress],
              ['Closed', list.data.counts.closed],
              ['Archived', list.data.counts.archived],
              ['Total', list.data.counts.total],
            ].map(([label, n]) => (
              <Card key={label} className="p-4">
                <p className="tabular font-display text-2xl font-semibold">{n}</p>
                <p className="text-sm text-muted-foreground">{label}</p>
              </Card>
            ))}
          </div>
          <div className="flex flex-col gap-3 md:flex-row md:flex-wrap md:items-end">
            {list.data.canSeeAll && (
              <Segmented
                label="Whose feedback"
                value={all ? 'all' : 'mine'}
                onChange={(v) => setAll(v === 'all')}
                options={[
                  { value: 'mine', label: 'My feedback' },
                  { value: 'all', label: 'All feedback' },
                ]}
              />
            )}
            <Field label="Status">
              {(p) => (
                <Select {...p} value={status} onChange={(e) => setStatus(e.target.value)} className="md:w-44">
                  <option value="">Any</option>
                  {feedbackStatuses.map((s) => (
                    <option key={s.value} value={s.value}>
                      {s.label}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field label="Kind">
              {(p) => (
                <Select {...p} value={kind} onChange={(e) => setKind(e.target.value)} className="md:w-48">
                  <option value="">Any</option>
                  {feedbackKinds.map((k) => (
                    <option key={k.value} value={k.value}>
                      {k.label}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field label="Search">
              {(p) => <TextInput {...p} value={q} onChange={(e) => setQ(e.target.value)} placeholder="Words, screen or person" className="md:w-64" />}
            </Field>
            <Checkbox label="Include archived" checked={archived} onChange={setArchived} />
          </div>
          {items.length === 0 ? (
            <Card>
              <EmptyState
                icon={list.data.counts.total === 0 ? <MessageSquareWarning /> : <Search />}
                title={list.data.counts.total === 0 ? 'No feedback yet. Use the Feedback button on any screen.' : 'Nothing matches. Try other filters.'}
              />
            </Card>
          ) : (
            <ul className="flex flex-col gap-3">
              {items.map((f) => (
                <FeedbackCard
                  key={f.id}
                  item={f}
                  open={openId === f.id}
                  onToggle={(o) =>
                    setParams(
                      (p) => {
                        const n = new URLSearchParams(p)
                        if (o) n.set('open', String(f.id))
                        else n.delete('open')
                        return n
                      },
                      { replace: true },
                    )
                  }
                />
              ))}
            </ul>
          )}
        </>
      )}
    </>
  )
}

function FeedbackCard({ item, open, onToggle }: { item: FeedbackItem; open: boolean; onToggle: (open: boolean) => void }) {
  return (
    <li>
      <Card className="p-0">
        <details open={open} onToggle={(e) => onToggle((e.currentTarget as HTMLDetailsElement).open)}>
          <summary className="flex cursor-pointer list-none flex-col gap-2 p-4 md:p-5">
            <span className="flex flex-wrap items-center gap-2">
              <Badge tone={kindTone(item.kind)}>{item.kindName}</Badge>
              <Badge tone={statusTone(item.status)}>{item.statusName}</Badge>
              {item.isArchived && <Badge icon={<Archive aria-hidden className="size-3.5" />}>Archived</Badge>}
              {item.replies > 0 && <span className="text-xs text-muted-foreground">{item.replies === 1 ? '1 reply' : `${item.replies} replies`}</span>}
            </span>
            <span className="whitespace-pre-wrap">{item.comment}</span>
            <span className="text-xs text-muted-foreground">
              {[item.screen, item.createdByName, formatDateTime(item.createdUtc)].filter(Boolean).join(' · ')}
            </span>
          </summary>
          {open && <FeedbackThread id={item.id} />}
        </details>
      </Card>
    </li>
  )
}

function FeedbackThread({ id }: { id: number }) {
  const queryClient = useQueryClient()
  const detail = useQuery({ queryKey: ['feedback-item', id], queryFn: () => api.get<FeedbackDetail>(`/api/feedback/${id}`) })
  const [text, setText] = useState('')
  const [error, setError] = useState<string | undefined>()
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['feedback-item', id] })
    void queryClient.invalidateQueries({ queryKey: ['feedback'] })
  }
  const reply = useMutation({
    mutationFn: () => api.post(`/api/feedback/${id}/replies`, { text }),
    onSuccess: () => {
      setText('')
      setError(undefined)
      refresh()
    },
    onError: (e) => setError(e instanceof ApiError ? (e.fields.text ?? e.message) : "That didn't send. Try again."),
  })
  const status = useMutation({
    mutationFn: (value: string) => api.put(`/api/feedback/${id}/status`, { status: value }),
    onSuccess: () => {
      refresh()
      toast.success('Status saved.')
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : "That didn't save. Try again."),
  })
  const archive = useMutation({
    mutationFn: (archived: boolean) => api.put(`/api/feedback/${id}/archive`, { archived }),
    onSuccess: (_, archived) => {
      refresh()
      toast.success(archived ? 'Archived.' : 'Back in the list.')
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : "That didn't save. Try again."),
  })
  if (detail.isPending) return <Skeleton className="mx-4 mb-4 h-24" />
  if (detail.isError) return <div className="px-4 pb-4"><Alert tone="danger" title={(detail.error as Error).message} /></div>
  const d = detail.data
  return (
    <div className="flex flex-col gap-4 border-t border-border px-4 py-4 md:px-5">
      {d.replies.length > 0 && (
        <ol className="flex flex-col gap-3" aria-label="Replies">
          {d.replies.map((r) => (
            <li key={r.id} className="border-l-2 border-border pl-3">
              <p className="whitespace-pre-wrap">{r.text}</p>
              <p className="text-xs text-muted-foreground">
                {r.byName} · {formatDateTime(r.createdUtc)}
              </p>
            </li>
          ))}
        </ol>
      )}
      <Field label="Reply" error={error}>
        {(p) => <Textarea {...p} rows={3} maxLength={4000} value={text} onChange={(e) => setText(e.target.value)} />}
      </Field>
      <div className="flex flex-wrap items-end gap-3">
        <Button variant="primary" loading={reply.isPending} onClick={() => reply.mutate()}>
          Send reply
        </Button>
        <Field label="Status">
          {(p) => (
            <Select {...p} value={status.isPending ? status.variables : d.item.status} onChange={(e) => status.mutate(e.target.value)} className="w-44">
              {feedbackStatuses.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </Select>
          )}
        </Field>
        {d.canArchive && (
          <Button
            variant="ghost"
            icon={d.item.isArchived ? <ArchiveRestore aria-hidden className="size-4" /> : <Archive aria-hidden className="size-4" />}
            loading={archive.isPending}
            onClick={() => archive.mutate(!d.item.isArchived)}
          >
            {d.item.isArchived ? 'Unarchive' : 'Archive'}
          </Button>
        )}
      </div>
    </div>
  )
}
