// The Feedback button on every screen (D-101): what kind, what happened; the screen is sent with it.
import { useMutation } from '@tanstack/react-query'
import { MessageSquareWarning } from 'lucide-react'
import { useState } from 'react'
import { useLocation } from 'react-router'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { feedbackKinds } from '@/lib/feedback'
import { Dialog } from './Dialog'
import { Select, Textarea } from './form'
import { Button, Field } from './ui'

/**
 * Floating, bottom right, on wider screens. On a phone it would sit over forms' Save bars, so there it's on the Feedback page instead
 * (inline), reached from the menu.
 */
export function FeedbackButton({ inline }: { inline?: boolean }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className={
          inline
            ? 'inline-flex min-h-11 items-center gap-2 self-start rounded-[var(--radius-control)] border border-input bg-card px-4 font-medium hover:bg-muted'
            : 'fixed right-4 bottom-4 z-30 hidden min-h-11 items-center gap-2 rounded-full border border-border bg-card px-4 text-sm font-medium shadow-lg hover:bg-muted md:inline-flex print:hidden'
        }
      >
        <MessageSquareWarning aria-hidden className="size-4 text-primary" /> Feedback
      </button>
      {open && <FeedbackDialog onClose={() => setOpen(false)} />}
    </>
  )
}

function FeedbackDialog({ onClose }: { onClose: () => void }) {
  const location = useLocation()
  const [kind, setKind] = useState('Bug')
  const [comment, setComment] = useState('')
  const [error, setError] = useState<string | undefined>()
  // The screen it came from: the page's heading (Home's is a greeting, so it's called Home) and address.
  const heading = location.pathname === '/' ? 'Home' : document.querySelector('main h1')?.textContent?.trim()
  const screen = `${heading ? `${heading} ` : ''}(${location.pathname}${location.search})`
  const send = useMutation({
    mutationFn: () => api.post('/api/feedback', { kind, comment, screen }),
    onSuccess: () => {
      toast.success('Thanks, your feedback has been recorded. You can see it any time on the Feedback page.')
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? (e.fields.comment ?? e.message) : "That didn't send. Try again."),
  })
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Feedback"
      description="Tell us what's working and what isn't. The screen you're on is sent with it."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={send.isPending} onClick={() => send.mutate()}>
            Send feedback
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label="What kind of feedback?">
          {(p) => (
            <Select {...p} value={kind} onChange={(e) => setKind(e.target.value)}>
              {feedbackKinds.map((k) => (
                <option key={k.value} value={k.value}>
                  {k.label}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label="Tell us what happened" required error={error}>
          {(p) => (
            <Textarea
              {...p}
              autoFocus
              rows={5}
              maxLength={4000}
              value={comment}
              onChange={(e) => setComment(e.target.value)}
              placeholder="For example: I clicked Save and nothing happened. Or: it would help to sort by supplier."
            />
          )}
        </Field>
        <p className="text-xs text-muted-foreground">From: {screen}</p>
      </div>
    </Dialog>
  )
}
