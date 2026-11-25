// Dates are stored in UTC and always shown in Brisbane time, Australian style (design-system.md section 9).
const zone = 'Australia/Brisbane'

const dateTime = new Intl.DateTimeFormat('en-AU', {
  timeZone: zone,
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  hour: 'numeric',
  minute: '2-digit',
  hour12: true,
})
const longDate = new Intl.DateTimeFormat('en-AU', { timeZone: zone, weekday: 'long', day: 'numeric', month: 'long' })
const hourOnly = new Intl.DateTimeFormat('en-AU', { timeZone: zone, hour: 'numeric', hourCycle: 'h23' })

// Fixed short month names: Australian locale data spells some out ("June", "Sept"), which reads unevenly in lists.
const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "Tue 4 Nov, 3:00pm" */
export function formatDateTime(iso: string | Date): string {
  const parts = dateTime.formatToParts(typeof iso === 'string' ? new Date(iso) : iso)
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? ''
  const monthIndex = Number(new Intl.DateTimeFormat('en-AU', { timeZone: zone, month: 'numeric' }).format(typeof iso === 'string' ? new Date(iso) : iso)) - 1
  return `${get('weekday')} ${get('day')} ${months[monthIndex]}, ${get('hour')}:${get('minute')}${get('dayPeriod').toLowerCase().replace(/\./g, '')}`
}

/** "Wednesday 25 November" */
export function formatLongDate(date: Date): string {
  return longDate.format(date).replace(',', '')
}

/** "Good morning" / "Good afternoon" / "Good evening", by Brisbane time. */
export function greeting(now: Date): string {
  const hour = Number(hourOnly.format(now))
  if (hour < 12) return 'Good morning'
  if (hour < 17) return 'Good afternoon'
  return 'Good evening'
}

/** Relative time for "last seen": "just now", "5 minutes ago", "3 days ago". */
export function timeAgo(iso: string, now = new Date()): string {
  const seconds = Math.max(0, (now.getTime() - new Date(iso).getTime()) / 1000)
  if (seconds < 90) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} minutes ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return hours === 1 ? '1 hour ago' : `${hours} hours ago`
  const days = Math.round(hours / 24)
  if (days < 30) return days === 1 ? 'yesterday' : `${days} days ago`
  return formatDateTime(iso)
}

/** How long something has waited: "today", "1 day", "6 days". */
export function waited(iso: string, now = new Date()): string {
  const days = Math.max(0, Math.floor((now.getTime() - new Date(iso).getTime()) / 86_400_000))
  return days === 0 ? 'today' : days === 1 ? '1 day' : `${days} days`
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  return ((parts[0][0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase()
}

export function firstName(name: string): string {
  return name.trim().split(/\s+/)[0] ?? name
}

/** A date-only value ("2027-12-31") as "31 Dec 2027". Date-only values have no time zone. */
export function formatDate(value: string | null | undefined): string {
  if (!value) return ''
  const [y, m, d] = value.split('-').map(Number)
  return `${d} ${months[m - 1]} ${y}`
}

/** Whole days from today (Brisbane) to a date-only value; negative when it has passed. */
export function daysUntil(value: string, now = new Date()): number {
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: zone }).format(now) // yyyy-mm-dd
  return Math.round((Date.parse(`${value}T00:00:00Z`) - Date.parse(`${today}T00:00:00Z`)) / 86_400_000)
}

/** "$1,200,000.00 AUD" in detail; "$1.2m" when short. */
export function formatMoney(value: number | null | undefined, currency = 'AUD', short = false): string {
  if (value === null || value === undefined) return ''
  if (short) {
    const compact = new Intl.NumberFormat('en-AU', { notation: 'compact', maximumFractionDigits: 1 }).format(value).toLowerCase()
    return `$${compact}${currency === 'AUD' ? '' : ` ${currency}`}`
  }
  return `$${new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)} ${currency}`
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}
