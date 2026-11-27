// Records the help centre's short silent videos (D-89), with the guide's steps as captions.
//
// Run it against a LOCAL copy of JBS Contracts with only the fictional demo data loaded (never QA or Production data):
// a fresh database, a made-up admin (for example DEMO\amorgan, "Alex Morgan (demo)"), header sign-in, then Load demo data.
// Restore that starting point before each run, because the videos change things (they raise, send and approve contracts).
//
//   BASE=http://localhost:5090 node help-videos/record.cjs            (every video)
//   BASE=http://localhost:5090 node help-videos/record.cjs approve    (just one)
//
// Needs Playwright (Chromium) and ffmpeg. Writes public/help-videos/<guide>.mp4, .vtt (captions) and .jpg (poster).
const { chromium } = require('playwright')
const fs = require('node:fs')
const path = require('node:path')
const os = require('node:os')
const { execFileSync } = require('node:child_process')
const { guides } = require('../src/helpcentre/guides.json')

const BASE = process.env.BASE || 'http://localhost:5090'
const DOMAIN = process.env.DEMO_DOMAIN || 'DEMO'
const OUT = path.join(__dirname, '..', 'public', 'help-videos')
const WORK = fs.mkdtempSync(path.join(os.tmpdir(), 'help-videos-'))
const who = { admin: 'amorgan', legal: 'demo.snguyen', approver: 'demo.gwallace' }
const wait = (ms) => new Promise((r) => setTimeout(r, ms))

// A visible pointer (screen recordings don't show the real one) that ripples on click.
const pointer = `(() => {
  const add = () => {
    if (document.getElementById('__pointer')) return
    const dot = document.createElement('div')
    dot.id = '__pointer'
    dot.style.cssText = 'position:fixed;left:-50px;top:-50px;width:22px;height:22px;margin:-11px 0 0 -11px;border-radius:50%;background:rgba(35,35,120,.28);border:2px solid #232378;z-index:2147483647;pointer-events:none;transition:transform .15s'
    document.documentElement.appendChild(dot)
    addEventListener('mousemove', (e) => { dot.style.left = e.clientX + 'px'; dot.style.top = e.clientY + 'px' }, true)
    addEventListener('mousedown', () => { dot.style.transform = 'scale(.6)' }, true)
    addEventListener('mouseup', () => { dot.style.transform = 'scale(1)' }, true)
  }
  if (document.readyState === 'loading') addEventListener('DOMContentLoaded', add); else add()
})()`

/** A small fictional PDF to upload. */
function pdf(name) {
  const file = path.join(WORK, name)
  fs.writeFileSync(file, '%PDF-1.4\n% Fictional demo document for the JBS Contracts help videos.\n%%EOF\n')
  return file
}

const plain = (t) => t.replace(/\*\*/g, '')
const stamp = (s) => {
  const ms = Math.max(0, Math.round(s * 1000))
  const h = String(Math.floor(ms / 3600000)).padStart(2, '0')
  const m = String(Math.floor((ms % 3600000) / 60000)).padStart(2, '0')
  const sec = String(Math.floor((ms % 60000) / 1000)).padStart(2, '0')
  return `${h}:${m}:${sec}.${String(ms % 1000).padStart(3, '0')}`
}

