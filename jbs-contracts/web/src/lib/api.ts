// Talks to the JBS Contracts API on the same site. Windows sign-in is handled by the browser and IIS.

export type ApiErrorBody = { code: string; message: string; fields?: Record<string, string>; extra?: unknown }

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly fields: Record<string, string>
  readonly extra: unknown
  constructor(status: number, body: ApiErrorBody) {
    super(body.message)
    this.status = status
    this.code = body.code
    this.fields = body.fields ?? {}
    this.extra = body.extra
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, {
      method,
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        // Every change carries this header; the API refuses changes without it (protection against other sites).
        ...(method !== 'GET' ? { 'X-JBS-Contracts': '1' } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
  } catch {
    throw new ApiError(0, {
      code: 'offline',
      message: "We couldn't reach JBS Contracts. Check you're connected to the JBS network, then try again.",
    })
  }
  if (response.status === 204) return undefined as T
  const text = await response.text()
  let data: unknown = undefined
  try {
    data = text ? JSON.parse(text) : undefined
  } catch {
    data = undefined
  }
  if (!response.ok) {
    const fallback: ApiErrorBody = {
      code: 'error',
      message: 'Something went wrong on our side. Try again in a minute; if it keeps happening, tell your admin.',
    }
    const body = data && typeof data === 'object' && 'message' in data ? (data as ApiErrorBody) : fallback
    throw new ApiError(response.status, body)
  }
  return data as T
}

export const api = {
  get: <T,>(path: string) => request<T>('GET', path),
  post: <T,>(path: string, body: unknown) => request<T>('POST', path, body),
  put: <T,>(path: string, body: unknown) => request<T>('PUT', path, body),
  del: <T,>(path: string) => request<T>('DELETE', path),
}

export type Roles = {
  contractOwner: boolean
  legal: boolean
  approver: boolean
  groupHead: boolean
  admin: boolean
  auditor: boolean
  procurement: boolean
  /** Sees Reports (not the audit trail) for the contracts they may see (D-110). */
  reports: boolean
}

export type Person = {
  id: number
  account: string
  name: string
  email: string
  roles: Roles
  isActive: boolean
  lastSignInUtc: string | null
  rowVersion: string
}

export type Me = {
  person: Person
  defaultDomain: string | null
  /** Admins on QA: whether fictional demo data can be loaded, or already is. */
  demoData: 'available' | 'loaded' | null
  /** QA only: admins can try the app as someone else (D-81). */
  canTryAs: boolean
  tryingAs: { realName: string } | null
  /** How their reminder emails come (D-99): their own choice, or null for the default. */
  reminderDelivery: 'Each' | 'Daily' | 'Weekly' | null
  defaultReminderDelivery: 'Each' | 'Daily' | 'Weekly'
}
