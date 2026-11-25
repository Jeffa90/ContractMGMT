// Admin, Reminders: when each reminder is sent and what its email says (D-13, D-70). Closed choices only: whole days, and plain
// text with a fixed list of {placeholders}. A live preview shows the email with a sample contract. Every saved change is kept.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BellRing, History } from 'lucide-react'
import { useRef, useState } from 'react'
import { toast } from 'sonner'
import { Textarea } from '@/components/form'
import { PageHeader } from '@/components/Help'
import { Alert, Button, Card, Field, Skeleton, TextInput } from '@/components/ui'
import { api, ApiError } from '@/lib/api'
import { formatDateTime } from '@/lib/format'
import { help } from '@/help'

type Kind = { kind: string; name: string; days: number[]; subject: string; body: string }
type Data = {
  kinds: Kind[]
  placeholders: string[]
  history: { id: number; kind: string; kindName: string; what: 'Days' | 'Wording'; before: string; after: string; byName: string; atUtc: string }[]
  lastRunUtc: string | null
}

const when: Record<string, string> = {
  Notice: 'Before the notice deadline, to the owner and backup, until a renewal decision is recorded.',
  Escalation: 'Before the notice deadline with no decision, to the Group Head (the owner and backup are copied).',
  End: 'Before the end date, to the owner and backup, until a renewal decision is recorded.',
  Review: 'Before the review date, to the owner and backup, until the review is done.',
}

const sample: Record<string, string> = {
  'First name': 'Jeff',
  'Contract title': 'Linehaul freight Brisbane to Sydney',
  Reference: 'JBSAUS-0142',
  Supplier: 'Southern Cross Freight',
  Owner: 'Jeff Judd',
  'Due date': 'Mon 12 Jan 2027',
  Days: '30',
}

const fill = (text: string) => text.replace(/\{([^{}]*)\}/g, (m, name: string) => sample[name] ?? m)

