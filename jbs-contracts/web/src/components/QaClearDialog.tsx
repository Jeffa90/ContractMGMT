// QA only (D-111): clear every contract, supplier and contract list before a rehearsal import, so numbers and names don't cross over.
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { Dialog } from './Dialog'
import { Alert, Button, Field, TextInput } from './ui'

export function QaClearDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | undefined>()
  const clear = useMutation({
    mutationFn: () => api.post<{ contracts: number; suppliers: number; files: number }>('/api/admin/qa-clear', { confirm }),
    onSuccess: (r) => {
      void queryClient.invalidateQueries()
      toast.success(`QA cleared: ${r.contracts} contracts, ${r.suppliers} suppliers and ${r.files} files removed. Numbers start again at JBSAUS0000001.`)
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? (e.fields.confirm ?? e.message) : "That didn't work. Try again."),
  })
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Clear contracts, suppliers and lists?"
      description="Only on QA, for a fresh start before importing. It can't be undone."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="danger" loading={clear.isPending} disabled={confirm.trim() !== 'CLEAR'} onClick={() => clear.mutate()}>
            Clear QA
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3 text-sm">
        <Alert tone="warning" title="This removes, for everyone on QA:">
          Every contract (with its documents, Variations, Legal conversation, approvals and reminders), every supplier (with its documents,
          contacts and business reviews), and the categories, agreement types, sites, business units, entities and functions. Contract numbers
          start again at JBSAUS0000001.
        </Alert>
        <p>
          <strong>Kept:</strong> people and their ticks, settings, email wording, reminder settings, contract fields, currencies, supplier tiers and
          risk levels, document kinds, feedback, and the audit trail (which records this).
        </p>
        <Field label="Type CLEAR to confirm" error={error}>
          {(p) => <TextInput {...p} autoComplete="off" value={confirm} onChange={(e) => setConfirm(e.target.value)} />}
        </Field>
      </div>
    </Dialog>
  )
}
