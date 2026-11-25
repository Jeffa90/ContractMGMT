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

test('Drag an Excel file onto the import box', async ({ page, api }, info) => {
  test.skip(info.project.use.hasTouch === true, 'Dragging files is for desktop')
  // An export is a ready-made import file.
  const workbook = await (await api.get('/api/excel/suppliers')).body()
  await page.goto('/suppliers')
  await settled(page)
  await page.getByRole('button', { name: 'Import from Excel' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText('Drag an Excel workbook (.xlsx) here, or')).toBeVisible()

  const drop = async (name: string, bytes: number[], type: string) => {
    const transfer = await page.evaluateHandle(
      ({ name, bytes, type }) => {
        const t = new DataTransfer()
        t.items.add(new File([new Uint8Array(bytes)], name, { type }))
        return t
      },
      { name, bytes, type },
    )
    const box = dialog.getByText(/Drag an Excel workbook|Drop it here/).locator('..')
    await box.dispatchEvent('dragenter', { dataTransfer: transfer })
    await box.dispatchEvent('drop', { dataTransfer: transfer })
  }

  // The wrong kind of file is turned away, plainly.
  await drop('notes.pdf', [37, 80, 68, 70], 'application/pdf')
  await expect(dialog.getByRole('alert')).toHaveText('notes.pdf isn\'t an Excel workbook (.xlsx). Choose an Excel workbook (.xlsx).')

  // The right one is checked straight away, as if chosen with the button.
  await drop('suppliers.xlsx', [...workbook], 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')
  await expect(dialog.getByText('suppliers.xlsx', { exact: false }).first()).toBeVisible()
  await expect(dialog.getByText('No change')).toBeVisible()
  await expectAccessible(page)
})

test('Drag documents onto a contract', async ({ page, api }, info) => {
  test.skip(info.project.use.hasTouch === true, 'Dragging files is for desktop')
  type Row = { id: number; stage: string; ownerName: string }
  const drafts: Row[] = await (await api.get('/api/contracts?view=drafts')).json()
  const draft = drafts.find((d) => d.stage === 'Draft')
  test.skip(!draft, 'No draft to add documents to')
  await page.goto(`/contracts/${draft!.id}`)
  await settled(page)
  const card = page.getByRole('heading', { name: 'Documents', exact: true }).locator('xpath=ancestor::div[contains(@class,"relative")][1]')
  const name = `Dropped ${info.project.name} ${Date.now()}.pdf`
  const transfer = await page.evaluateHandle((name) => {
    const t = new DataTransfer()
    t.items.add(new File(['%PDF-1.4 dropped'], name, { type: 'application/pdf' }))
    return t
  }, name)
  await card.dispatchEvent('dragenter', { dataTransfer: transfer })
  await expect(page.getByText(/Drop to add as/)).toBeVisible()
  await card.dispatchEvent('drop', { dataTransfer: transfer })
  await expect(page.getByText(name.replace('.pdf', '')).first()).toBeVisible()
})

test('Back goes to where you were, not up a level', async ({ page }) => {
  // Renewals › a contract › Back: Renewals again (not the Contracts list).
  await page.goto('/renewals')
  await settled(page)
  await page.locator('main a[href^="/contracts/"]').first().click()
  await settled(page)
  await page.getByRole('button', { name: 'Back', exact: true }).click()
  await expect(page).toHaveURL(/\/renewals$/)
  // Opened straight from a link (nothing to go back to): the page above.
  await page.goto('/suppliers')
  await settled(page)
  const href = await page.locator('main a[href^="/suppliers/"]').first().getAttribute('href')
  await page.goto(href!)
  await settled(page)
  await page.getByRole('button', { name: 'Back', exact: true }).click()
  await expect(page).toHaveURL(/\/suppliers$/)
})

test('Send feedback from any screen and find it on the Feedback page', async ({ page }, info) => {
  const words = `E2E feedback ${info.project.name} ${Date.now()}`
  // Bottom right on a computer; on a phone, on the Feedback page.
  const phone = info.project.use.hasTouch === true
  await page.goto(phone ? '/feedback' : '/suppliers')
  await settled(page)
  await page.getByRole('button', { name: 'Feedback', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText(phone ? 'From: Feedback (/feedback)' : 'From: Suppliers (/suppliers)')).toBeVisible()
  await dialog.getByLabel('What kind of feedback?').selectOption('Improvement')
  await dialog.getByLabel(/Tell us what happened/).fill(words)
  await expectAccessible(page)
  await dialog.getByRole('button', { name: 'Send feedback' }).click()
  await expect(page.getByText('Thanks, your feedback has been recorded.')).toBeVisible()
  await page.goto('/feedback')
  await settled(page)
  const item = page.getByText(words)
  await expect(item).toBeVisible()
  await item.click()
  await expect(page.getByLabel('Reply')).toBeVisible()
  await expectAccessible(page)
})
