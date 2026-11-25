// Choose a file with the button, or drag it from File Explorer (or Outlook) onto the box. The button is always there,
// for the keyboard and for phones, where there's nothing to drag from.
import { Upload } from 'lucide-react'
import { useEffect, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { cn } from '@/lib/cn'
import { formatBytes } from '@/lib/format'
import { Button } from './ui'

const hasFiles = (e: { dataTransfer: DataTransfer | null }) => Array.from(e.dataTransfer?.types ?? []).includes('Files')

export function DropZone({
  extensions,
  what,
  label,
  file,
  busy,
  onFile,
  icon,
}: {
  /** The file types allowed, for example ['.xlsx'] or ['.xlsx', '.csv']. */
  extensions: string[]
  /** What to drop, in words, for example "an Excel workbook (.xlsx)". */
  what: string
  /** The button's words, for example "Choose an Excel file". */
  label: string
  file: File | null
  busy?: boolean
  onFile: (f: File) => void
  icon?: ReactNode
}) {
  const input = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // A file dropped just outside the box would make the browser leave the page to show it. Stop that while this is on screen.
  useEffect(() => {
    const stop = (e: globalThis.DragEvent) => {
      if (hasFiles(e)) e.preventDefault()
    }
    window.addEventListener('dragover', stop)
    window.addEventListener('drop', stop)
    return () => {
      window.removeEventListener('dragover', stop)
      window.removeEventListener('drop', stop)
    }
  }, [])

  const take = (f: File | undefined | null) => {
    if (!f) return
    const ok = extensions.some((x) => f.name.toLowerCase().endsWith(x.toLowerCase()))
    if (!ok) {
      setProblem(`${f.name} isn't ${what}. Choose ${what}.`)
      return
    }
    setProblem(null)
    onFile(f)
  }

  const drag = (e: DragEvent, entering: boolean) => {
    if (!hasFiles(e)) return
    e.preventDefault()
    if (entering) e.dataTransfer.dropEffect = 'copy'
    setOver(entering)
  }

  return (
    <div
      onDragEnter={(e) => drag(e, true)}
      onDragOver={(e) => drag(e, true)}
      onDragLeave={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false)
      }}
      onDrop={(e) => {
        if (!hasFiles(e)) return
        e.preventDefault()
        setOver(false)
        if (e.dataTransfer.files.length > 1) setProblem('Drop one file at a time.')
        else take(e.dataTransfer.files[0])
      }}
      className={cn(
        'flex flex-col items-center gap-2 rounded-[var(--radius-card)] border-2 border-dashed border-border p-5 text-center transition-colors',
        over && 'border-primary bg-primary-soft',
      )}
    >
      <input
        ref={input}
        type="file"
        hidden
        accept={extensions.join(',')}
        onChange={(e) => {
          take(e.target.files?.[0])
          e.target.value = '' // choosing the same file again still counts
        }}
      />
      <span className="hidden text-sm text-muted-foreground md:block">{over ? 'Drop it here' : `Drag ${what} here, or`}</span>
      <Button icon={icon ?? <Upload aria-hidden className="size-4" />} loading={busy} onClick={() => input.current?.click()}>
        {label}
      </Button>
      <span className="text-sm text-muted-foreground" aria-live="polite">
        {file ? `${file.name} (${formatBytes(file.size)})` : 'No file chosen'}
      </span>
      {problem && (
        <span role="alert" className="text-sm text-danger">
          {problem}
        </span>
      )}
    </div>
  )
}
