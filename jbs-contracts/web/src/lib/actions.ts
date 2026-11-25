// Contract actions (Send to Legal, approve, ...): post with the page's row version, then refresh what shows the contract.
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api, ApiError } from './api'
import type { ContractDetail } from './contracts'

/** Runs a contract action with the page's row version, then refreshes the contract and lists. */
export function useAction(data: ContractDetail, onDone?: () => void) {
  const queryClient = useQueryClient()
  const c = data.contract
  return useMutation({
    mutationFn: ({ path, body, rowVersion }: { path: string; body?: object; success: string; rowVersion?: string }) =>
      api.post(`/api/contracts/${c.id}/${path}`, { ...body, rowVersion: rowVersion ?? c.rowVersion }),
    onSuccess: (_, v) => {
      queryClient.invalidateQueries({ queryKey: ['contract', c.id] })
      queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success(v.success)
      onDone?.()
    },
  })
}

export function errorText(e: unknown, field?: string) {
  if (e instanceof ApiError) return (field && e.fields[field]) || e.message
  return "That didn't work. Try again."
}
