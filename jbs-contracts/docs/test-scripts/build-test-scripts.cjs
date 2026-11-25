// Builds the user-testing scripts for JBS Contracts as Word documents, one per role (Jeff, 26 Nov).
// Run from this folder with the docx npm package available:  node build-test-scripts.cjs
// Short and plain (Jeff, 26 Nov): each person tests their own role on practice contracts; each section points at the in-app help.
// Everything on QA is made up; never put real contract data in QA.
const fs = require('fs')
const path = require('path')
const {
  Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell, WidthType, ShadingType, BorderStyle, AlignmentType,
  LevelFormat, HeadingLevel, PageBreak, Footer, PageNumber, TableLayoutType, VerticalAlign,
} = require('docx')

const QA = 'https://jbs-contracts-qa.jbssa.com.au'
const NAVY = '232378'
const FONT = 'Arial'
const PAGE_W = 11906 // A4
const MARGIN = 1080 // 0.75 inch
const CONTENT = PAGE_W - 2 * MARGIN // 9746
const COLS = [520, 4260, 3466, 1500] // #, Do this, You should see, Result
const border = { style: BorderStyle.SINGLE, size: 4, color: 'C9CDDC' }
const borders = { top: border, bottom: border, left: border, right: border }

// ---------------------------------------------------------------- building blocks

/** Text with **bold** parts. */
function runs(text, opts = {}) {
  return text.split(/(\*\*[^*]+\*\*)/).filter(Boolean).map((part) =>
    part.startsWith('**') ? new TextRun({ text: part.slice(2, -2), bold: true, font: FONT, ...opts }) : new TextRun({ text: part, font: FONT, ...opts }))
}
const p = (text, opts = {}) => new Paragraph({ children: runs(text, opts.run), spacing: { after: 120 }, ...opts.para })
const h1 = (text) => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun({ text, font: FONT })] })
const h2 = (text) => new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun({ text, font: FONT })] })
const bullet = (text) => new Paragraph({ numbering: { reference: 'bullets', level: 0 }, children: runs(text), spacing: { after: 60 } })
const pageBreak = () => new Paragraph({ children: [new PageBreak()] })

function cell(children, width, opts = {}) {
  return new TableCell({
    width: { size: width, type: WidthType.DXA },
    borders,
    margins: { top: 80, bottom: 80, left: 110, right: 110 },
    verticalAlign: VerticalAlign.TOP,
    shading: opts.fill ? { fill: opts.fill, type: ShadingType.CLEAR, color: 'auto' } : undefined,
    columnSpan: opts.span,
    children: Array.isArray(children) ? children : [children],
  })
}
const cellText = (text, opts = {}) => new Paragraph({ children: runs(text, opts.run), spacing: { after: 0 } })

/** A box of shaded text across the page (notes, tips). */
function box(lines, fill = 'EEF0F7') {
  return new Table({
    width: { size: CONTENT, type: WidthType.DXA },
    columnWidths: [CONTENT],
    layout: TableLayoutType.FIXED,
    rows: [new TableRow({ children: [cell(lines.map((l) => new Paragraph({ children: runs(l), spacing: { after: 80 } })), CONTENT, { fill })] })],
  })
}

/** Empty lines to write on. */
function notes(label = 'Notes on this section (what was confusing, what went wrong, ideas):', lines = 3) {
  return new Table({
    width: { size: CONTENT, type: WidthType.DXA },
    columnWidths: [CONTENT],
    layout: TableLayoutType.FIXED,
    rows: [
      new TableRow({ children: [cell([cellText(label, { run: { color: '5B6280', size: 18 } })], CONTENT)] }),
      ...Array.from({ length: lines }, () => new TableRow({ height: { value: 420, rule: 'atLeast' }, children: [cell([cellText('')], CONTENT)] })),
    ],
  })
}

/**
 * The steps of a section. Each step is [do, see], or { handover: text } / { wait: text } for the moments people pass work between roles.
 */
