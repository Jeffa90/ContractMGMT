// Load or refresh the fictional demo data (QA only, admins; D-66, D-97).
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { Dialog } from './Dialog'
import { Button } from './ui'

export function DemoDataDialog({ refresh, onClose }: { refresh: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const load = useMutation({
    mutationFn: () => api.post<{ contracts: number; archived: number; people: number }>('/api/admin/demo-data', {}),
    onSuccess: (r) => {
      void queryClient.invalidateQueries()
      toast.success(
        r.archived > 0
          ? `Demo data refreshed: ${r.archived} old demo contracts archived, ${r.contracts} new ones shared out across ${r.people} people.`
          : `Demo data loaded: ${r.contracts} made-up contracts shared out across ${r.people} people.`,
      )
      onClose()
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : "That didn't work. Try again."),
  })
  const action = refresh ? 'Refresh demo data' : 'Load demo data'
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${action}?`}
      description="Only offered on QA, for trying the app. It takes a few seconds."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={load.isPending} onClick={() => load.mutate()}>
            {action}
          </Button>
        </>
      }
    >
      <ul className="flex list-disc flex-col gap-1 pl-5 text-sm">
        {refresh && <li>The demo contracts loaded before are archived (out of every list; nothing is deleted), and a fresh set is loaded, dated from today.</li>}
        <li>
          Everyone gets something to try, by their roles: owners get drafts, contracts back from Legal, notice dates coming up and renewals; Legal people get
          reviews; approvers get contracts and Variations to approve; Procurement get suppliers with documents, reviews and actions.
        </li>
        <li>It also adds Variations, an emergency unlock, ended and archived contracts, and contracts imported from the old system with their files.</li>
        <li>
          <strong>Demo items never send email</strong>, even when someone acts on them. Their names and the four demo people end in "(demo)" or are made up.
        </li>
        <li>Nothing you or anyone else added is changed or removed.</li>
      </ul>
    </Dialog>
  )
}
