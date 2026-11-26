import { createContext, createElement, useContext, type ReactNode } from 'react'
import type { Me } from './api'

const MeContext = createContext<Me | null>(null)

export function MeProvider({ me, children }: { me: Me; children: ReactNode }) {
  return createElement(MeContext.Provider, { value: me }, children)
}

/** The signed-in person. Only used inside the signed-in app, where it is always set. */
export function useMe(): Me {
  const me = useContext(MeContext)
  if (!me) throw new Error('useMe outside MeProvider')
  return me
}

export function roleSummary(roles: Me['person']['roles']): string {
  const names = [
    roles.groupHead ? 'Group Head' : roles.approver ? 'Approver' : null,
    roles.contractOwner ? 'Contract owner' : null,
    roles.legal ? 'Legal' : null,
    roles.procurement ? 'Procurement' : null,
    roles.reports ? 'Reports' : null,
    roles.admin ? 'Admin' : null,
    roles.auditor ? 'Auditor' : null,
  ].filter(Boolean)
  return names.length ? names.join(', ') : 'Can look only'
}