function steps(list) {
  const header = new TableRow({
    tableHeader: true,
    children: ['#', 'Do this', 'You should see', 'Result'].map((t, i) =>
      cell([cellText(`**${t}**`, { run: { color: 'FFFFFF' } })], COLS[i], { fill: NAVY })),
  })
  let n = 0
  const rows = list.map((s) => {
    if (s.handover || s.wait) {
      const fill = s.handover ? 'FFF4D6' : 'E3F2FD'
      const label = s.handover ? 'HAND-OVER' : 'WAIT'
      return new TableRow({ cantSplit: true, children: [cell([cellText(`**${label}:** ${s.handover ?? s.wait}`)], CONTENT, { fill, span: 4 })] })
    }
    n += 1
    return new TableRow({
      cantSplit: true,
      children: [
        cell([cellText(String(n))], COLS[0]),
        cell([cellText(s[0])], COLS[1]),
        cell([cellText(s[1])], COLS[2]),
        cell([cellText('☐ Works'), cellText('☐ Problem')], COLS[3]),
      ],
    })
  })
  return new Table({ width: { size: CONTENT, type: WidthType.DXA }, columnWidths: COLS, layout: TableLayoutType.FIXED, rows: [header, ...rows] })
}

/** One section: heading, why, where to learn it, the steps and a notes box. */
function section(number, title, why, learn, list) {
  return [
    h1(`${number}. ${title}`),
    p(why),
    p(`**Help:** ${learn}`, { run: { color: '1F4E8C' } }),
    steps(list),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    notes('Notes:', 1),
    new Paragraph({ spacing: { after: 200 }, children: [] }),
  ]
}

/** A little table of name fields. */
function fillIn(rows) {
  const w = [3400, CONTENT - 3400]
  return new Table({
    width: { size: CONTENT, type: WidthType.DXA },
    columnWidths: w,
    layout: TableLayoutType.FIXED,
    rows: rows.map((r) => new TableRow({ height: { value: 460, rule: 'atLeast' }, children: [cell([cellText(`**${r}**`)], w[0], { fill: 'F7F8FC' }), cell([cellText('')], w[1])] })),
  })
}

function intro(role) {
  return [
    new Paragraph({ children: [new TextRun({ text: 'JBS Contracts', font: FONT, size: 28, color: NAVY, bold: true })], spacing: { after: 40 } }),
    new Paragraph({ heading: HeadingLevel.TITLE, children: [new TextRun({ text: `Testing: ${role.title}`, font: FONT })] }),
    p(role.who, { run: { color: '5B6280' } }),
    bullet(`Open ${QA} (it signs you in automatically).`),
    bullet('There\'s no training: if you\'re not sure how to do something, click the **?** next to the page title, or **Help** in the menu.'),
    bullet('Tick **Works** or **Problem** for each step. Jot down anything confusing.'),
    bullet('It\'s all made-up test data, so you can\'t break anything. Use the practice contracts marked **Demo (no emails)**.'),
    bullet(`Takes about ${role.time}.`),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    fillIn(['Your name', 'Date']),
  ]
}

function feedback() {
  const qs = [
    'Easy to find your way around?',
    'Was the ? help enough?',
    'Would you be happy using it for real?',
  ]
  const w = [CONTENT - 2600, 2600]
  return [
    pageBreak(),
    h1('Last: how was it?'),
    p('Circle one (1 = not at all, 5 = definitely).'),
    new Table({
      width: { size: CONTENT, type: WidthType.DXA },
      columnWidths: w,
      layout: TableLayoutType.FIXED,
      rows: qs.map((q) => new TableRow({ children: [cell([cellText(q)], w[0]), cell([cellText('1    2    3    4    5')], w[1])] })),
    }),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    notes('Anything else? What you liked, what bugged you, what\'s missing:', 4),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    p('Send this back when you\'re done. Thanks!'),
  ]
}

