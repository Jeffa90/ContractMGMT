// Draws any added contract field from its definition (design-system "Field renderer"). Forms never hand-code an added field.
import type { FieldValue, LayoutField } from '@/lib/layout'
import { Select, Textarea } from './form'
import { MultiSelect } from './MultiSelect'
import { Field, TextInput } from './ui'

export type PickList = { people: { id: number; name: string }[]; suppliers: { id: number; name: string }[] }

export function FieldInput({
  field,
  value,
  onChange,
  error,
  picks,
  disabled,
}: {
  field: LayoutField
  value: FieldValue
  onChange: (value: FieldValue) => void
  error?: string
  picks: PickList
  disabled?: boolean
}) {
  const help = field.help ?? undefined
  if (field.kind === 'YesNo') {
    return (
      <Field label={field.label} help={help} error={error}>
        {(p) => (
          <Select {...p} disabled={disabled} value={value === true ? 'yes' : value === false ? 'no' : ''} onChange={(e) => onChange(e.target.value === '' ? null : e.target.value === 'yes')}>
            <option value="">Not filled in</option>
            <option value="yes">Yes</option>
            <option value="no">No</option>
          </Select>
        )}
      </Field>
    )
  }
  if (field.kind === 'MultiChoice') {
    const selected = Array.isArray(value) ? value : []
    const options = field.choices.filter((c) => !c.hidden || selected.includes(c.key))
    return (
      <Field label={field.label} required={field.isRequired} help={help ?? 'Choose one or more.'} error={error}>
        {(p) => (
          <MultiSelect
            {...p}
            disabled={disabled}
            options={options.map((c) => ({ id: c.key, label: c.label, hidden: c.hidden }))}
            selected={selected}
            onChange={(keys) => onChange(keys)}
          />
        )}
      </Field>
    )
  }
  return (
    <Field label={field.label} required={field.isRequired} help={help} error={error}>
      {(p) => {
        switch (field.kind) {
          case 'LongText':
            return <Textarea {...p} disabled={disabled} maxLength={field.maxLength ?? 4000} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />
          case 'WholeNumber':
          case 'Decimal':
          case 'Money':
            return (
              <TextInput
                {...p}
                disabled={disabled}
                inputMode={field.kind === 'WholeNumber' ? 'numeric' : 'decimal'}
                className="tabular sm:w-60"
                value={value === null || value === undefined ? '' : String(value)}
                onChange={(e) => onChange(e.target.value.trim() === '' ? null : e.target.value)}
              />
            )
          case 'Date':
            return <TextInput {...p} disabled={disabled} type="date" className="sm:w-60" value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value || null)} />
          case 'Choice': {
            const options = field.choices.filter((c) => !c.hidden || c.key === value)
            return (
              <Select {...p} disabled={disabled} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value || null)}>
                <option value="">Not chosen</option>
                {options.map((c) => (
                  <option key={c.key} value={c.key}>
                    {c.label}
                  </option>
                ))}
              </Select>
            )
          }
          case 'Person':
          case 'Supplier': {
            const list = field.kind === 'Person' ? picks.people : picks.suppliers
            return (
              <Select {...p} disabled={disabled} value={value === null || value === undefined ? '' : String(value)} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}>
                <option value="">Not chosen</option>
                {list.map((x) => (
                  <option key={x.id} value={x.id}>
                    {x.name}
                  </option>
                ))}
              </Select>
            )
          }
          case 'Link':
            return <TextInput {...p} disabled={disabled} type="url" placeholder="https://" value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />
          case 'Email':
            return <TextInput {...p} disabled={disabled} type="email" value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />
          default:
            return <TextInput {...p} disabled={disabled} maxLength={field.maxLength ?? 200} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />
        }
      }}
    </Field>
  )
}
