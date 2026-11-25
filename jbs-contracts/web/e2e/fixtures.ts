import AxeBuilder from '@axe-core/playwright'
import { test as base, expect, type APIRequestContext, type Page } from '@playwright/test'
import { headers, type Who } from './people'

type Fixtures = {
  /** Sign the page in as one of the fictional people (admin unless a test says otherwise). */
  signInAs: (who: Who) => Promise<void>
  /** The API, signed in as the admin, for finding things to open. */
  api: APIRequestContext
}

export const test = base.extend<Fixtures>({
  signInAs: async ({ page }, provide) => {
    await provide(async (who) => {
      await page.setExtraHTTPHeaders({ 'X-Signed-In-As': headers(who)['X-Signed-In-As'] })
    })
  },
  page: async ({ page }, provide) => {
    await page.setExtraHTTPHeaders({ 'X-Signed-In-As': headers('admin')['X-Signed-In-As'] })
    await provide(page)
  },
  api: async ({ playwright, baseURL }, provide) => {
    const api = await playwright.request.newContext({ baseURL, extraHTTPHeaders: headers('admin') })
    await provide(api)
    await api.dispose()
  },
})

export { expect }

/** Waits until the screen has finished loading: its heading is showing and no loading placeholders are left. */
export async function settled(page: Page, heading?: string | RegExp) {
  const h1 = page.locator('main h1').first()
  if (heading) await expect(h1).toHaveText(heading)
  else await expect(h1).toBeVisible()
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)
  await page.waitForLoadState('networkidle')
}

/** axe accessibility check (WCAG 2.2 A and AA). Any problem fails the test, listed plainly. */
export async function expectAccessible(page: Page) {
  // Let fades and slides finish (a pop-up message checked half-faded-in has no real contrast). Endless ones, like spinners, are left.
  await page.evaluate(() =>
    Promise.all(
      document.getAnimations()
        .filter((a) => Number.isFinite(a.effect?.getComputedTiming().endTime ?? Infinity))
        .map((a) => a.finished.catch(() => undefined)),
    ),
  )
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze()
  const problems = results.violations.map(
    (v) => `${v.id} (${v.impact}): ${v.help}\n${v.nodes.slice(0, 5).map((n) => `   - ${n.target.join(' ')}: ${n.failureSummary?.split('\n')[1]?.trim() ?? ''}`).join('\n')}`,
  )
  expect(problems, problems.join('\n\n')).toEqual([])
}

/** On a phone, nothing should make the whole page scroll sideways. */
export async function expectNoSideScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow, 'The page scrolls sideways').toBeLessThanOrEqual(0)
}
