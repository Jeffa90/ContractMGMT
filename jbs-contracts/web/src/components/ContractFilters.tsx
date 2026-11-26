// Filters on the Contracts list: one drop-down per field an admin made filterable (Admin, Contract fields).
// The chosen values live in the address (f.{field key}={value}) so Back and shared links keep them.
import { useQuery } from '@tanstack/react-query'
import { X } from 'lucide-react'
import { useMemo } from 'react'
import { api } from '@/lib/api'
import { categoryPaths, type Lists, type Supplier } from '@/lib/contracts'
import type { LayoutField } from '@/lib/layout'
import { Combobox } from './Combobox'
import { Select } from './form'
import { Button, Field } from './ui'

type Option = { value: string; label: string }

const yesNo: Option[] = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
]

export function ContractFilters({
  fields,
  chosen,
  onChange,
  onClear,
}: {
  fields: LayoutField[]
  chosen: Record<string, string>
  onChange: (key: string, value: string) => void
  onClear: () => void
}) {
  const needs = (keys: string[], kinds: string[] = []) => fields.some((f) => keys.includes(f.key) || (!f.isBuiltIn && kinds.includes(f.kind)))
  const lists = useQuery({
    queryKey: ['lists'],
    queryFn: () => api.get<Lists>('/api/lists'),
    enabled: needs(['category', 'agreementType', 'businessUnit', 'site', 'entity', 'function', 'owner', 'backupOwner']),
  })
  const suppliers = useQuery({
    queryKey: ['suppliers'],
    queryFn: () => api.get<Supplier[]>('/api/suppliers'),
    enabled: needs(['supplier'], ['Supplier']),
  })
  const people = useQuery({
    queryKey: ['people-picker'],
    queryFn: () => api.get<{ id: number; name: string }[]>('/api/people/picker'),
    enabled: needs([], ['Person']),
  })

  const options = useMemo(() => {
    const items = (list: string) =>
      (lists.data?.items ?? []).filter((i) => i.listName === list && i.isActive).map((i) => ({ value: String(i.id), label: i.value }))
    const supplierOptions = (suppliers.data ?? []).filter((s) => s.isActive).map((s) => ({ value: String(s.id), label: s.name }))
    const owners = (lists.data?.owners ?? []).map((o) => ({
      value: String(o.id),
      label: o.name,
    }))
    // Families, categories and subcategories as paths (D-115): each includes what's under it.
    const categories = categoryPaths(lists.data?.categories ?? [], true)
      .filter((p) => p.isActive)
      .map((p) => ({ value: String(p.id), label: p.path }))
    const builtIn: Record<string, Option[]> = {
      supplier: supplierOptions,
      category: categories,
      agreementType: items('AgreementType'),
      businessUnit: items('BusinessUnit'),
      site: items('Site'),
      entity: items('Entity'),
      function: items('Function'),
      owner: owners,
      backupOwner: owners,
      confidential: yesNo,
      evergreen: yesNo,
      autoRenews: yesNo,
      needsLegalReview: yesNo,
    }
    return (f: LayoutField): Option[] => {
      if (f.isBuiltIn) return builtIn[f.key] ?? []
      switch (f.kind) {
        case 'Choice':
        case 'MultiChoice':
          return f.choices.filter((c) => !c.hidden).map((c) => ({ value: c.key, label: c.label }))
        case 'YesNo':
          return yesNo
        case 'Person':
          return (people.data ?? []).map((p) => ({
            value: String(p.id),
            label: p.name,
          }))
        case 'Supplier':
          return supplierOptions
        default:
          return []
      }
    }
  }, [lists.data, suppliers.data, people.data])

  const active = Object.keys(chosen).filter((k) => fields.some((f) => f.key === k)).length

  return (
    <div className="flex flex-col gap-3 rounded-[var(--radius-card)] border border-border bg-card p-4">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {fields.map((f) => (
          <Field key={f.key} label={f.label}>
            {(p) =>
              // Long lists (the spend taxonomy) are found by typing; the rest are drop-downs.
              options(f).length > 40 ? (
                <Combobox
                  {...p}
                  placeholder="Any: type to find"
                  options={[{ value: '', label: 'Any' }, ...options(f)]}
                  value={chosen[f.key] ?? ''}
                  onChange={(v) => onChange(f.key, v)}
                />
              ) : (
                <Select {...p} value={chosen[f.key] ?? ''} onChange={(e) => onChange(f.key, e.target.value)}>
                  <option value="">Any</option>
                  {options(f).map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              )
            }
          </Field>
        ))}
      </div>
      {active > 0 && (
        <div>
          <Button variant="link" icon={<X aria-hidden className="size-4" />} onClick={onClear}>
            Clear filters
          </Button>
        </div>
      )}
    </div>
  )
}