async function record(browser, id, { as = 'admin', start = '/', phone = false }, script) {
  const guide = guides.find((g) => g.id === id)
  if (!guide) throw new Error(`No guide called ${id}`)
  const size = phone ? { width: 390, height: 760 } : { width: 1280, height: 760 }
  const context = await browser.newContext({
    viewport: size,
    deviceScaleFactor: 1,
    colorScheme: 'light',
    locale: 'en-AU', // dates as JBS sees them (dd/mm/yyyy)
    timezoneId: 'Australia/Brisbane',
    recordVideo: { dir: WORK, size },
    extraHTTPHeaders: { 'X-Signed-In-As': `${DOMAIN}\\${who[as]}` },
  })
  await context.addInitScript(pointer)
  const page = await context.newPage()
  const t0 = Date.now()
  const now = () => (Date.now() - t0) / 1000
  await page.goto(BASE + start)
  await page.waitForLoadState('networkidle')
  await page.mouse.move(size.width / 2, size.height / 2)
  await wait(700)
  const begin = now()
  const marks = []

  const point = async (locator) => {
    await locator.scrollIntoViewIfNeeded()
    await wait(200)
    const box = await locator.boundingBox()
    if (box) await page.mouse.move(box.x + Math.min(box.width / 2, 60), box.y + box.height / 2, { steps: 20 })
    await wait(300)
  }
  const s = {
    page,
    step: async (n) => {
      marks[n - 1] = now() - begin
      await wait(500)
    },
    point: async (locator, ms = 900) => {
      await point(locator)
      await wait(ms)
    },
    click: async (locator, ms = 900) => {
      await point(locator)
      await locator.click()
      await wait(ms)
    },
    type: async (locator, text, ms = 500) => {
      await point(locator)
      await locator.click()
      await locator.pressSequentially(text, { delay: 40 })
      await wait(ms)
    },
    fill: async (locator, text, ms = 600) => {
      await point(locator)
      await locator.fill(text)
      await wait(ms)
    },
    select: async (locator, label, ms = 700) => {
      await point(locator)
      await locator.selectOption({ label })
      await wait(ms)
    },
    upload: async (button, file, ms = 1500) => {
      await point(button)
      const [chooser] = await Promise.all([page.waitForEvent('filechooser'), button.click()])
      await chooser.setFiles(file)
      await wait(ms)
    },
    wait,
  }

  try {
    await script(s)
    await wait(1800)
  } catch (e) {
    await page.screenshot({ path: path.join(WORK, `${id}-failed.png`) })
    await context.close()
    throw new Error(`${id}: ${e.message.split('\n')[0]} (screenshot in ${WORK})`)
  }
  const end = now() - begin
  if (marks.length !== guide.steps.length || marks.some((m) => m === undefined))
    throw new Error(`${id}: marked ${marks.filter((m) => m !== undefined).length} steps but the guide has ${guide.steps.length}`)
  await context.close()
  const raw = await page.video().path()

  fs.mkdirSync(OUT, { recursive: true })
  const mp4 = path.join(OUT, `${id}.mp4`)
  // H.264 plays in every browser JBS uses; screen recordings compress very small.
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-ss', begin.toFixed(2), '-i', raw, '-t', end.toFixed(2), '-c:v', 'libx264', '-crf', '30', '-preset', 'slow',
    '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-an', mp4])
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-ss', Math.max(0, end - 2).toFixed(2), '-i', mp4, '-frames:v', '1', '-q:v', '5', path.join(OUT, `${id}.jpg`)])
  const cues = guide.steps.map((step, i) => `${i + 1}\n${stamp(marks[i])} --> ${stamp(i + 1 < marks.length ? marks[i + 1] : end)}\n${i + 1}. ${plain(step)}\n`)
  fs.writeFileSync(path.join(OUT, `${id}.vtt`), `WEBVTT\n\n${cues.join('\n')}`)
  console.log(`${id}: ${end.toFixed(1)}s, ${(fs.statSync(mp4).size / 1024).toFixed(0)} KB`)
}

// ---------------------------------------------------------------- The videos, in an order that works from the demo data's starting point.

