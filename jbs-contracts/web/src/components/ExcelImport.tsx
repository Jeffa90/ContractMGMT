// Export to Excel / Import from Excel (decision D-44). An import is always previewed first: nothing changes until
// the person confirms, and the preview says, row by row, what will be added, changed or refused and why.
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, CircleDashed, FileDown, FileSpreadsheet, FileUp, PencilLine, PlusCircle, XCircle } from 'lucide-react'
import { useRef, useState } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/lib/api'
import { Dialog } from './Dialog'
import { Alert, Badge, Button, Segmented } from './ui'

export type ExcelKind = 'people' | 'lists' | 'suppliers' | 'contracts'

type Planned = { row: number; label: string; action: 'add' | 'update' | 'same' | 'refused'; notes: string[] }
type Result = {
  applied: boolean
  counts: { added: number; updated: number; unchanged: number; refused: number }
  ignoredColumns: string[]
  rows: Planned[]
}

const what: Record<ExcelKind, { plural: string; tip: string }> = {
  people: { plural: 'people', tip: 'Rows are matched by Windows logon. Type just the username; the domain is added for you.' },
  lists: { plural: 'list items', tip: 'One row per item: the list (Category, Agreement type, Business unit, Site or Currency), its name and, for subcategories, the parent category.' },
  suppliers: { plural: 'suppliers', tip: 'Rows are matched by supplier name.' },
  contracts: { plural: 'contracts', tip: 'Leave Reference blank to add a new draft. Fill it in to update that draft. Suppliers, categories, lists and owners are matched by name.' },
}

async function send(kind: ExcelKind, step: 'preview' | 'import', file: File): Promise<Result> {
  const form = new FormData()
  form.append('file', file)
  let response: Response
  try {
    response = await fetch(`/api/excel/${kind}/${step}`, { method: 'POST', credentials: 'same-origin', headers: { 'X-JBS-Contracts': '1', Accept: 'application/json' }, body: form })
  } catch {
    throw new ApiError(0, { code: 'offline', message: "We couldn't reach JBS Contracts. Check your connection and try again." })
  }
  const body = await response.json().catch(() => undefined)
  if (!response.ok) throw new ApiError(response.status, body?.message ? body : { code: 'error', message: 'That didn\'t work. Try again.' })
  return body as Result
}

const actionStyle = {
  add: { tone: 'success' as const, icon: PlusCircle, word: 'New' },
  update: { tone: 'info' as const, icon: PencilLine, word: 'Change' },
  same: { tone: 'neutral' as const, icon: CircleDashed, word: 'No change' },
  refused: { tone: 'danger' as const, icon: XCircle, word: 'Not imported' },
}

export function ExcelButtons({ kind, canImport, invalidate }: { kind: ExcelKind; canImport: boolean; invalidate: string[][] }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="flex flex-wrap gap-1">
      <a href={`/api/excel/${kind}`} className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] px-4 font-medium hover:bg-muted">
        <FileDown aria-hidden className="size-5" /> Export to Excel
      </a>
      {canImport && (
        <Button variant="ghost" icon={<FileUp aria-hidden className="size-5" />} onClick={() => setOpen(true)}>
          Import from Excel
        </Button>
      )}
      {open && <ImportDialog kind={kind} invalidate={invalidate} onClose={() => setOpen(false)} />}
    </div>
  )
}