export function ReminderSettingsPage() {
  const data = useQuery({ queryKey: ['reminder-settings'], queryFn: () => api.get<Data>('/api/admin/reminders') })
  return (
    <>
      <PageHeader title="Reminders" description="When reminder emails go out, and what they say." help={help.reminders} />
      {data.isPending ? (
        <Skeleton className="h-96 w-full" />
      ) : data.isError ? (
        <Alert tone="danger" title={(data.error as Error).message} />
      ) : (
        <>
          <p className="text-sm text-muted-foreground">
            Reminders are checked every morning at 7:00am Brisbane time.{' '}
            {data.data.lastRunUtc ? `Last checked ${formatDateTime(data.data.lastRunUtc)}.` : "They haven't run yet."}
          </p>
          {data.data.kinds.map((k) => (
            <KindCard key={k.kind} kind={k} placeholders={data.data.placeholders} />
          ))}
          <Card className="p-5 md:p-6">
            <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
              <History aria-hidden className="size-5" /> History
            </h2>
            {data.data.history.length === 0 ? (
              <p className="mt-2 text-sm text-muted-foreground">No changes yet. These are the starting days and wording.</p>
            ) : (
              <ul className="mt-3 flex flex-col gap-3">
                {data.data.history.map((h) => (
                  <li key={h.id} className="flex flex-col border-l-2 border-border pl-3 text-sm">
                    <span>
                      {h.byName} changed the {h.kindName.toLowerCase()} {h.what === 'Days' ? `days from ${h.before} to ${h.after}` : 'wording'}
                    </span>
                    {h.what === 'Wording' && (
                      <details className="text-xs">
                        <summary className="cursor-pointer text-muted-foreground">See before and after</summary>
                        <div className="mt-1 grid gap-2 md:grid-cols-2">
                          <pre className="rounded bg-muted p-2 whitespace-pre-wrap font-sans">{h.before}</pre>
                          <pre className="rounded bg-muted p-2 whitespace-pre-wrap font-sans">{h.after}</pre>
                        </div>
                      </details>
                    )}
                    <span className="text-xs text-muted-foreground">{formatDateTime(h.atUtc)}</span>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </>
      )}
    </>
  )
}

function KindCard({ kind, placeholders }: { kind: Kind; placeholders: string[] }) {
  const queryClient = useQueryClient()
  const [days, setDays] = useState(kind.days.join(', '))
  const [subject, setSubject] = useState(kind.subject)
  const [body, setBody] = useState(kind.body)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const lastField = useRef<'subject' | 'body'>('body')
  const subjectRef = useRef<HTMLInputElement>(null)
  const bodyRef = useRef<HTMLTextAreaElement>(null)

  const done = (message: string) => () => {
    queryClient.invalidateQueries({ queryKey: ['reminder-settings'] })
    toast.success(message)
  }
  const fail = (e: unknown) => setErrors(e instanceof ApiError ? { ...e.fields, form: Object.keys(e.fields).length ? '' : e.message } : { form: "That didn't save. Try again." })
  const saveDays = useMutation({
    mutationFn: () => {
      const parsed = days.split(/[\s,]+/).filter(Boolean).map(Number)
      if (parsed.length === 0 || parsed.some((d) => !Number.isInteger(d))) throw new ApiError(400, { code: 'invalid', message: 'Use whole numbers.', fields: { days: 'Use whole numbers of days, separated by commas, for example 90, 60, 30.' } })
      return api.put('/api/admin/reminders/days', { kind: kind.kind, days: parsed })
    },
    onSuccess: done(`${kind.name} days saved.`),
    onError: fail,
  })
  const saveWording = useMutation({
    mutationFn: () => api.put('/api/admin/reminders/wording', { kind: kind.kind, subject, body }),
    onSuccess: done(`${kind.name} wording saved.`),
    onError: fail,
  })

  // Adds a placeholder where the cursor was (subject or body).
  const insert = (name: string) => {
    const token = `{${name}}`
    const target = lastField.current === 'subject' ? subjectRef.current : bodyRef.current
    const value = lastField.current === 'subject' ? subject : body
    const start = target?.selectionStart ?? value.length
    const end = target?.selectionEnd ?? value.length
    const next = value.slice(0, start) + token + value.slice(end)
    if (lastField.current === 'subject') setSubject(next)
    else setBody(next)
    requestAnimationFrame(() => {
      target?.focus()
      target?.setSelectionRange(start + token.length, start + token.length)
    })
  }
  const changed = subject !== kind.subject || body !== kind.body

  return (
    <Card className="flex flex-col gap-5 p-5 md:p-6">
      <div>
        <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
          <BellRing aria-hidden className="size-5" /> {kind.name}
        </h2>
        <p className="text-sm text-muted-foreground">{when[kind.kind]}</p>
      </div>
      {errors.form && <Alert tone="danger" title={errors.form} />}
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Days before" error={errors.days} help="Whole days, largest first. For example 90, 60, 30, 14, 7.">
          {(p) => <TextInput {...p} value={days} onChange={(e) => setDays(e.target.value)} className="max-w-72" inputMode="numeric" />}
        </Field>
        <Button loading={saveDays.isPending} disabled={days === kind.days.join(', ')} onClick={() => saveDays.mutate()}>
          Save days
        </Button>
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <div className="flex flex-col gap-3">
          <Field label="Subject" error={errors.subject}>
            {(p) => <TextInput {...p} ref={subjectRef} value={subject} onFocus={() => (lastField.current = 'subject')} onChange={(e) => setSubject(e.target.value)} maxLength={200} />}
          </Field>
          <Field label="Email" error={errors.body} help="Plain text. Leave a blank line between paragraphs. A button to open the contract is added at the end.">
            {(p) => <Textarea {...p} ref={bodyRef} value={body} onFocus={() => (lastField.current = 'body')} onChange={(e) => setBody(e.target.value)} className="min-h-48" maxLength={4000} />}
          </Field>
          <div>
            <p className="mb-1 text-sm font-medium">Add a placeholder</p>
            <div className="flex flex-wrap gap-1">
              {placeholders.map((ph) => (
                <button key={ph} type="button" onClick={() => insert(ph)} className="min-h-9 rounded-full border border-border px-3 text-xs hover:bg-muted">
                  {`{${ph}}`}
                </button>
              ))}
            </div>
          </div>
          <div className="flex gap-2">
            <Button variant="primary" loading={saveWording.isPending} disabled={!changed} onClick={() => saveWording.mutate()}>
              Save wording
            </Button>
            {changed && (
              <Button
                variant="ghost"
                onClick={() => {
                  setSubject(kind.subject)
                  setBody(kind.body)
                  setErrors({})
                }}
              >
                Undo changes
              </Button>
            )}
          </div>
        </div>
        <div className="flex flex-col gap-2">
          <p className="text-sm font-medium">Preview, with a sample contract</p>
          <div className="rounded-[var(--radius-card)] border border-border bg-background p-4 text-sm">
            <p className="font-semibold">{fill(subject)}</p>
            <div className="mt-3 flex flex-col gap-2">
              {body
                .split(/\n\s*\n/)
                .filter((para) => para.trim())
                .map((para, i) => (
                  <p key={i} className="whitespace-pre-line">
                    {fill(para.trim())}
                  </p>
                ))}
            </div>
            <span className="mt-3 inline-block rounded-lg bg-primary px-3 py-2 text-xs font-semibold text-primary-foreground">Open the contract</span>
          </div>
        </div>
      </div>
    </Card>
  )
}