const videos = {
  'find-your-way': (b) =>
    record(b, 'find-your-way', { start: '/' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.getByRole('link', { name: /Back from Legal/ }), 1600)
      await s.step(2)
      await s.point(p.getByRole('heading', { name: 'Urgent' }), 600)
      await s.point(p.locator('main').getByRole('link', { name: 'Record decision' }).first(), 1400)
      await s.step(3)
      await s.click(p.getByRole('button', { name: /^Search/ }).first(), 500)
      await s.type(p.getByRole('dialog').getByRole('combobox').or(p.getByRole('dialog').getByRole('textbox')).first(), 'pest', 1800)
      await p.keyboard.press('Escape')
      await s.wait(500)
      await s.step(4)
      for (const name of ['Contracts', 'Renewals', 'Suppliers']) await s.point(p.getByRole('navigation').getByRole('link', { name, exact: true }), 700)
      await s.step(5)
      await s.click(p.getByRole('button', { name: 'Help: Your Home' }), 2200)
      await p.keyboard.press('Escape')
      await s.point(p.getByRole('navigation').getByRole('link', { name: 'Help', exact: true }), 1500)
    }),

  'raise-contract': (b) =>
    record(b, 'raise-contract', { start: '/contracts' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('link', { name: 'Add contract' }), 1200)
      await s.step(2)
      await s.select(p.getByLabel('Supplier'), 'Harbourside Cleaning (demo)')
      await s.select(p.getByLabel(/^Agreement type/), 'Services agreement')
      await s.select(p.getByLabel('Family', { exact: true }), 'Services')
      await s.select(p.getByLabel('Category', { exact: true }), 'Facilities')
      await s.select(p.getByLabel('Subcategory (optional)'), 'Cleaning')
      await s.step(3)
      await s.point(p.getByLabel(/^Title/), 2200)
      await s.step(4)
      await s.type(p.getByLabel('Value', { exact: true }), '96000')
      await s.fill(p.getByLabel('End date', { exact: true }), '2028-06-30')
      await s.type(p.getByLabel('Notice period (days)'), '60')
      await s.step(5)
      await s.point(p.getByLabel('Needs Legal review'), 1400)
      await s.step(6)
      await s.click(p.getByRole('button', { name: 'Save draft' }), 2500)
    }),

  'add-document': (b) =>
    record(b, 'add-document', { start: '/contracts/1' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.getByRole('heading', { name: 'Documents' }), 900)
      await s.step(2)
      await s.select(p.getByLabel('Kind'), 'Draft')
      await s.step(3)
      await s.upload(p.getByRole('button', { name: 'Add document' }), pdf('Office cleaning agreement.pdf'), 2200)
      await s.step(4)
      await s.upload(p.getByRole('button', { name: /Upload a new version of Office cleaning/ }), pdf('Office cleaning agreement v2.pdf'), 2500)
    }),

  'send-to-legal': (b) =>
    record(b, 'send-to-legal', { start: '/contracts/1' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('button', { name: 'Send to Legal', exact: true }), 900)
      const d = p.getByRole('dialog')
      await s.step(2)
      await s.type(d.getByLabel('Note for Legal'), 'The supplier wants a 60-day termination clause. Please check clause 9.')
      await s.step(3)
      await s.click(d.getByRole('button', { name: 'Send to Legal' }), 2200)
      await s.point(p.getByRole('heading', { name: /Legal conversation/ }), 1200)
      await s.step(4)
      await s.click(p.getByRole('navigation').getByRole('link', { name: 'Home', exact: true }), 1000)
      await s.point(p.getByRole('link', { name: /Back from Legal/ }), 1800)
    }),

  'legal-review': (b) =>
    record(b, 'legal-review', { as: 'legal', start: '/' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('link', { name: /New to pick up/ }), 1200)
      await s.click(p.locator('main').getByRole('link', { name: 'Office cleaning 2027' }), 1300)
      await s.step(2)
      await s.click(p.getByRole('button', { name: "I'll take it" }), 1500)
      await s.step(3)
      await s.click(p.getByRole('button', { name: 'Update status' }), 700)
      await s.click(p.getByRole('dialog').getByLabel('Reviewing'), 500)
      await s.click(p.getByRole('dialog').getByRole('button', { name: 'Save status' }), 1300)
      await s.step(4)
      await s.click(p.getByRole('button', { name: 'Send to owner' }), 700)
      await s.type(p.getByRole('dialog').getByLabel(/What's needed/), 'Please confirm the supplier accepts a 60-day termination clause.', 900)
      await s.click(p.getByRole('dialog').getByRole('button', { name: 'Cancel' }), 800)
      await s.step(5)
      await s.click(p.getByRole('button', { name: 'Legal cleared' }), 700)
      await s.type(p.getByRole('dialog').getByLabel(/Outcome/), 'Cleared. 60-day termination clause agreed.')
      await s.click(p.getByRole('dialog').getByRole('button', { name: 'Clear it' }), 2200)
    }),

  'send-for-approval': (b) =>
    record(b, 'send-for-approval', { start: '/contracts/6' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.getByText('Signing', { exact: true }).first(), 1400)
      await s.step(2)
      await s.click(p.getByRole('button', { name: 'Send for approval', exact: true }), 900)
      const d = p.getByRole('dialog')
      await s.step(3)
      await s.upload(d.getByRole('button', { name: 'Choose the signed copy' }), pdf('Uniform supply 2027 signed.pdf'), 1000)
      await s.fill(d.getByLabel(/Effective date/), '2026-12-01', 900)
      await s.step(4)
      await s.click(d.getByRole('button', { name: 'Send for approval' }), 2500)
    }),

  approve: (b) =>
    record(b, 'approve', { as: 'approver', start: '/', phone: true }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('link', { name: /To approve/ }), 1200)
      await s.click(p.locator('main').getByRole('link', { name: 'Uniform supply 2027' }), 1200)
      await s.click(p.getByRole('link', { name: 'Review and approve' }), 1300)
      await s.step(2)
      await s.point(p.getByRole('heading', { name: 'Details' }), 900)
      await s.point(p.getByRole('link', { name: /signed\.pdf/ }), 1400)
      await s.step(3)
      await s.click(p.getByRole('button', { name: 'Send back' }), 900)
      await s.point(p.getByRole('dialog').getByRole('textbox').first(), 1000)
      await s.click(p.getByRole('dialog').getByRole('button', { name: 'Cancel' }), 800)
      await s.step(4)
      await s.click(p.getByRole('button', { name: 'Approve and register' }), 1200)
      await s.click(p.getByRole('dialog').getByRole('button', { name: 'Approve and register' }), 1500)
      await s.step(5)
      await s.point(p.getByRole('button', { name: 'Undo' }), 2500)
    }),

  'renewal-decision': (b) =>
    record(b, 'renewal-decision', { start: '/renewals' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.getByRole('heading', { name: /Overdue/ }), 1300)
      await s.step(2)
      await s.click(p.locator('main').getByRole('link', { name: 'Record decision' }).first(), 1500)
      const d = p.getByRole('dialog')
      await s.step(3)
      await s.click(d.getByText('Let it end', { exact: true }), 700)
      await s.type(d.getByLabel(/Note/), 'Moving to a new security provider from next year.')
      await s.step(4)
      await s.click(d.getByRole('button', { name: 'Save decision' }), 2300)
    }),

  variation: (b) =>
    record(b, 'variation', { start: '/contracts/9' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('button', { name: 'Start a Variation' }), 900)
      await s.step(2)
      await s.type(p.getByRole('dialog').getByLabel(/Why is it changing/), 'The supplier agreed a 12-month extension at the same rates.')
      await s.click(p.getByRole('dialog').getByRole('button', { name: 'Start' }), 1800)
      await s.step(3)
      await s.fill(p.getByLabel('New end date'), '2028-06-30', 900)
      await s.click(p.getByRole('button', { name: 'Save changes' }), 1800)
      await s.step(4)
      await s.point(p.getByRole('button', { name: /Send to Legal|Send for approval/ }).first(), 2000)
    }),

  'hand-over': (b) =>
    record(b, 'hand-over', { start: '/contracts/13' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('button', { name: 'Hand over' }), 900)
      const d = p.getByRole('dialog')
      await s.step(2)
      await s.select(d.getByLabel(/Owner/), 'Priya Patel (demo)')
      await s.select(d.getByLabel('Backup'), 'Alex Morgan (demo)')
      await s.step(3)
      await s.click(d.getByRole('button', { name: 'Hand over' }), 2300)
    }),

  'supplier-details': (b) =>
    record(b, 'supplier-details', { start: '/suppliers' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('link', { name: 'Southern Cross Freight (demo)' }), 1200)
      await s.step(2)
      await s.click(p.getByRole('button', { name: 'Edit', exact: true }), 800)
      const d = p.getByRole('dialog')
      await s.select(d.getByLabel('Relationship owner'), 'Priya Patel (demo)')
      await s.select(d.getByLabel('Tier'), 'Strategic')
      await s.step(3)
      await s.click(d.getByRole('radio', { name: 'Medium' }), 500)
      await s.type(d.getByLabel(/Why/), 'Main linehaul carrier; few alternatives.')
      await s.fill(d.getByLabel('Next business review due'), '2027-02-10')
      await s.click(d.getByRole('button', { name: 'Save details' }), 1500)
      await s.step(4)
      await s.click(p.getByRole('button', { name: 'Add contact' }), 700)
      const c = p.getByRole('dialog')
      await s.type(c.getByLabel(/Name/), 'Dana Brooks')
      await s.type(c.getByLabel('Role'), 'Account manager')
      await s.type(c.getByLabel('Email'), 'dana.brooks@example.com')
      await s.click(c.getByRole('button', { name: 'Add contact' }), 2000)
    }),

  'supplier-documents': (b) =>
    record(b, 'supplier-documents', { start: '/suppliers/1' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('radio', { name: /Documents/ }), 800)
      await s.click(p.getByRole('button', { name: 'Add document' }), 800)
      const d = p.getByRole('dialog')
      await s.step(2)
      await s.select(d.getByLabel(/Kind/), 'Insurance certificate')
      await s.upload(d.getByRole('button', { name: 'Choose file' }), pdf('Public liability 2026.pdf'), 700)
      await s.fill(d.getByLabel('Expires'), '2027-01-20')
      await s.click(d.getByRole('button', { name: 'Upload' }), 1600)
      await s.step(3)
      await s.click(p.getByLabel('Remind me before it expires'), 1600)
      await s.step(4)
      await s.click(p.getByRole('button', { name: /New version of Public liability/ }), 800)
      const v = p.getByRole('dialog')
      await s.upload(v.getByRole('button', { name: 'Choose file' }), pdf('Public liability 2027.pdf'), 700)
      await s.fill(v.getByLabel('New expiry date'), '2028-01-20')
      await s.click(v.getByRole('button', { name: 'Upload' }), 2000)
    }),

  'business-review': (b) =>
    record(b, 'business-review', { start: '/suppliers/1?tab=reviews' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('button', { name: 'Add business review' }), 800)
      const d = p.getByRole('dialog')
      await s.step(2)
      await s.select(d.getByLabel('JBS people who attended'), 'Priya Patel (demo)')
      await s.click(d.getByRole('button', { name: 'Add new contact' }), 500)
      await s.type(d.getByLabel(/^Name/), 'Dana Brooks')
      await s.type(d.getByLabel(/^Role/), 'Account manager')
      await s.click(d.getByRole('button', { name: 'Save contact' }), 1200)
      await s.type(d.getByLabel(/Summary/), 'On-time delivery is back to 96%. Fuel levy stays until June.')
      await s.step(3)
      await s.click(d.getByRole('radio', { name: 'Good' }), 400)
      for (const [label, n] of [['Quality', '4'], ['Delivery', '4'], ['Service', '5'], ['Cost', '3']])
        await s.click(d.getByRole('radiogroup', { name: label }).getByRole('radio', { name: n, exact: true }), 300)
      await s.step(4)
      await s.click(d.getByRole('button', { name: 'Add an action' }), 500)
      await s.type(d.getByLabel(/Action 1/), 'Send the revised Christmas delivery schedule')
      await s.select(d.getByLabel(/^Who/), 'Priya Patel (demo)')
      await s.fill(d.getByLabel(/^Due/), '2026-12-05')
      await s.click(d.getByRole('button', { name: 'Add an action' }), 500)
      await s.type(d.getByLabel(/Action 2/), 'Confirm the new delivery windows in writing')
      await s.select(d.getByLabel(/^Who/).nth(1), 'Dana Brooks (Account manager)')
      await s.fill(d.getByLabel(/^Due/).nth(1), '2026-12-12')
      await s.fill(d.getByLabel('Next business review due'), '2027-03-10')
      await s.step(5)
      await s.click(d.getByRole('button', { name: 'Add review' }), 1500)
      await s.upload(p.getByRole('button', { name: 'Attach a file' }).first(), pdf('Quarterly review deck.pdf'), 2200)
      await s.point(p.getByRole('link', { name: /^Word document of the review/ }).first(), 1500)
    }),

  'import-old-contracts': (b) =>
    record(b, 'import-old-contracts', { start: '/admin/import' }, async (s) => {
      const p = s.page
      // A small, fictional Medius-style export that matches the demo data, and a ZIP of its files (made with Python).
      const csv = path.join(WORK, 'medius-export.csv')
      fs.writeFileSync(csv, [
        'Agreement No,Agreement Name,Vendor,Category,Contract Owner,Start Date,Expiry Date,Notice Period,Status',
        'MED-1001,Linen hire,Harbourside Cleaning (demo),Facilities > Cleaning,Priya Patel (demo),1/07/2025,30/06/2027,60,Active',
        'MED-1002,Refrigerated trailers,Coastal Cold Storage (demo),Freight > Cold chain,Alex Morgan (demo),1/03/2023,28/02/2025,30,Expired',
        'MED-1003,Courier services,Swift Couriers (demo),Freight > Linehaul,Priya Patel (demo),1/01/2026,31/12/2027,30,Active',
        'MED-1004,Uniform laundering,Redgum Uniforms (demo),Facilities > Laundry,Alex Morgan (demo),1/02/2026,31/01/2028,30,Active',
      ].join('\r\n'))
      const zip = path.join(WORK, 'medius-files.zip')
      execFileSync('python3', ['-c', "import zipfile,sys\nz=zipfile.ZipFile(sys.argv[1],'w')\nz.writestr('MED-1001 Linen hire signed.pdf','%PDF-1.4 fictional')\nz.writestr('MED-1001 Linen hire draft v2.docx','fictional draft')\nz.writestr('MED-1001 Cover page - sign off.pdf','%PDF-1.4 fictional cover page')\nz.writestr('MED-1003/Signed/Courier agreement.pdf','%PDF-1.4 fictional courier')\nz.writestr('MED-1003/Variation 1 - rates.pdf','%PDF-1.4 fictional variation')\nz.close()", zip])
      await s.step(1)
      await s.upload(p.getByRole('button', { name: 'Choose the spreadsheet' }), csv, 1800)
      await s.step(2)
      await s.point(p.getByRole('list', { name: 'Needed fields' }), 1200)
      await s.point(p.getByLabel('Where Expiry Date goes'), 1200)
      await s.step(3)
      await s.click(p.getByRole('button', { name: /^Check/ }), 1800)
      await s.click(p.getByRole('radio', { name: /All rows/ }), 2200)
      await s.step(4)
      await s.click(p.getByRole('button', { name: /^Import \d+ contracts/ }), 900)
      await s.click(p.getByRole('dialog').getByRole('button', { name: /^Import \d+/ }), 2200)
      await s.step(5)
      await s.upload(p.getByRole('button', { name: 'Choose ZIPs' }), zip, 1800)
      await s.point(p.getByText('What the matched files are:'), 1600)
      await s.point(p.getByLabel('What MED-1001 Cover page - sign off.pdf is'), 1400)
      await s.click(p.getByRole('button', { name: /^Add \d+ files?/ }), 2200)
    }),

  register: (b) =>
    record(b, 'register', { start: '/reports' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.locator('main table'), 1400)
      await s.step(2)
      await s.click(p.getByRole('button', { name: 'Columns' }), 800)
      const d = p.getByRole('dialog')
      await s.click(d.getByLabel('Business unit', { exact: true }), 400)
      await s.click(d.getByLabel('Supplier SAP vendor number', { exact: true }), 600)
      await s.click(d.getByRole('button', { name: /^Show \d+ columns/ }), 1500)
      await s.step(3)
      await s.click(p.getByRole('button', { name: /Filters/ }), 700)
      await s.select(p.getByLabel('Business unit'), 'Beef', 1500)
      await s.step(4)
      await s.point(p.getByRole('button', { name: 'Print or save as PDF' }), 1200)
      await s.point(p.getByRole('link', { name: 'Download Excel' }), 1500)
    }),

  team: (b) =>
    record(b, 'team', { start: '/team' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.locator('main li button').first(), 1600)
      await s.step(2)
      await s.click(p.getByRole('radio', { name: '180 days' }), 1300)
      await s.step(3)
      await s.click(p.locator('main li button').first(), 2200)
      await s.step(4)
      await s.click(p.locator('main li li a').first(), 1800)
    }),

  'add-person': (b) =>
    record(b, 'add-person', { start: '/admin/people' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('button', { name: 'Add person' }), 800)
      const d = p.getByRole('dialog')
      await s.step(2)
      await s.type(d.getByLabel(/Windows logon/), 'tchen')
      await s.step(3)
      await s.type(d.getByLabel(/^Name/), 'Tara Chen (demo)')
      await s.type(d.getByLabel(/Work email/), 'tara.chen@example.com')
      await s.step(4)
      await s.click(d.getByLabel('Contract owner'), 500)
      await s.click(d.getByRole('button', { name: 'Add person' }), 2200)
    }),

  'add-field': (b) =>
    record(b, 'add-field', { start: '/admin/fields' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.click(p.getByRole('button', { name: 'Add field' }).nth(1), 900)
      const d = p.getByRole('dialog')
      await s.step(2)
      await s.type(d.getByLabel(/^Label/), 'Site contact')
      await s.select(d.getByLabel('Kind of field'), 'Short text')
      await s.type(d.getByLabel('Help'), 'Who to call at the site about this contract.')
      await s.step(3)
      await s.click(d.getByLabel('Offer as a column in the Contracts list'), 400)
      await s.click(d.getByText('Can be updated any time'), 900)
      await s.step(4)
      await s.click(d.getByRole('button', { name: 'Add field' }), 1200)
      await s.click(p.getByRole('button', { name: 'Save changes' }), 2200)
    }),

  'change-email': (b) =>
    record(b, 'change-email', { start: '/admin/reminders' }, async (s) => {
      const p = s.page
      await s.step(1)
      await s.point(p.getByRole('heading', { name: 'Reminders', exact: true }).first(), 1200)
      await s.step(2)
      await s.fill(p.getByLabel('Days before').first(), '90, 60, 30, 14', 700)
      await s.click(p.getByRole('button', { name: 'Save days' }).first(), 1300)
      await s.step(3)
      const email = p.getByLabel('Email', { exact: true }).first()
      await s.click(email, 300)
      await p.keyboard.press('Control+End')
      await p.keyboard.type('\n\nReference: ', { delay: 40 })
      await s.click(p.getByRole('button', { name: '{Reference}' }).first(), 1200)
      await s.step(4)
      await s.click(p.getByRole('button', { name: 'Save wording' }).first(), 2200)
    }),
}

;(async () => {
  const only = process.argv.slice(2)
  // Australian English all the way down, so date boxes read dd/mm/yyyy as they do at JBS.
  const browser = await chromium.launch({ args: ['--lang=en-AU'], env: { ...process.env, LANG: 'en_AU.UTF-8', LANGUAGE: 'en_AU' } })
  const failures = []
  for (const [id, run] of Object.entries(videos)) {
    if (only.length && !only.includes(id)) continue
    try {
      await run(browser)
    } catch (e) {
      failures.push(e.message)
      console.error(`FAILED ${e.message}`)
    }
  }
  await browser.close()
  const missing = guides.filter((g) => g.video && !videos[g.id]).map((g) => g.id)
  if (missing.length) console.error(`No recording script for: ${missing.join(', ')}`)
  if (failures.length || missing.length) process.exit(1)
})()
