import { forwardRef, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react'
import { Archive, Ban, CheckCircle2, FileEdit, FileSignature, Hourglass, Scale } from 'lucide-react'
import { cn } from '@/lib/cn'
import type { Stage } from '@/lib/contracts'
import { Badge, type Tone } from './ui'

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(function Select({ className, children, ...props }, ref) {
  return (
    <select
      ref={ref}
      className={cn(
        'min-h-11 w-full rounded-[var(--radius-control)] border border-input bg-card px-3 text-foreground aria-[invalid=true]:border-danger',
        className,
      )}
      {...props}
    >
      {children}
    </select>
  )
})

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(function Textarea({ className, ...props }, ref) {
  return (
    <textarea
      ref={ref}
      className={cn(
        'min-h-24 w-full rounded-[var(--radius-control)] border border-input bg-card px-3 py-2 text-foreground aria-[invalid=true]:border-danger',
        className,
      )}
      {...props}
    />
  )
})

const stageStyle: Record<Stage, { tone: Tone; icon: typeof FileEdit }> = {
  Draft: { tone: 'neutral', icon: FileEdit },
  LegalReview: { tone: 'info', icon: Scale },
  Signing: { tone: 'info', icon: FileSignature },
  Approval: { tone: 'warning', icon: Hourglass },
  Effective: { tone: 'success', icon: CheckCircle2 },
  Ended: { tone: 'neutral', icon: Archive },
  Archived: { tone: 'neutral', icon: Archive },
  Withdrawn: { tone: 'neutral', icon: Ban },
}

/** A contract's stage: always icon + word (colour never carries meaning alone). */
export function StageBadge({ stage, name }: { stage: Stage; name: string }) {
  const { tone, icon: Icon } = stageStyle[stage]
  return (
    <Badge tone={tone} icon={<Icon aria-hidden className="size-3.5" />}>
      {name}
    </Badge>
  )
}
