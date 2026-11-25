// Centred dialog on desktop, bottom sheet on phones (design-system.md section 8).
import * as RadixDialog from '@radix-ui/react-dialog'
import { X } from 'lucide-react'
import { useLayoutEffect, useRef, type ReactNode } from 'react'

export function Dialog({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description?: string
  children: ReactNode
  footer?: ReactNode
}) {
  // Radix gives focus back only to a Dialog.Trigger, and ours are opened from ordinary buttons and menus. So remember what had
  // focus when the dialog opened and go back to it when it closes, or keyboard users are dropped at the top of the page.
  const returnTo = useRef<HTMLElement | null>(null)
  useLayoutEffect(() => {
    if (open && document.activeElement instanceof HTMLElement && document.activeElement !== document.body) returnTo.current = document.activeElement
  }, [open])
  return (
    <RadixDialog.Root open={open} onOpenChange={onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/40 data-[state=open]:animate-[fade-in_150ms_ease-out]" />
        <RadixDialog.Content
          onCloseAutoFocus={(e) => {
            const el = returnTo.current
            if (el?.isConnected) {
              e.preventDefault()
              el.focus()
            }
          }}
          className="fixed inset-x-0 bottom-0 z-50 flex max-h-[92dvh] flex-col rounded-t-[var(--radius-card)] border border-border bg-card shadow-xl md:inset-auto md:top-1/2 md:left-1/2 md:w-full md:max-w-lg md:-translate-x-1/2 md:-translate-y-1/2 md:rounded-[var(--radius-card)]"
        >
          <div className="flex items-start justify-between gap-4 border-b border-border px-5 py-4">
            <div>
              <RadixDialog.Title className="font-display text-lg font-semibold">{title}</RadixDialog.Title>
              {description ? (
                <RadixDialog.Description className="mt-1 text-sm text-muted-foreground">{description}</RadixDialog.Description>
              ) : (
                <RadixDialog.Description className="sr-only">{title}</RadixDialog.Description>
              )}
            </div>
            <RadixDialog.Close className="-mr-2 inline-flex size-11 items-center justify-center rounded-[var(--radius-control)] hover:bg-muted" aria-label="Close">
              <X aria-hidden className="size-5" />
            </RadixDialog.Close>
          </div>
          <div className="overflow-y-auto px-5 py-4">{children}</div>
          {footer && <div className="flex flex-col-reverse gap-2 border-t border-border px-5 py-4 sm:flex-row sm:justify-end">{footer}</div>}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  )
}
