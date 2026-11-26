import { useQuery } from '@tanstack/react-query'
import { Building2, Plus, Search } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { ExcelButtons } from '@/components/ExcelImport'
import { PageHeader } from '@/components/Help'
import { Alert, Badge, Button, Card, EmptyState, Segmented, Skeleton, TextInput } from '@/components/ui'
import { api } from '@/lib/api'
import type { Supplier } from '@/lib/contracts'
import { useMe } from '@/lib/me'
import { help } from '@/help'
import { SupplierDialog } from './ContractForm'

const formatAbn = (abn: string | null) => (abn ? `${abn.slice(0, 2)} ${abn.slice(2, 5)} ${abn.slice(5, 8)} ${abn.slice(8)}` : '')

export function SuppliersPage() {
  const { person } = useMe()
  const canChange = person.roles.contractOwner || person.roles.admin
  const [params] = useSearchParams()
  const [search, setSearch] = useState(() => params.get('q') ?? '') // ?q= from search (Ctrl+K)
  const [show, setShow] = useState<'active' | 'hidden'>('active')
  const [editing, setEditing] = useState<Supplier | 'new' | null>(null)
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })
  const all = useMemo(() => suppliers.data ?? [], [suppliers.data])
  const visible = useMemo(() => {
    const q = search.trim().toLowerCase()
    return all
      .filter((s) => (show === 'active' ? s.isActive : !s.isActive))
      .filter((s) => !q || [s.name, s.abn ?? '', s.vendorNumber ?? ''].some((v) => v.toLowerCase().includes(q)))
  }, [all, search, show])

  return (
    <>
      <PageHeader
        title="Suppliers"
        description="Everyone JBS has a contract with."
        help={help.suppliers}
        action={
          canChange ? (
            <Button variant="primary" icon={<Plus aria-hidden className="size-5" />} onClick={() => setEditing('new')}>
              Add supplier
            </Button>
          ) : undefined
        }
      />
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
      <div className="flex flex-col gap-3 md:flex-row md:items-center">
        <div className="relative md:w-80">
          <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-5 -translate-y-1/2 text-muted-foreground" />
          <TextInput type="search" aria-label="Find a supplier" placeholder="Name, ABN or vendor number" value={search} onChange={(e) => setSearch(e.target.value)} className="pl-10" />
        </div>
        <Segmented
          label="Show"
          value={show}
          onChange={setShow}
          options={[
            { value: 'active', label: 'In use', count: all.filter((s) => s.isActive).length },
            { value: 'hidden', label: 'Hidden', count: all.filter((s) => !s.isActive).length },
          ]}
        />
      </div>
        <div className="flex flex-wrap gap-1">
          <ExcelButtons kind="suppliers" canImport={person.roles.admin} invalidate={[['suppliers']]} />
          <ExcelButtons kind="contacts" noun="contacts" canImport={person.roles.admin} invalidate={[['suppliers']]} />
        </div>
      </div>

      {suppliers.isPending ? (
        <Card className="flex flex-col gap-3 p-4">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} className="h-5 w-72" />
          ))}
        </Card>
      ) : suppliers.isError ? (
        <Alert tone="danger" title="We couldn't load suppliers.">{(suppliers.error as Error).message}</Alert>
      ) : visible.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Building2 />}
            title={search ? `No suppliers match "${search}".` : show === 'hidden' ? 'No hidden suppliers.' : 'No suppliers yet. Suppliers you add will appear here.'}
            action={
              canChange && !search && show === 'active' ? (
                <Button variant="primary" onClick={() => setEditing('new')}>
                  Add supplier
                </Button>
              ) : undefined
            }
          />
        </Card>
      ) : (
        <>
          <Card className="overflow-hidden">
            <ul className="divide-y divide-border">
              {visible.map((s) => (
                <li key={s.id} className="flex flex-col gap-1 p-4 sm:flex-row sm:items-center sm:justify-between">
                  <div className="min-w-0">
                    <Link to={`/suppliers/${s.id}`} className="font-medium text-primary hover:underline">
                      {s.name}
                    </Link>
                    <p className="tabular text-xs text-muted-foreground">
                      {[
                        s.abn ? `ABN ${formatAbn(s.abn)}` : null,
                        s.vendorNumber ? `Vendor ${s.vendorNumber}` : null,
                        s.parentSupplierName ? `Part of ${s.parentSupplierName}` : null,
                        s.memberCount > 0 ? `Group of ${s.memberCount + 1}` : null,
                      ]
                        .filter(Boolean)
                        .join(' · ') || 'No ABN or vendor number yet'}
                    </p>
                  </div>
                  {/* A group member's contracts are its group's (D-116). */}
                  <Link to={`/contracts?view=all&q=${encodeURIComponent(s.parentSupplierName ?? s.name)}`} className="text-sm text-primary hover:underline">
                    <Badge tone="brand">
                      {s.contractCount} {s.contractCount === 1 ? 'contract' : 'contracts'}
                    </Badge>
                  </Link>
                </li>
              ))}
            </ul>
          </Card>
          <p className="text-xs text-muted-foreground">
            {visible.length} {visible.length === 1 ? 'supplier' : 'suppliers'}
          </p>
        </>
      )}
      {editing && <SupplierDialog supplier={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => setEditing(null)} />}
    </>
  )
}