function ImportDialog({ kind, invalidate, onClose }: { kind: ExcelKind; invalidate: string[][]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<Result | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [show, setShow] = useState<'todo' | 'refused' | 'all'>('todo')
  const label = what[kind]

  const check = useMutation({
    mutationFn: (f: File) => send(kind, 'preview', f),
    onSuccess: (r) => {
      setPreview(r)
      setError(null)
      setShow(r.counts.refused > 0 && r.counts.added + r.counts.updated === 0 ? 'refused' : 'todo')
    },
    onError: (e) => {
      setPreview(null)
      setError(e instanceof ApiError ? e.message : 'That didn\'t work. Try again.')
    },
  })
  const apply = useMutation({
    mutationFn: () => send(kind, 'import', file!),
    onSuccess: (r) => {
      invalidate.forEach((key) => queryClient.invalidateQueries({ queryKey: key }))
      const parts = [r.counts.added && `${r.counts.added} added`, r.counts.updated && `${r.counts.updated} changed`].filter(Boolean).join(', ')
      toast.success(`Imported ${label.plural}: ${parts || 'nothing to change'}.${r.counts.refused ? ` ${r.counts.refused} row${r.counts.refused === 1 ? ' was' : 's were'} not imported.` : ''}`)
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'The import failed and nothing was changed. Try again.'),
  })

  const choose = (f: File | undefined) => {
    if (!f) return
    setFile(f)
    check.mutate(f)
  }
  const toDo = preview ? preview.counts.added + preview.counts.updated : 0
  const rows = preview?.rows.filter((r) => (show === 'all' ? true : show === 'refused' ? r.action === 'refused' : r.action === 'add' || r.action === 'update')) ?? []

  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={`Import ${label.plural} from Excel`}
      description="Nothing changes until you confirm. New rows are added, existing ones updated. Nothing is deleted, and blank cells leave what's there."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!preview || toDo === 0} loading={apply.isPending} onClick={() => apply.mutate()}>
            {toDo === 0 ? 'Nothing to import' : `Import ${toDo} ${toDo === 1 ? 'row' : 'rows'}`}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <p className="text-sm text-muted-foreground">
          {label.tip} Tip:{' '}
          <a href={`/api/excel/${kind}`} className="font-medium text-primary hover:underline">
            export first
          </a>{' '}
          to get a sheet with the right headings, then change or add rows.
        </p>
        <input ref={input} type="file" hidden accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" onChange={(e) => choose(e.target.files?.[0])} />
        <Button variant="secondary" icon={<FileSpreadsheet aria-hidden className="size-5" />} onClick={() => input.current?.click()} loading={check.isPending}>
          {file ? `Choose another file (${file.name})` : 'Choose an Excel file'}
        </Button>
        {error && <Alert tone="danger" title={error} />}
        {preview && (
          <>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              {(['added', 'updated', 'unchanged', 'refused'] as const).map((k) => (
                <div key={k} className="rounded-[var(--radius-control)] border border-border p-3">
                  <p className="tabular font-display text-xl font-semibold">{preview.counts[k]}</p>
                  <p className="text-xs text-muted-foreground">{{ added: 'New', updated: 'Changes', unchanged: 'No change', refused: 'Not imported' }[k]}</p>
                </div>
              ))}
            </div>
            {preview.ignoredColumns.length > 0 && (
              <p className="text-xs text-muted-foreground">These columns aren't used and will be ignored: {preview.ignoredColumns.join(', ')}.</p>
            )}
            {preview.counts.refused > 0 && (
              <Alert tone="warning" title={`${preview.counts.refused} row${preview.counts.refused === 1 ? '' : 's'} won't be imported.`}>
                The reasons are below. Fix them in the sheet and choose it again, or import the rest now.
              </Alert>
            )}
            <Segmented
              label="Show rows"
              value={show}
              onChange={setShow}
              options={[
                { value: 'todo', label: 'To import', count: toDo },
                { value: 'refused', label: 'Not imported', count: preview.counts.refused },
                { value: 'all', label: 'All', count: preview.rows.length },
              ]}
            />
            {rows.length === 0 ? (
              <p className="flex items-center gap-2 text-sm text-muted-foreground">
                <CheckCircle2 aria-hidden className="size-4" /> Nothing here.
              </p>
            ) : (
              <ul className="flex max-h-80 flex-col divide-y divide-border overflow-y-auto rounded-[var(--radius-control)] border border-border">
                {rows.map((r) => {
                  const style = actionStyle[r.action]
                  const Icon = style.icon
                  return (
                    <li key={r.row} className="flex flex-col gap-1 p-3">
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="tabular text-xs text-muted-foreground">Row {r.row}</span>
                        <Badge tone={style.tone} icon={<Icon aria-hidden className="size-3.5" />}>
                          {style.word}
                        </Badge>
                        <span className="font-medium">{r.label}</span>
                      </span>
                      {r.notes.map((n, i) => (
                        <span key={i} className={r.action === 'refused' ? 'text-sm text-danger' : 'text-sm text-muted-foreground'}>
                          {n}
                        </span>
                      ))}
                    </li>
                  )
                })}
              </ul>
            )}
          </>
        )}
      </div>
    </Dialog>
  )
}
