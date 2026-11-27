import { request, type FullConfig } from '@playwright/test'
import { headers } from './people'

/** Loads the fictional demo data once, as the admin. A second run on the same database finds it already loaded. */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0].use.baseURL!
  const api = await request.newContext({ baseURL, extraHTTPHeaders: headers('admin') })
  const live = await api.get('/health/live')
  if (!live.ok()) throw new Error(`JBS Contracts isn't running at ${baseURL} (health check: ${live.status()}).`)
  const res = await api.post('/api/admin/demo-data')
  if (!res.ok() && res.status() !== 409) throw new Error(`Couldn't load the demo data: ${res.status()} ${await res.text()}`)
  // The tests type their own titles; titles made from the details (D-132, on in a new database) are checked on their own.
  const titles = await api.put('/api/settings/title-from-details', { data: { fromDetails: false } })
  if (!titles.ok()) throw new Error(`Couldn't set the title setting: ${titles.status()} ${await titles.text()}`)
  await api.dispose()
}
