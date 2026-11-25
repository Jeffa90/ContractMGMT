// The contract field layout an admin arranges (Admin, Contract fields). Forms and contract pages draw from it.
export type FieldKind =
  | 'BuiltIn'
  | 'ShortText'
  | 'LongText'
  | 'WholeNumber'
  | 'Decimal'
  | 'Money'
  | 'Date'
  | 'YesNo'
  | 'Choice'
  | 'MultiChoice'
  | 'Person'
  | 'Supplier'
  | 'Link'
  | 'Email'

export type ChoiceOption = { key: string; label: string; hidden: boolean }

export type LayoutSection = { key: string; title: string; isHidden: boolean }

export type LayoutField = {
  key: string
  isBuiltIn: boolean
  isRuleField: boolean
  kind: FieldKind
  kindName: string
  label: string
  help: string | null
  sectionKey: string
  isRequired: boolean
  isHidden: boolean
  showInList: boolean
  filterable: boolean
  inExport: boolean
  afterEffective: 'Variation' | 'AnyTime'
  choices: ChoiceOption[]
  maxLength: number | null
  minValue: number | null
  maxValue: number | null
  hasValues: boolean
}

export type Layout = { versionId: number; sections: LayoutSection[]; fields: LayoutField[] }

export const addedKinds: { kind: FieldKind; label: string; description: string }[] = [
  { kind: 'ShortText', label: 'Short text', description: 'A line of text, for example a policy number.' },
  { kind: 'LongText', label: 'Long text', description: 'A paragraph or more.' },
  { kind: 'WholeNumber', label: 'Whole number', description: 'For example a count of sites.' },
  { kind: 'Decimal', label: 'Decimal number', description: 'For example a rate of 2.5.' },
  { kind: 'Money', label: 'Money (AUD)', description: 'An amount in dollars.' },
  { kind: 'Date', label: 'Date', description: 'For example an insurance expiry date.' },
  { kind: 'YesNo', label: 'Yes/No', description: 'A tick box.' },
  { kind: 'Choice', label: 'Pick one from a list', description: 'You type the options.' },
  { kind: 'MultiChoice', label: 'Pick several from a list', description: 'You type the options; people tick any number.' },
  { kind: 'Person', label: 'Person', description: 'Someone added to JBS Contracts.' },
  { kind: 'Supplier', label: 'Supplier', description: 'A supplier in JBS Contracts.' },
  { kind: 'Link', label: 'Link', description: 'A web link, for example a SharePoint folder.' },
  { kind: 'Email', label: 'Email', description: 'An email address.' },
]

/** Visible sections in order, each with its visible fields in order. */
export function arrange(layout: Layout, includeHidden = false) {
  return layout.sections
    .filter((s) => includeHidden || !s.isHidden)
    .map((section) => ({ section, fields: layout.fields.filter((f) => f.sectionKey === section.key && (includeHidden || !f.isHidden)) }))
    .filter((g) => includeHidden || g.fields.length > 0)
}

export type FieldValue = string | number | boolean | string[] | null
