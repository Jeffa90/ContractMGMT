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

/** "Tue 4 Nov, 3:00pm" */
export function formatDateTime(iso: string | Date): string {
  const parts = dateTime.formatToParts(typeof iso === 'string' ? new Date(iso) : iso)
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? ''
  return `${get('weekday')} ${get('day')} ${get('month')}, ${get('hour')}:${get('minute')}${get('dayPeriod').toLowerCase().replace(/\./g, '')}`
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

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  return ((parts[0][0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase()
}

export function firstName(name: string): string {
  return name.trim().split(/\s+/)[0] ?? name
}
