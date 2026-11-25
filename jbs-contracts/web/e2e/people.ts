// The fictional people the tests sign in as. The admin is the first admin the test copy of the app is started with;
// the others come from the demo data (they can't sign in on a real server; header sign-in is for tests only).
export const DOMAIN = process.env.E2E_DOMAIN ?? 'DEMO'

export const people = {
  admin: 'amorgan',
  owner: 'demo.ppatel',
  legal: 'demo.snguyen',
  approver: 'demo.gwallace',
  stranger: 'nobody.here',
} as const

export type Who = keyof typeof people

export const account = (who: Who) => `${DOMAIN}\\${people[who]}`

/** Headers a signed-in browser sends: who they are, and the header every change needs. */
export const headers = (who: Who) => ({ 'X-Signed-In-As': account(who), 'X-JBS-Contracts': '1' })