function build(role, file) {
  const children = [...intro(role), new Paragraph({ spacing: { after: 200 }, children: [] }), ...role.sections.flatMap((s, i) => section(i + 1, ...s)), ...feedback()]
  const doc = new Document({
    creator: 'JBS Contracts',
    title: `JBS Contracts testing: ${role.title}`,
    styles: {
      default: { document: { run: { font: FONT, size: 21 } } },
      paragraphStyles: [
        { id: 'Title', name: 'Title', basedOn: 'Normal', next: 'Normal', run: { font: FONT, size: 44, bold: true, color: NAVY }, paragraph: { spacing: { before: 0, after: 120 }, keepNext: true } },
        { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true, run: { font: FONT, size: 32, bold: true, color: NAVY }, paragraph: { spacing: { before: 120, after: 120 }, keepNext: true, outlineLevel: 0 } },
        { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true, run: { font: FONT, size: 26, bold: true, color: NAVY }, paragraph: { spacing: { before: 240, after: 100 }, keepNext: true, outlineLevel: 1 } },
      ],
    },
    numbering: { config: [{ reference: 'bullets', levels: [{ level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 300 } } } }] }] },
    sections: [{
      properties: { page: { size: { width: PAGE_W, height: 16838 }, margin: { top: MARGIN, bottom: MARGIN, left: MARGIN, right: MARGIN } } },
      footers: {
        default: new Footer({
          children: [new Paragraph({
            alignment: AlignmentType.RIGHT,
            children: [new TextRun({ text: `JBS Contracts testing: ${role.title}   ·   Page `, font: FONT, size: 16, color: '5B6280' }),
              new TextRun({ children: [PageNumber.CURRENT], font: FONT, size: 16, color: '5B6280' })],
          })],
        }),
      },
      children,
    }],
  })
  return Packer.toBuffer(doc).then((buf) => fs.writeFileSync(path.join(__dirname, file), buf))
}

// ---------------------------------------------------------------- the scripts (each person tests their own role, on practice contracts)

const owner = {
  title: 'Contract owner and Procurement',
  who: 'Raising and looking after contracts, and looking after suppliers.',
  time: '45 minutes',
  sections: [
    ['Get started', 'Home is your to-do list.', 'Help › "Find your way around".', [
      ['Look at **Home** and click a tile.', 'The contracts behind it.'],
      ['Click the **?** next to the greeting.', 'Help for that page.'],
      ['Click **Search** and type "Coastal".', 'Matching contracts and suppliers.'],
      ['Click your name (bottom left) › **Email reminders** and pick one.', 'Saved.'],
    ]],
    ['Raise a contract', 'Start a new contract.', 'Help › "Raise a contract".', [
      ['**Contracts** › **Add contract**. Give it a title and add a new made-up supplier.', 'The supplier is filled in for you.'],
      ['Fill in a category, value and dates, then **Save draft**.', 'It gets a reference like JBSAUS-0123.'],
      ['Drag any file onto the **Documents** box.', 'The file is added.'],
    ]],
    ['Work with Legal', 'Send contracts to Legal and get them back.', 'Help › "Send a contract to Legal".', [
      ['On your new contract, click **Send to Legal** with a short note.', 'It shows it\'s Legal\'s turn.'],
      ['On Home, open a demo contract under **Back from Legal**.', 'Legal\'s note and any mark-up.'],
      ['Click **Send to Legal** again with a reply.', 'It\'s back with Legal.'],
    ]],
    ['Get it approved', 'Once signed, it goes to an approver.', 'Help › "Send a signed contract for approval".', [
      ['Raise another quick contract, untick **Needs Legal review**, save, then click **Ready for signing**.', 'It moves to Signing.'],
      ['Click **Send for approval**. Attach any PDF as the signed copy and pick an effective date.', 'It moves to Approval.'],
    ]],
    ['Renewals', 'Decide what happens before the notice date.', 'Help › "Record a renewal decision".', [
      ['Open **Renewals**.', 'Most urgent at the top.'],
      ['On a demo contract, **Record decision** › **Let it end**.', 'The decision shows.'],
      ['On another, choose **Renew**.', 'A new renewal draft opens.'],
    ]],
    ['Change a live contract', 'Live (Effective) contracts change through a Variation.', 'Help › "Change an Effective contract with a Variation".', [
      ['Open an Effective demo contract › **Start a Variation**, give a reason.', 'The Variation page opens.'],
      ['Change the end date › **Save changes**.', 'Old and new side by side.'],
      ['Send it to Legal or for approval.', 'It shows whose turn it is.'],
    ]],
    ['Suppliers', 'Everything about a supplier in one place.', 'Help › "Set up a supplier\'s details" and "Add a supplier document".', [
      ['**Suppliers** › open one › **Edit**. Make yourself relationship owner, set a risk and reason.', 'Saved, with a risk badge.'],
      ['**Documents** › **Add document**. Pick a kind, drag a file in, set an expiry date, tick **Remind me**.', 'Listed with its expiry date.'],
      ['**Business reviews** › **Add business review**, with one action for you.', 'The action shows on your Home.'],
      ['Tick the action done.', 'It leaves your Home.'],
    ]],
    ['Find things', 'Get to any contract fast.', 'Help › "Find a contract".', [
      ['**Contracts**: switch **My contracts** / **All**, then search.', 'The list narrows as you type.'],
      ['Click **Filters** and pick a site.', 'Only that site.'],
      ['Click **Export to Excel**.', 'An Excel file downloads.'],
    ]],
  ],
}

