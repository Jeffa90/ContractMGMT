import { test, settled, expectAccessible, expectNoSideScroll, expect } from './fixtures'

// Every main screen, as the admin (who can see them all), in all four projects: phone and desktop, light and dark.
// Each must load, pass axe, and not scroll sideways on a phone.

type Contract = { id: number; stage: string; isConfidential: boolean }
type Supplier = { id: number; name: string }

const fixed: [string, string][] = [
  ['Home', '/'],
  ['Contracts', '/contracts'],
  ['Raise a contract', '/contracts/new'],
  ['Renewals', '/renewals'],
  ['Reports', '/reports'],
  ['Team', '/team'],
  ['Help centre', '/help'],
  ['A help guide', '/help/raise-contract'],
  ['Suppliers', '/suppliers'],
  ['People', '/admin/people'],
  ['Lists', '/admin/lists'],
  ['Contract fields', '/admin/fields'],
  ['Reminders', '/admin/reminders'],
  ['Import old contracts', '/admin/import'],
  ['Feedback', '/feedback'],
  ['Page not found', '/no-such-page'],
]

for (const [name, path] of fixed) {
  test(`${name} (${path})`, async ({ page }) => {
    await page.goto(path)
    await settled(page)
    await expectAccessible(page)
    await expectNoSideScroll(page)
  })
}

for (const stage of ['Draft', 'LegalReview', 'Approval', 'Effective']) {
  test(`A contract at ${stage}`, async ({ page, api }) => {
    const all: Contract[] = await (await api.get('/api/contracts?view=all')).json()
    const contract = all.find((k) => k.stage === stage)
    test.skip(!contract, `The demo data has no contract at ${stage}`)
    await page.goto(`/contracts/${contract!.id}`)
    await settled(page)
    await expectAccessible(page)
    await expectNoSideScroll(page)
  })
}

test('A supplier page, each tab', async ({ page, api }) => {
  const suppliers: Supplier[] = await (await api.get('/api/suppliers')).json()
  const supplier = suppliers.find((s) => s.name.startsWith('Southern Cross Freight')) ?? suppliers[0]
  await page.goto(`/suppliers/${supplier.id}`)
  await settled(page)
  const tabs = await page.getByRole('radiogroup', { name: 'Show', exact: true }).getByRole('radio').all()
  expect(tabs.length).toBeGreaterThan(1)
  for (const tab of tabs) {
    await tab.click()
    await expect(tab).toBeChecked()
    await page.waitForLoadState('networkidle')
    await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)
    await expectAccessible(page)
    await expectNoSideScroll(page)
  }
})

test('A business review to print, and its form', async ({ page, api }) => {
  const suppliers: Supplier[] = await (await api.get('/api/suppliers')).json()
  let found: { supplier: number; review: number } | null = null
  for (const s of suppliers) {
    const detail = await (await api.get(`/api/suppliers/${s.id}`)).json()
    if (detail.reviews.length > 0) {
      found = { supplier: s.id, review: detail.reviews[0].id }
      break
    }
  }
  expect(found, 'the demo data has a business review').not.toBeNull()
  await page.goto(`/suppliers/${found!.supplier}/reviews/${found!.review}/print`)
  await settled(page)
  await expect(page.getByRole('heading', { level: 1, name: /^Business review: / })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Print or save as PDF' })).toBeVisible()
  await expectAccessible(page)
  await expectNoSideScroll(page)

  // The form: the supplier's contacts to tick, a new contact, and actions for JBS people or the supplier.
  await page.goto(`/suppliers/${found!.supplier}?tab=reviews`)
  await settled(page)
  await page.getByRole('button', { name: 'Add business review' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'Add new contact' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Add an action' }).click()
  await expect(dialog.locator('optgroup[label$="(not emailed)"]')).toHaveCount(1)
  await expectAccessible(page)
})

test('Reports, each tab', async ({ page }) => {
  await page.goto('/reports')
  await settled(page)
  const tabs = await page.getByRole('radiogroup', { name: 'Report', exact: true }).getByRole('radio').all()
  expect(tabs.length).toBeGreaterThan(1)
  for (const tab of tabs) {
    await tab.click()
    await expect(tab).toBeChecked()
    await page.waitForLoadState('networkidle')
    await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)
    await expectAccessible(page)
    await expectNoSideScroll(page)
  }
})
