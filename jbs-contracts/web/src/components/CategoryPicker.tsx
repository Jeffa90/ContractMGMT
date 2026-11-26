// Picks a category from the spend taxonomy (D-115): Family, then Category, then an optional Subcategory, each a short drop-down
// that only shows what's under the one before. Or type in "Find a category" to jump straight to one.
import { useMemo, useState } from 'react'
import { Combobox } from '@/components/Combobox'
import { Select } from '@/components/form'
import { Field } from '@/components/ui'
import { categoryPaths, type Category } from '@/lib/contracts'

export function CategoryPicker({
  categories,
  value,
  onChange,
  label,
  required,
  error,
  help,
  disabled,
}: {
  categories: Category[]
  value: number | null
  onChange: (id: number | null) => void
  label: string
  required?: boolean
  error?: string
  help?: string
  disabled?: boolean
}) {
  const byId = useMemo(() => new Map(categories.map((c) => [c.id, c])), [categories])
  // The chosen node and the levels above it, family first.
  const line = useMemo(() => {
    const out: Category[] = []
    for (let at = value === null ? undefined : byId.get(value); at; at = at.parentId === null ? undefined : byId.get(at.parentId)) out.unshift(at)
    return out
  }, [value, byId])
  // A family picked before its category: kept here until a category is chosen.
  const [family, setFamily] = useState<number | null>(line[0]?.id ?? null)
  const familyId = line[0]?.id ?? family
  const categoryId = line[1]?.id ?? null
  const subcategoryId = line[2]?.id ?? null

  // Hidden ones show only where they're already chosen.
  const under = (parentId: number | null) => categories.filter((c) => c.parentId === parentId && (c.isActive || line.some((l) => l.id === c.id)))
  const families = under(null)
  const cats = familyId === null ? [] : under(familyId)
  const subs = categoryId === null ? [] : under(categoryId)
  const findable = useMemo(
    () =>
      categoryPaths(categories)
        .filter((p) => p.isActive || p.id === value)
        .map((p) => ({ value: String(p.id), label: p.path })),
    [categories, value],
  )

  return (
    <fieldset className="flex flex-col gap-3" disabled={disabled}>
      <legend className="mb-1 text-sm font-medium">
        {label}
        {required && <span className="font-normal text-muted-foreground"> (required)</span>}
      </legend>
      <Combobox
        aria-label="Find a category by typing"
        placeholder="Find one: type a word, for example casing"
        options={findable}
        value={value === null ? '' : String(value)}
        onChange={(v) => {
          const id = Number(v)
          onChange(id)
          let top = byId.get(id)
          while (top?.parentId) top = byId.get(top.parentId)
          setFamily(top?.id ?? null)
        }}
        disabled={disabled}
      />
      <div className="grid gap-3 sm:grid-cols-3">
        <Field label="Family" error={!familyId ? error : undefined}>
          {(p) => (
            <Select
              {...p}
              value={familyId ?? ''}
              onChange={(e) => {
                setFamily(e.target.value ? Number(e.target.value) : null)
                onChange(null)
              }}
              disabled={disabled || families.length === 0}
            >
              <option value="">{families.length === 0 ? 'None yet' : 'Choose'}</option>
              {families.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                  {!c.isActive ? ' (hidden)' : ''}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label="Category" error={familyId ? error : undefined}>
          {(p) => (
            <Select
              {...p}
              value={categoryId ?? ''}
              onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}
              disabled={disabled || cats.length === 0}
            >
              <option value="">{familyId === null ? 'Choose a family first' : 'Choose'}</option>
              {cats.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                  {!c.isActive ? ' (hidden)' : ''}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label="Subcategory (optional)">
          {(p) => (
            <Select
              {...p}
              value={subcategoryId ?? ''}
              onChange={(e) => onChange(e.target.value ? Number(e.target.value) : categoryId)}
              disabled={disabled || subs.length === 0}
            >
              <option value="">
                {categoryId === null ? 'Choose a category first' : subs.length === 0 ? 'None for this category' : 'None: the whole category'}
              </option>
              {subs.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                  {!c.isActive ? ' (hidden)' : ''}
                </option>
              ))}
            </Select>
          )}
        </Field>
      </div>
      {help && <p className="text-xs text-muted-foreground">{help}</p>}
    </fieldset>
  )
}
