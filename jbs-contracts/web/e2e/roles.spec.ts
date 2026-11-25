import { test, settled, expectAccessible, expect } from './fixtures'

// What other people see: their Home works and passes axe, and screens that aren't theirs say so plainly.

for (const who of ['owner', 'legal', 'approver'] as const) {
  test(`Home as ${who}`, async ({ page, signInAs }) => {
    await signInAs(who)
    await page.goto('/')
    await settled(page)
    await expectAccessible(page)
  })
}

test('A contract owner opening an admin screen is told it is for admins', async ({ page, signInAs }) => {
  await signInAs('owner')
  await page.goto('/admin/import')
  await settled(page, 'Import old contracts')
  await expect(page.getByText('Only admins can import old contracts.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Import old contracts' })).toHaveCount(0)
  await expectAccessible(page)
})

test('Someone who has not been added sees how to get access', async ({ page, signInAs }) => {
  await signInAs('stranger')
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText("You don't have access to JBS Contracts yet")
  await expect(page.getByText('nobody.here')).toBeVisible()
  await expectAccessible(page)
})
