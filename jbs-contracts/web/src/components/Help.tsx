// The "?" beside every page title: plain how-to steps for that screen (design-system.md section 12).
import { HelpCircle } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Dialog } from './Dialog'
import { Button } from './ui'

export type HelpTopic = { title: string; body: ReactNode }

export function HelpButton({ topic }: { topic: HelpTopic }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={`Help: ${topic.title}`}
        className="inline-flex size-11 items-center justify-center rounded-full text-muted-foreground hover:bg-muted hover:text-foreground"
      >
        <HelpCircle aria-hidden className="size-5" />
      </button>
      <Dialog
        open={open}
        onOpenChange={setOpen}
        title={topic.title}
        footer={
          <Button variant="primary" onClick={() => setOpen(false)}>
            Got it
          </Button>
        }
      >
        <div className="flex max-w-prose flex-col gap-3 text-sm [&_li]:ml-5 [&_ol]:list-decimal [&_ul]:list-disc">{topic.body}</div>
      </Dialog>
    </>
  )
}

export function PageHeader({
  title,
  description,
  help,
  action,
  back,
}: {
  title: string
  description?: string
  help?: HelpTopic
  action?: ReactNode
  back?: ReactNode
}) {
  return (
    <header className="flex flex-col gap-3">
      {back}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-1">
            <h1 className="font-display text-xl font-semibold md:text-2xl">{title}</h1>
            {help && <HelpButton topic={help} />}
          </div>
          {description && <p className="mt-1 text-muted-foreground">{description}</p>}
        </div>
        {action}
      </div>
    </header>
  )
}
