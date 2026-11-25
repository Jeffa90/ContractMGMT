import { test, settled, expectAccessible, expect } from './fixtures'

// The everyday path, by keyboard and pointer: skip to the content, raise a draft, find it again.

test('Skip to content is the first thing the keyboard reaches', async ({ page, isMobile }, info) => {
  test.skip(info.project.use.hasTouch === true || isMobile, 'Keyboard check is for desktop')
  await page.goto('/')
  await settled(page)
  await page.keyboard.press('Tab')
  const skip = page.getByRole('link', { name: 'Skip to content' })
  await expect(skip).toBeFocused()
  await expect(skip).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/#main$/)
})

test('Raise a draft contract, then find it', async ({ page }, info) => {
  const title = `E2E freight trial ${info.project.name} ${Date.now()}`
  await page.goto('/contracts/new')
  await settled(page)

  // Saving with nothing filled in says what's missing, next to the field.
  await page.getByRole('button', { name: 'Save draft' }).click()
  await expect(page.getByText(/Give the contract a title/).first()).toBeVisible()
  await expectAccessible(page)

  await page.getByLabel(/^Title/).fill(title)
  await page.getByLabel(/^Supplier/).selectOption({ label: 'Southern Cross Freight (demo)' })
  await page.getByLabel(/^Category/).selectOption({ label: 'Freight › Linehaul' })
  await page.getByRole('button', { name: 'Save draft' }).click()

  await expect(page).toHaveURL(/\/contracts\/\d+$/)
  await settled(page, title)
  await expect(page.getByText('Draft', { exact: true }).first()).toBeVisible()
  await expectAccessible(page)

  await page.goto('/contracts?view=drafts')
  await settled(page)
  await expect(page.getByRole('link', { name: title }).first()).toBeVisible()
})

test('Dialogs close with Escape and give focus back', async ({ page }, info) => {
  test.skip(info.project.use.hasTouch === true, 'Keyboard check is for desktop')
  await page.goto('/suppliers')
  await settled(page)
  const add = page.getByRole('button', { name: 'Add supplier' })
  await add.click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await expectAccessible(page)
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(add).toBeFocused()
})
