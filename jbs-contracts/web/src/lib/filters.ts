// Filters on the Contracts list, kept in the address as f.{field key}={value}.
import { canFilter, type LayoutField } from './layout'

export const filterPrefix = 'f.'

/** The fields shown as filters, in layout order. */
export function filterFields(fields: LayoutField[] | undefined) {
  return (fields ?? []).filter((f) => f.filterable && !f.isHidden && canFilter(f))
}

/** The filters chosen in the address, as field key to value. */
export function chosenFilters(params: URLSearchParams) {
  const chosen: Record<string, string> = {}
  params.forEach((value, key) => {
    if (key.startsWith(filterPrefix) && value) chosen[key.slice(filterPrefix.length)] = value
  })
  return chosen
}
