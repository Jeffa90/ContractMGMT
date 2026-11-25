import { daysUntil, firstName, formatBytes, formatDate, formatDateTime, formatLongDate, formatMoney, greeting, initials, timeAgo } from './format'

describe('dates, money and sizes', () => {
  it('shows date-only values without shifting the day', () => {
    expect(formatDate('2027-12-31')).toBe('31 Dec 2027')
    expect(daysUntil('2026-12-05', new Date('2026-11-25T02:00:00Z'))).toBe(10)
    expect(daysUntil('2026-11-20', new Date('2026-11-25T02:00:00Z'))).toBe(-5)
  })
  it('formats money the Australian way', () => {
    expect(formatMoney(1200000)).toBe('$1,200,000.00 AUD')
    expect(formatMoney(1200000, 'AUD', true)).toBe('$1.2m')
    expect(formatMoney(250, 'USD', true)).toBe('$250 USD')
    expect(formatBytes(2_500_000)).toBe('2.4 MB')
  })
})

describe('dates are shown in Brisbane time, Australian style', () => {
  it('formats a UTC time as "Tue 4 Nov, 3:00pm" in Brisbane (UTC+10, no daylight saving)', () => {
    expect(formatDateTime('2025-11-04T05:00:00Z')).toBe('Tue 4 Nov, 3:00pm')
    expect(formatDateTime('2025-11-03T23:05:00Z')).toBe('Tue 4 Nov, 9:05am')
  })
  it('formats the long date for the greeting', () => {
    expect(formatLongDate(new Date('2026-11-25T02:00:00Z'))).toBe('Wednesday 25 November')
  })
  it('greets by Brisbane time of day', () => {
    expect(greeting(new Date('2026-11-24T21:00:00Z'))).toBe('Good morning') // 7am
    expect(greeting(new Date('2026-11-25T04:00:00Z'))).toBe('Good afternoon') // 2pm
    expect(greeting(new Date('2026-11-25T09:00:00Z'))).toBe('Good evening') // 7pm
  })
  it('says how long ago in plain words', () => {
    const now = new Date('2026-11-25T04:00:00Z')
    expect(timeAgo('2026-11-25T03:59:30Z', now)).toBe('just now')
    expect(timeAgo('2026-11-25T03:40:00Z', now)).toBe('20 minutes ago')
    expect(timeAgo('2026-11-24T04:00:00Z', now)).toBe('yesterday')
  })
})

describe('names', () => {
  it('makes initials and first names', () => {
    expect(initials('Jeff Judd')).toBe('JJ')
    expect(initials('Ann Marie Ho')).toBe('AH')
    expect(initials('Cher')).toBe('C')
    expect(firstName('Jeff Judd')).toBe('Jeff')
  })
})