const legal = {
  title: 'Legal',
  who: 'Reviewing contracts and Variations.',
  time: '30 minutes',
  sections: [
    ['Get started', 'Home shows what\'s waiting for Legal.', 'Help › "Find your way around".', [
      ['Look at **Home** and click **New to pick up**.', 'Contracts waiting for Legal.'],
      ['Click the **?** next to the greeting.', 'Help for that page.'],
      ['Click your name (bottom left) › **Email reminders** and pick one.', 'Saved.'],
    ]],
    ['Review a contract', 'Pick it up, go back and forth with the owner, clear it.', 'Help › "Review a contract (Legal)".', [
      ['Open a demo contract from **New to pick up** › **I\'ll take it**.', 'Your name shows as the Legal person.'],
      ['**Update status** › **Waiting on supplier**, with a note.', 'The status shows on the contract.'],
      ['**Send to owner** with a question, and attach any file.', 'It moves to Waiting on the owner.'],
      ['Open one from **Your turn (Legal)** › **Legal cleared**, with a short outcome.', 'It moves to Signing.'],
    ]],
    ['Review a Variation', 'Changes to live contracts come to Legal too.', 'The ? on the Variation page.', [
      ['Open one from **Variations for Legal** and take it.', 'Old and new values side by side.'],
      ['Click **Legal cleared** with an outcome.', 'It goes back to the owner.'],
    ]],
    ['Find things', 'Get to anything fast.', 'Help › "Find a contract".', [
      ['**Contracts** › **With Legal**.', 'Everything with Legal and whose turn it is.'],
      ['Press **Ctrl+K** and type a reference.', 'Jump straight to it.'],
    ]],
  ],
}

const approver = {
  title: 'Approver and Group Head',
  who: 'Approving contracts and Variations, and (Group Head) seeing the team\'s workload.',
  time: '20 minutes',
  sections: [
    ['Get started', 'Home shows what\'s waiting for you.', 'Help › "Approve a contract".', [
      ['Look at **Home** and click **To approve**.', 'Contracts waiting for approval.'],
      ['Click your name (bottom left) › **Email reminders** and pick one.', 'Saved.'],
    ]],
    ['Approve contracts', 'Check the signed copy matches, then approve.', 'Help › "Approve a contract".', [
      ['Open a demo contract and open its signed copy.', 'The details and the file.'],
      ['Click **Send back** with a reason.', 'It goes back to the owner.'],
      ['Open another › **Approve and register**.', 'It becomes Effective and locked.'],
      ['Optional: try it on your phone.', 'Easy to use on a small screen.'],
    ]],
    ['Approve a Variation', 'Changes to live contracts need approval too.', 'The ? on the Variation page.', [
      ['Open one from **Variations to approve**.', 'Old and new values side by side.'],
      ['Click **Approve**.', 'The contract shows the new values.'],
    ]],
    ['Team (Group Head only)', 'See who has what coming up.', 'Help › "See what your team has coming up".', [
      ['Open **Team** and try 30, 90 and 180 days.', 'Each person\'s overdue and upcoming work.'],
      ['Click a person, then a contract.', 'Their contracts, then the contract itself.'],
    ]],
  ],
}

