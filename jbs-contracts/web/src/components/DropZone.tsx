// Choose files with the button, or drag them from File Explorer (or Outlook) onto the box (D-96). The button is always there,
// for the keyboard and for phones, where there's nothing to drag from.
import { Upload } from 'lucide-react'
import { useEffect, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { toast } from 'sonner'
import { cn } from '@/lib/cn'
import { documentExtensions, documentWhat } from '@/lib/files'
import { formatBytes } from '@/lib/format'
import { Button } from './ui'


const hasFiles = (e: { dataTransfer: DataTransfer | null }) => Array.from(e.dataTransfer?.types ?? []).includes('Files')
const allowed = (f: File, extensions: string[]) => extensions.some((x) => f.name.toLowerCase().endsWith(x.toLowerCase()))

/** Dragging files over an area: highlights it while files are over it and hands over the allowed ones when dropped. */
function useFileDrop(extensions: string[], what: string, onFiles: (files: File[]) => void, onRefused: (message: string) => void, enabled = true) {
  const [over, setOver] = useState(false)

  // A file dropped just outside the area would make the browser leave the page to show it. Stop that while this is on screen.
  useEffect(() => {
    if (!enabled) return
    const stop = (e: globalThis.DragEvent) => {
      if (hasFiles(e)) e.preventDefault()
    }
    window.addEventListener('dragover', stop)
    window.addEventListener('drop', stop)
    return () => {
      window.removeEventListener('dragover', stop)
      window.removeEventListener('drop', stop)
    }
  }, [enabled])

  const take = (files: File[]) => {
    const ok = files.filter((f) => allowed(f, extensions))
    const refused = files.filter((f) => !allowed(f, extensions))
    if (refused.length > 0) onRefused(`${refused.map((f) => f.name).join(', ')} ${refused.length === 1 ? "isn't" : "aren't"} ${what}. Choose ${what}.`)
    if (ok.length > 0) onFiles(ok)
  }

  const drag = (e: DragEvent, entering: boolean) => {
    if (!enabled || !hasFiles(e)) return
    e.preventDefault()
    if (entering) e.dataTransfer.dropEffect = 'copy'
    setOver(entering)
  }
  const handlers = {
    onDragEnter: (e: DragEvent) => drag(e, true),
    onDragOver: (e: DragEvent) => drag(e, true),
    onDragLeave: (e: DragEvent<HTMLElement>) => {
      if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false)
    },
    onDrop: (e: DragEvent) => {
      if (!enabled || !hasFiles(e)) return
      e.preventDefault()
      setOver(false)
      take(Array.from(e.dataTransfer.files))
    },
  }
  return { over, handlers, take }
}

/** A dashed box with a Choose button that also takes a dropped file (or files, with multiple). */
export function DropZone({
  extensions,
  what,
  label,
  file,
  files,
  busy,
  multiple,
  onFile,
  onFiles,
  icon,
}: {
  /** The file types allowed, for example ['.xlsx'] or ['.xlsx', '.csv']. */
  extensions: string[]
  /** What to drop, in words, for example "an Excel workbook (.xlsx)". */
  what: string
  /** The button's words, for example "Choose an Excel file". */
  label: string
  file?: File | null
  /** With multiple: the files chosen so far (shown by the caller). */
  files?: File[]
  busy?: boolean
  multiple?: boolean
  onFile?: (f: File) => void
  onFiles?: (f: File[]) => void
  icon?: ReactNode
}) {
  const input = useRef<HTMLInputElement>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const give = (list: File[]) => {
    if (!multiple && list.length > 1) {
      setProblem('Drop one file at a time.')
      return
    }
    setProblem(null)
    if (multiple) onFiles?.(list)
    else onFile?.(list[0])
  }
  const { over, handlers, take } = useFileDrop(extensions, what, give, setProblem)

  return (
    <div
      {...handlers}
      className={cn(
        'flex flex-col items-center gap-2 rounded-[var(--radius-card)] border-2 border-dashed border-border p-5 text-center transition-colors',
        over && 'border-primary bg-primary-soft',
      )}
    >
      <input
        ref={input}
        type="file"
        hidden
        multiple={multiple}
        accept={extensions.join(',')}
        onChange={(e) => {
          take(Array.from(e.target.files ?? []))
          e.target.value = '' // choosing the same file again still counts
        }}
      />
      <span className="hidden text-sm text-muted-foreground md:block">{over ? 'Drop it here' : `Drag ${what} here, or`}</span>
      <Button icon={icon ?? <Upload aria-hidden className="size-4" />} loading={busy} onClick={() => input.current?.click()}>
        {label}
      </Button>
      {!multiple && (
        <span className="text-sm text-muted-foreground" aria-live="polite">
          {file ? `${file.name} (${formatBytes(file.size)})` : 'No file chosen'}
        </span>
      )}
      {multiple && files && files.length === 0 && <span className="text-sm text-muted-foreground">No files chosen</span>}
      {problem && (
        <span role="alert" className="text-sm text-danger">
          {problem}
        </span>
      )}
    </div>
  )
}

/**
 * Makes an existing area (for example a Documents card) take dropped files, without changing how it looks until files are dragged
 * over it. The area keeps its own buttons for choosing files.
 */
export function DropTarget({
  enabled,
  extensions = documentExtensions,
  what = documentWhat,
  message,
  onFiles,
  children,
  className,
}: {
  enabled: boolean
  extensions?: string[]
  what?: string
  /** Shown while files are over it, for example "Drop to add as Draft". */
  message: string
  onFiles: (files: File[]) => void
  children: ReactNode
  className?: string
}) {
  const { over, handlers } = useFileDrop(extensions, what, onFiles, (m) => toast.error(m), enabled)
  return (
    <div {...(enabled ? handlers : {})} className={cn('relative', className)}>
      {children}
      {over && (
        <div className="pointer-events-none absolute inset-0 z-10 flex items-center justify-center rounded-[var(--radius-card)] border-2 border-dashed border-primary bg-primary-soft/90">
          <span className="flex items-center gap-2 font-medium text-primary-soft-foreground">
            <Upload aria-hidden className="size-5" /> {message}
          </span>
        </div>
      )}
    </div>
  )
}