const admin = {
  title: 'Admin',
  who: 'People, lists, fields, emails and reminders, reports and importing old contracts.',
  time: '45 minutes',
  sections: [
    ['Set up the testers (do this first)', 'Give testers access and practice data.', 'Help › "Give someone access".', [
      ['**People** › **Add person** (or open one). Tick their roles.', 'Saved. New people get a welcome email.'],
      ['Click your name › **Refresh demo data (QA)**.', 'Everyone now has practice contracts.'],
      ['Your name › **Try as someone else (QA)** › pick a tester. Then stop from the banner.', 'You see their Home, then yours again.'],
    ]],
    ['Lists', 'The choices in drop-downs.', 'The ? on Lists.', [
      ['**Lists** › **Contract document kinds** › add "Test kind".', 'Added.'],
      ['Open a contract and add a document.', '"Test kind" is in the Kind list.'],
      ['Rename it, then **Hide** it.', 'Renamed, then gone from the list.'],
    ]],
    ['Contract fields', 'Ask for something new on every contract.', 'Help › "Add a contract field".', [
      ['**Contract fields** › **Add field** › "Test field" › **Save changes**.', 'It appears on Add contract.'],
      ['Hide it and save.', 'It\'s gone again.'],
    ]],
    ['Emails and reminders', 'When reminders go and how they come.', 'Help › "Change a reminder or email".', [
      ['Read **How reminder emails come** and try the three options.', 'Each one saves.'],
      ['Open a reminder, untick **Send this reminder**, then tick it again.', 'Shows Off, then on again.'],
      ['Change a word in an email and check the preview.', 'The preview updates. (Put it back.)'],
    ]],
    ['Fix a mistake on a locked contract', 'For typos a Variation can\'t fix.', 'Help › "Fix a mistake on a locked contract".', [
      ['Open an Effective demo contract › **Emergency unlock** for 1 hour, with a reason.', 'A banner shows it\'s unlocked.'],
      ['**Edit details**, change something, save. Then **Lock it again now**.', 'Saved, then locked again.'],
    ]],
    ['Reports', 'The register and downloads.', 'Help › "Print or download the register".', [
      ['**Reports** › **Register** › **Columns**: add a column.', 'The column appears.'],
      ['**Download Excel** and **Print or save as PDF**.', 'Both work.'],
      ['Open **Audit trail**.', 'It says the trail is intact.'],
    ]],
    ['Import old contracts', 'Bring in contracts from the old system.', 'Help › "Import old contracts and their files".', [
      ['Make a small Excel sheet: Agreement No, Agreement Name, Vendor, Category, Contract Owner, Start Date, Expiry Date, Status, with 3 made-up rows.', 'Ready to import.'],
      ['**Import old contracts**: drag it in › **Check** › **Import**.', 'Rows go in, or say why they can\'t.'],
      ['Zip a couple of files named like "TEST-001 signed.pdf" and drag the ZIP in › **Add files**.', 'Files land on the right contract.'],
    ]],
  ],
}

Promise.all([
  build(owner, 'JBS Contracts testing - Contract owner and Procurement.docx'),
  build(legal, 'JBS Contracts testing - Legal.docx'),
  build(approver, 'JBS Contracts testing - Approver and Group Head.docx'),
  build(admin, 'JBS Contracts testing - Admin.docx'),
]).then(() => console.log('Built 4 testing documents.'))
