// Builds the user-testing scripts for JBS Contracts as Word documents, one per role (Jeff, 26 Nov).
// Run from this folder with the docx npm package available:  node build-test-scripts.cjs
// Plain English, no training needed: each section points at the in-app help, and the steps say what to do and what you should see.
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
    p(`**Learn it in the app:** ${learn}`, { run: { color: '1F4E8C' } }),
    steps(list),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    notes(),
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
    new Paragraph({ heading: HeadingLevel.TITLE, children: [new TextRun({ text: `Test script: ${role.title}`, font: FONT })] }),
    p(role.who, { run: { color: '5B6280' } }),
    h2('How this works'),
    bullet(`Open **JBS Contracts on QA**: ${QA} (it signs you in with your Windows logon).`),
    bullet('**There is no training.** The app should teach you as you go. On every screen, click the **?** beside the page title for help, and open **Help** in the menu for short step-by-step guides, most with a silent video. Use them: whether they helped is part of the test.'),
    bullet('Work through the sections in order. Each step says what to do and what you should see. Tick **Works** or **Problem**.'),
    bullet('If something goes wrong, looks odd, or you can\'t work out what to do, tick **Problem** and write down what happened in the notes. A screenshot helps: press **Windows key + Shift + S**, then paste it into this document or an email.'),
    bullet('**Everything on QA is made up.** Never use real contracts, suppliers or people\'s details. Contracts marked **Demo (no emails)** are practice ones for everyone; do what you like with them.'),
    bullet('Some steps need another tester (a **HAND-OVER** or **WAIT** row). Agree who you\'re working with before you start, and message them (Teams is fine) when you hand something over.'),
    bullet(`It takes about ${role.time}. You don't have to do it in one go.`),
    h2('About you and who you\'re testing with'),
    fillIn(role.fillIn),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    box(role.tips),
  ]
}

function feedback() {
  const qs = [
    'How easy was it to find what you needed? (1 = very hard, 5 = very easy)',
    'How useful were the ? help and the Help guides? (1 = no help, 5 = all I needed)',
    'How confident would you be using it for real contracts? (1 = not at all, 5 = completely)',
    'How does it compare with how you manage contracts today? (1 = worse, 5 = much better)',
  ]
  const w = [CONTENT - 2600, 2600]
  return [
    pageBreak(),
    h1('Last: how did it feel?'),
    p('Circle a number for each, then tell us anything else. Honest answers help most.'),
    new Table({
      width: { size: CONTENT, type: WidthType.DXA },
      columnWidths: w,
      layout: TableLayoutType.FIXED,
      rows: qs.map((q) => new TableRow({ children: [cell([cellText(q)], w[0]), cell([cellText('1    2    3    4    5')], w[1])] })),
    }),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    notes('The best thing about it:', 2),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    notes('The thing that most needs fixing:', 2),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    notes('Anything you expected to be able to do but couldn\'t find:', 2),
    new Paragraph({ spacing: { after: 120 }, children: [] }),
    p('**When you\'re finished**, save this document and send it back to whoever gave it to you. Thank you!'),
  ]
}

function build(role, file) {
  const children = [...intro(role), ...role.sections.flatMap((s, i) => [pageBreak(), ...section(i + 1, ...s)]), ...feedback()]
  const doc = new Document({
    creator: 'JBS Contracts',
    title: `JBS Contracts test script: ${role.title}`,
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
            children: [new TextRun({ text: `JBS Contracts test script: ${role.title}   ·   Page `, font: FONT, size: 16, color: '5B6280' }),
              new TextRun({ children: [PageNumber.CURRENT], font: FONT, size: 16, color: '5B6280' })],
          })],
        }),
      },
      children,
    }],
  })
  return Packer.toBuffer(doc).then((buf) => fs.writeFileSync(path.join(__dirname, file), buf))
}

// ---------------------------------------------------------------- the shared journey
// One real (made-up) contract goes all the way round: Contract owner -> Legal -> owner -> Legal -> owner -> Approver -> owner -> Approver,
// then a Variation goes owner -> Legal -> owner -> Approver. Each script has its side of it, with the same step names.

const journeyNote = [
  '**The shared journey.** You and your partners take one made-up contract all the way round:',
  'Contract owner raises it  →  Legal reviews and sends it back with a question  →  owner answers  →  Legal clears it  →  owner sends it for approval  →  approver sends it back once  →  owner fixes it  →  approver approves it.',
  'Then the owner changes it with a Variation, which goes to Legal and the approver too. Each script has its side of the journey, so you\'ll wait for each other at the hand-overs.',
]

// ---------------------------------------------------------------- Contract owner and Procurement

const owner = {
  title: 'Contract owner and Procurement',
  who: 'For people who raise and look after contracts, and who look after suppliers (Procurement). You\'ll test both.',
  time: '90 minutes, plus waiting for your partners',
  fillIn: ['Your name', 'Date', 'Your Legal partner', 'Your Approver partner', 'Your test contract\'s reference (you\'ll get it in section 2): JBSAUS-'],
  tips: [...journeyNote, '**Before you start**, check you can see **Contracts**, **Renewals** and **Suppliers** in the menu. If you can\'t sign in, or you can\'t see them, tell your admin: they give you access and roles.'],
  sections: [
    ['Find your way around', 'Home is your to-do list. This section is about getting comfortable before you do anything.', 'Help centre › "Find your way around".', [
      ['Open JBS Contracts. Look at **Home**.', 'You\'re greeted by name. Tiles show what\'s waiting for you (for example **Your drafts**, **Back from Legal**). If there\'s nothing, ask your admin to refresh the demo data.'],
      ['Click the **?** beside "Good morning" (or afternoon).', 'A help panel explains Home. Close it.'],
      ['Click a tile, for example **Your drafts**.', 'The Contracts list opens, showing just those contracts.'],
      ['Go back to Home. Under **Urgent** (if shown), read one item.', 'It says what\'s due, when, and what to do about it.'],
      ['Click **Search** (or press Ctrl+K) and type part of a supplier\'s name, for example "Coastal".', 'Matching contracts and suppliers appear as you type. Click one to open it.'],
      ['Open **Help** in the menu. Play the video in **Find your way around**.', 'A short silent video with captions plays.'],
      ['Click your name at the bottom of the menu. Try **Dark**, then **Match my device**.', 'The colours change straight away and are remembered.'],
      ['In the same menu, look at **Email reminders**.', 'You can choose **As they happen**, **One email a day** or **One email a week**. One is marked (default). Choose the one you\'d like.'],
    ]],
    ['Raise a contract (the shared journey starts)', 'Raise the contract you\'ll take all the way round with your partners.', 'Help centre › "Raise a contract", and the ? on the Add contract screen.', [
      ['Go to **Contracts** and click **Add contract**.', 'A short form opens. It says only a title is needed to save a draft.'],
      ['Click **Save draft** without typing anything.', 'It doesn\'t save. A message under Title says what\'s missing.'],
      ['Type a made-up **Title**, for example "Test linehaul freight [your initials]". For **Supplier**, click **Add new supplier** and add a made-up one, for example "Test Freight Co [your initials]".', 'The new supplier is chosen for you.'],
      ['Choose a **Category**, a value (for example 250000), a start date next month, an end date two years later and a notice period of 60 days. Leave **Needs Legal review** ticked.', 'Fields accept the values. Help text under each field explains it.'],
      ['Click **Save draft**.', 'The contract page opens with a reference like JBSAUS-0123. **Write the reference in the box on page 1.** The progress bar shows Draft.'],
      ['Scroll to **Documents**. Choose Kind **Draft**, then drag any small PDF or Word file from your desktop onto the Documents card (or click **Add document**).', 'While you drag, the card says "Drop to add as Draft". The file appears with your name and the time.'],
      ['Click **Edit details**, change the value, and save.', 'The new value shows. **Activity** at the bottom lists what you changed.'],
    ]],
    ['Send it to Legal', 'Hand the contract to Legal and see whose turn it is.', 'Help centre › "Send a contract to Legal".', [
      ['On your contract, click **Send to Legal**. Add a note, for example "Please check the liability clause", and attach your draft.', 'A dialog lets you add a note and files (you can drag files in).'],
      ['Click **Send to Legal**.', 'The progress bar shows it\'s Legal\'s turn and for how long. The Legal conversation shows your note.'],
      { handover: 'Tell your **Legal partner** your contract reference. They\'ll pick it up and send it back to you with a question.' },
      { wait: 'until your Legal partner says they\'ve sent it back to you. Meanwhile, try sections 6 and 7 with the demo contracts.' },
      ['On Home, open **Back from Legal**.', 'Your contract is there. Its page shows Legal\'s note, any mark-up they attached, and that it\'s your turn.'],
      ['Open the mark-up Legal attached (if any).', 'It downloads and opens.'],
      ['Click **Send to Legal** again, with a note answering their question.', 'It\'s Legal\'s turn again. The conversation shows the back-and-forth in order.'],
      { handover: 'Tell your **Legal partner** you\'ve answered. They\'ll clear it.' },
    ]],
    ['Signing and approval', 'Once Legal clears it, it\'s signed (in DocuSign, as today) and goes to an approver to register.', 'Help centre › "Send a signed contract for approval".', [
      { wait: 'until your Legal partner says it\'s cleared.' },
      ['Open your contract.', 'It\'s in **Signing**. Legal\'s outcome is shown. A message says what to do next.'],
      ['Click **Send for approval**. For the **Signed copy**, drag in any PDF (pretend it\'s signed). Leave the **Effective date** empty and try to send.', 'It asks for the effective date. It\'s never filled in for you.'],
      ['Enter an **Effective date** and click **Send for approval**.', 'It moves to **Approval**. The signed copy is in Documents.'],
      { handover: 'Tell your **Approver partner** your contract reference. They\'ll send it back once, on purpose.' },
      { wait: 'until your Approver partner says they\'ve sent it back.' },
      ['Open your contract.', 'It\'s back in **Signing**, with the approver\'s reason shown clearly.'],
      ['Fix what they asked (for example add another document), then **Send for approval** again.', 'It\'s in Approval again. You don\'t need to attach the signed copy again.'],
      { handover: 'Tell your **Approver partner** it\'s ready again.' },
      { wait: 'until your Approver partner says they\'ve approved it.' },
      ['Open your contract.', 'It\'s **Effective** and shows a lock. **Edit details** is gone: it can only change through a Variation now.'],
      ['Look at **Activity**.', 'Every step is listed: who did what, and when.'],
    ]],
    ['Change it with a Variation (the shared journey continues)', 'An Effective contract only changes through an approved Variation.', 'Help centre › "Change an Effective contract with a Variation".', [
      ['On your Effective contract, click **Start a Variation**. Say why (for example "Extending by a year") and click **Start**.', 'The Variation page opens, showing the current value and the new value side by side.'],
      ['Change the **End date** to a year later and click **Save changes**.', 'Only the change is listed, old and new. The contract itself hasn\'t changed yet.'],
      ['Click **Send to Legal** with a short note.', 'It\'s Legal\'s turn on the Variation.'],
      { handover: 'Tell your **Legal partner** the Variation is with them.' },
      { wait: 'until Legal says the Variation is cleared.' },
      ['Open the Variation (from the contract, or **Variations to finish** on Home) and click **Send for approval**.', 'It\'s waiting for approval.'],
      { handover: 'Tell your **Approver partner** the Variation is waiting.' },
      { wait: 'until the approver says it\'s approved.' },
      ['Open your contract.', 'The end date is now the new one. The Variation is listed as Approved, with what changed.'],
    ]],
    ['Renewals and endings (practise on demo contracts)', 'Never miss a notice date: decide in good time whether to renew, let it end or renegotiate.', 'Help centre › "Record a renewal decision" and "Mark a review done".', [
      ['Go to **Renewals**.', 'Contracts with notice deadlines or end dates coming up, most urgent first. Overdue ones are at the top in red.'],
      ['On a demo contract, click **Record decision**, choose **Let it end** and add a note. Save.', 'It shows the decision. Its reminders stop. It will end on its end date.'],
      ['On another demo contract, record **Renew**.', 'A new draft "(renewal)" opens, linked to the old one, with the same details.'],
      ['Open a demo contract with a review date (an evergreen one) and click **Review done**. Set a next review date.', 'The review is recorded and reminders follow the new date.'],
      ['On a demo draft you don\'t need, scroll to the bottom and click **Withdraw this contract**, with a reason.', 'It moves to Closed. Nothing is deleted; its history stays.'],
      ['On one of your demo contracts, click **Hand over** and choose a **Backup** (another tester).', 'They\'re now the backup. It\'s recorded in Activity.'],
    ]],
    ['Find contracts', 'Get to any contract quickly.', 'Help centre › "Find a contract".', [
      ['Go to **Contracts**. Switch between **My contracts** and **All**.', 'My contracts shows the ones you own or back up.'],
      ['Type a reference, a supplier or a title in the search box.', 'The list narrows as you type.'],
      ['Click **Filters** and narrow to one site or supplier. Then clear the filters.', 'The list follows the filters; clearing brings everything back.'],
      ['Look at the **Key date** column.', 'Amber within 30 days, red once passed.'],
      ['Click **Export to Excel**.', 'An Excel file downloads with the contracts you can see.'],
    ]],
    ['Suppliers (Procurement)', 'Everything about a supplier in one place: its contracts, its documents, the relationship and business reviews.', 'Help centre › "Set up a supplier\'s details", "Add a supplier document" and "Record a business review".', [
      ['Go to **Suppliers** and open the supplier you made in section 2.', 'Overview, Documents, Business reviews and Contracts. Your contract is under Contracts.'],
      ['On **Overview**, click **Edit**. Make yourself the **Relationship owner**, choose a **Tier**, set **Risk** to Medium without a reason and save.', 'It asks why the risk is Medium.'],
      ['Add a reason, set the **Next business review** to next month, and save.', 'The details show, with a coloured risk badge.'],
      ['Click **Add contact** and add a made-up person (for example "Sam Example", Account manager, sam@example.com).', 'The contact is listed.'],
      ['Choose **Documents**, then **Add document**. Kind **Insurance certificate**, drag in any PDF, expiry date in 3 weeks. Tick **Remind me before it expires**.', 'The document shows with its expiry date and that you\'ll be reminded.'],
      ['Click **New version** on it and give a later expiry date.', 'The new version replaces it; the earlier one is kept in its history.'],
      ['Choose **Business reviews** › **Add business review**. Held last week, you as an attendee, a short summary, a rating and scores. Add an action for yourself due next week. Click **Add review**.', 'The review shows with its scores and the action. Home shows the action under Suppliers.'],
      ['Click **Attach a file** on the review and add a "deck" (any file).', 'It\'s linked to the review.'],
      ['Tick the action done.', 'It\'s marked done with your name. It leaves your Home.'],
      ['Choose **Contracts** on the supplier page.', 'Every contract with this supplier, and their documents, are listed.'],
    ]],
  ],
}

// ---------------------------------------------------------------- Legal

const legal = {
  title: 'Legal',
  who: 'For Legal: picking up contracts, the back-and-forth with the owner, and clearing contracts and Variations.',
  time: '60 minutes, plus waiting for your partners',
  fillIn: ['Your name', 'Date', 'Your Contract owner partner', 'Their test contract\'s reference: JBSAUS-'],
  tips: [...journeyNote, '**Before you start**, check you can sign in and see **Contracts**. If you can\'t, tell your admin: they give you access and the Legal role.'],
  sections: [
    ['Find your way around', 'Home shows what\'s waiting for Legal, so nothing sits unnoticed.', 'Help centre › "Find your way around" and "Review a contract (Legal)".', [
      ['Open JBS Contracts. Look at **Home**.', 'Tiles such as **Your turn (Legal)**, **New to pick up**, **Variations for Legal** and **Waiting on the owner** (demo contracts fill them).'],
      ['Click the **?** beside the greeting.', 'A help panel explains Home for you.'],
      ['Click **New to pick up**.', 'Contracts sent to Legal that nobody has taken yet.'],
      ['Open **Help** in the menu. Watch **Review a contract (Legal)**.', 'A short silent video with captions.'],
      ['Click your name at the bottom of the menu and look at **Email reminders**.', 'Choose how your reminder emails come. Emails that hand you a contract always come straight away.'],
    ]],
    ['Practise on a demo contract', 'Try every Legal action on made-up contracts first.', 'The ? on the contract page.', [
      ['Open a contract from **New to pick up** and click **I\'ll take it**.', 'It\'s yours: your name shows as the Legal person, and it moves to **Your turn (Legal)**.'],
      ['Click **Update status** and choose **Waiting on supplier**, with a note such as "Insurance certificate requested".', 'The status shows on the contract, so the owner can see where it\'s up to without asking.'],
      ['Click **Give to someone in Legal** (if there\'s another Legal tester) and choose them.', 'They\'re now the Legal person and get an email. Whose turn it is doesn\'t change.'],
      ['On another demo contract, add a document with Kind **Legal mark-up** (drag a file onto the Documents card).', 'It\'s added with your name.'],
      ['Open a demo contract marked **Confidential** (for example "Executive recruitment services").', 'You can see it. (Most people can\'t; Legal can.)'],
    ]],
    ['The shared journey: review the contract', 'Your Contract owner partner sends you their test contract.', 'Help centre › "Review a contract (Legal)".', [
      { wait: 'until your Contract owner partner tells you their contract is with Legal. Write its reference on page 1.' },
      ['On Home, open **New to pick up** and find their contract.', 'It\'s there, with their note and attachments in the Legal conversation.'],
      ['Click **I\'ll take it**, then **Update status** › **Reviewing**.', 'It\'s yours and shows as Reviewing.'],
      ['Click **Send to owner**. Ask a question (for example "Can the supplier cap liability at $2m?") and attach a "mark-up" (any file).', 'It goes back to the owner. It moves to **Waiting on the owner** on your Home.'],
      { handover: 'Tell your **Contract owner partner** it\'s back with them.' },
      { wait: 'until they tell you they\'ve answered.' },
      ['Open the contract.', 'It\'s your turn again. The conversation shows every turn in order, with any files.'],
      ['Click **Legal cleared** and write a short outcome (for example "Cleared. Liability cap agreed at $2m.").', 'It moves to **Signing**. The owner and backup are emailed.'],
      { handover: 'Tell your **Contract owner partner** it\'s cleared.' },
    ]],
    ['The shared journey: the Variation', 'Later, the owner changes the approved contract with a Variation and sends it to you.', 'The ? on the Variation page.', [
      { wait: 'until the owner tells you the Variation is with Legal.' },
      ['On Home, open **Variations for Legal** and open it.', 'Old and new values side by side, with the owner\'s reason.'],
      ['Take it, then click **Legal cleared** with an outcome.', 'It goes back to the owner to send for approval.'],
      { handover: 'Tell your **Contract owner partner** the Variation is cleared.' },
    ]],
    ['Find contracts', 'Get to anything quickly.', 'Help centre › "Find a contract".', [
      ['Go to **Contracts** and choose **With Legal**.', 'Everything with Legal, showing whose turn it is and for how long.'],
      ['Search for a supplier name.', 'The list narrows as you type.'],
      ['Press **Ctrl+K** from any page and type a reference.', 'You can jump straight to it.'],
    ]],
  ],
}

// ---------------------------------------------------------------- Approver and Group Head

const approver = {
  title: 'Approver and Group Head',
  who: 'For approvers and the Group Head: checking signed contracts and Variations and approving them into the register, and (Group Head) seeing what the team has coming up.',
  time: '45 minutes, plus waiting for your partners',
  fillIn: ['Your name', 'Date', 'Your Contract owner partner', 'Their test contract\'s reference: JBSAUS-'],
  tips: [...journeyNote, '**Nobody approves their own contract.** If you raised, own or back up a contract, you\'ll see why you can\'t approve it, and another approver can.', '**Before you start**, check you can sign in. If you can\'t, tell your admin.'],
  sections: [
    ['Find your way around', 'Home shows what\'s waiting for your approval.', 'Help centre › "Find your way around" and "Approve a contract".', [
      ['Open JBS Contracts. Look at **Home**.', 'Tiles such as **To approve** and **Variations to approve** (demo contracts fill them).'],
      ['Click the **?** beside the greeting.', 'Help explains Home for you.'],
      ['Open **Help** in the menu. Watch **Approve a contract (on your phone or computer)**.', 'A short silent video with captions.'],
      ['Click your name at the bottom of the menu and look at **Email reminders**.', 'Choose how your reminder emails come. Approval requests always come straight away.'],
    ]],
    ['Practise on demo contracts', 'Try approving and sending back on made-up contracts first.', 'The ? on the approval screen.', [
      ['Click **To approve** and open a demo contract.', 'A clear page: the key details, Legal\'s outcome and the signed copy.'],
      ['Open the signed copy.', 'It opens. You\'d check it matches the details.'],
      ['Click **Send back** without a reason.', 'It asks you to say what needs fixing.'],
      ['Add a reason and send it back.', 'It goes back to the owner, in Signing, with your reason.'],
      ['Open another demo contract to approve and click **Approve and register**.', 'It becomes **Effective** and locked.'],
      ['If you have a phone that can reach QA, open the approval email or JBS Contracts on it and open one to approve.', 'The approval page fits the phone and is easy to use with a thumb.'],
      ['Open **Variations to approve** and open one.', 'Each change is shown as old and new. **Approve** applies them all at once.'],
    ]],
    ['The shared journey: approve the contract', 'Your Contract owner partner sends you their signed contract.', 'Help centre › "Approve a contract".', [
      { wait: 'until your Contract owner partner tells you their contract is waiting for approval. Write its reference on page 1.' },
      ['Open it from **To approve**.', 'The details, Legal\'s outcome and the signed copy are shown.'],
      ['Click **Send back**, with a reason (for example "Please add the schedule of rates").', 'It goes back to the owner.'],
      { handover: 'Tell your **Contract owner partner** you\'ve sent it back.' },
      { wait: 'until they tell you it\'s ready again.' },
      ['Open it again and click **Approve and register**.', 'It\'s **Effective** and locked. It now appears on the register.'],
      { handover: 'Tell your **Contract owner partner** it\'s approved.' },
    ]],
    ['The shared journey: approve the Variation', 'Later, the owner changes the contract with a Variation.', 'The ? on the Variation page.', [
      { wait: 'until the owner tells you the Variation is waiting.' },
      ['Open **Variations to approve** and open it.', 'The change (for example a later end date) is shown old and new, with Legal\'s outcome.'],
      ['Click **Approve**.', 'It says the changes now apply. The contract shows the new end date.'],
      { handover: 'Tell your **Contract owner partner** the Variation is approved.' },
    ]],
    ['Group Head: see the team (Group Head only)', 'See who has what overdue and coming up, so you can help them focus.', 'Help centre › "See what your team has coming up".', [
      ['Go to **Team**.', 'Each person with what\'s overdue, coming up, on their turn and waiting on others.'],
      ['Change how far ahead to look: 30, 60, 90 and 180 days.', 'The numbers change.'],
      ['Click a person.', 'Their contracts, how long each has waited, the supplier and value.'],
      ['Click a contract.', 'It opens.'],
    ]],
  ],
}

// ---------------------------------------------------------------- Admin

const admin = {
  title: 'Admin',
  who: 'For admins: people and roles, lists, contract fields, emails and reminders, reports, importing old contracts and fixing mistakes.',
  time: '90 minutes',
  fillIn: ['Your name', 'Date', 'Testers you set up'],
  tips: ['**Do section 1 first**, before the other testers start: it gives them access and fills QA with practice contracts for everyone.', 'Admin changes affect everyone on QA. That\'s fine for testing, but tell the other testers before you change something big (like switching a reminder off).'],
  sections: [
    ['Set up the testers', 'Give each tester access and the right roles, and fill QA with practice contracts for everyone.', 'Help centre › "Give someone access", and the ? on People.', [
      ['Go to **People**.', 'Everyone with access, their roles, and whether they\'re active.'],
      ['Click **Add person** for a tester who isn\'t there yet. Type their Windows logon (the domain is added for you), name and work email. Tick their roles. Contract owner and Procurement testers get both ticks.', 'They\'re listed and get a welcome email.'],
      ['Open an existing tester and change their roles if needed.', 'The change is saved and recorded.'],
      ['Click your name at the bottom of the menu and choose **Refresh demo data (QA)**. Read the dialog, then confirm.', 'A message says how many demo contracts were loaded and for how many people. Old demo contracts are archived, not deleted.'],
      ['Click your name › **Try as someone else (QA)** and choose a Legal tester.', 'A banner shows you\'re trying the app as them. Their Home has Legal work on it.'],
      ['On the banner, click **Stop and go back to** your name.', 'You\'re back as yourself.'],
      ['On **People**, click **Export to Excel**.', 'A workbook of everyone and their roles downloads.'],
    ]],
    ['Lists', 'The choices people pick from, changed without any code.', 'The ? on Lists.', [
      ['Go to **Lists**. Look at the list menu.', 'Lists grouped under Contracts and Suppliers.'],
      ['Open **Contract document kinds** and click **Add document kind**. Add "Test certificate".', 'It\'s added. The note explains the built-in kinds are always there.'],
      ['Try adding "Signed copy".', 'It\'s refused: that one is built in.'],
      ['Open a contract and add a document: check the Kind list.', '"Test certificate" is offered.'],
      ['Back on Lists, **Rename** it to "Test certificate (renamed)", then **Hide** it.', 'Renamed everywhere. Hidden: no longer offered, but documents that use it keep the name.'],
      ['Open **Supplier risk levels** and add "Critical". Then hide it again.', 'Added, then hidden.'],
      ['Open **Categories** and add a subcategory under one of them.', 'It\'s shown under its category.'],
    ]],
    ['Contract fields', 'Ask for something new on every contract, without code.', 'Help centre › "Add a contract field".', [
      ['Go to **Contract fields**.', 'The fields in their sections, with a live preview of the Add contract form.'],
      ['Click **Add field** in a section. Label "Test field", Kind Text, some help text. Click **Add field**.', 'The preview shows the new field. Nothing changes for anyone until you save.'],
      ['Click **Save changes**. Open **Add contract**.', 'The new field is on the form.'],
      ['Back on Contract fields, hide "Test field" and save. Use **History** to see the versions.', 'It\'s gone from the form. History lists each saved version, which you can put back.'],
    ]],
    ['Emails and reminders', 'When reminders go out, how they come, and what every email says.', 'Help centre › "Change a reminder or email", and the ? on the page.', [
      ['Go to **Emails and reminders**. Read **How reminder emails come**.', 'As they happen, one email a day or one a week. This is the default for anyone who hasn\'t chosen their own.'],
      ['Open **End date reminder**. Untick **Send this reminder**.', 'It shows **Off**. Nobody gets it until it\'s switched back on.'],
      ['Tick it again.', 'It\'s back on.'],
      ['Open **Notice deadline reminder**. Change **Days before** to 60, 30, 14 and click **Save days**.', 'Saved. (Put it back to 90, 60, 30, 14, 7 afterwards.)'],
      ['Change a word in its wording. Click a placeholder button such as **{Reference}** to add it.', 'The preview updates with a sample contract.'],
      ['Click **Save wording**, then look at **History**.', 'Each change is listed with who and when. Put the wording back the same way.'],
    ]],
    ['Fix a mistake on a locked contract', 'An emergency unlock for mistakes a Variation can\'t fix, like a typo in an approved contract.', 'Help centre › "Fix a mistake on a locked contract (admins)".', [
      ['Open an Effective demo contract. Click **Emergency unlock**. Give a reason and choose 1 hour.', 'A banner shows it\'s unlocked, until when, and why.'],
      ['Click **Edit details**, change the title slightly and save.', 'Saved. Activity records the change with the unlock and its reason.'],
      ['Click **Lock it again now**.', 'It\'s locked again.'],
      ['On a locked contract, change **Confidential** (with a reason).', 'It changes, and it\'s recorded.'],
    ]],
    ['Reports and downloads', 'The register, the pipeline, approvals, the audit trail, and a full copy of everything.', 'Help centre › "Print or download the register" and "Download everything".', [
      ['Go to **Reports**. On **Register**, click **Columns** and add the supplier\'s SAP vendor number.', 'The column appears, and your choice is kept for next time.'],
      ['Click **Filters** and narrow to one business unit. Click **Print or save as PDF**.', 'A clean printout of just those contracts.'],
      ['Click **Download Excel**.', 'Every column, in Excel.'],
      ['Look at **Pipeline** and **Approvals**.', 'What\'s on the way, and how long approvals took.'],
      ['Open **Audit trail**.', 'Every change, newest first, and a message that the audit trail is intact.'],
      ['Open **Download everything** and download the workbook.', 'One workbook with a sheet for each part of the system.'],
      ['Go to **Team**.', 'Who has what overdue and coming up.'],
    ]],
    ['Import old contracts', 'Bring in contracts from the old system (for example a Medius export) and their files.', 'Help centre › "Import old contracts and their files".', [
      ['In Excel, make a small sheet with headings: Agreement No, Agreement Name, Vendor, Category, Contract Owner, Start Date, Expiry Date, Notice Period, Status. Add three made-up rows (for example TEST-001 to TEST-003). Give one an expiry date last year and one a category that doesn\'t exist. Use your own name as owner.', 'A small test spreadsheet.'],
      ['Go to **Import old contracts**. Drag the spreadsheet onto the box (or click **Choose the spreadsheet**).', 'Each column is matched to a field, with examples.'],
      ['Click **Check (nothing changes yet)**.', 'Each row shows Effective, Ended, or can\'t be imported and why (the unknown category).'],
      ['Click **Import** and confirm.', 'The good rows are imported. Open one: it\'s Effective and locked, and says where it came from with its old reference.'],
      ['Make a ZIP with three small files: "TEST-001 signed.pdf", "TEST-001 draft.docx" and "TEST-001 cover page sign off.pdf". Drag it onto the files box.', 'Each file matched to TEST-001, with its kind worked out: Signed copy, Draft, Internal sign-off.'],
      ['Change one file\'s kind using its drop-down, then click **Add files**.', 'The files are on the contract with the kinds shown.'],
      ['Import the same spreadsheet again and check.', 'Everything shows as already imported. Nothing is doubled.'],
    ]],
  ],
}

Promise.all([
  build(owner, 'JBS Contracts test script - Contract owner and Procurement.docx'),
  build(legal, 'JBS Contracts test script - Legal.docx'),
  build(approver, 'JBS Contracts test script - Approver and Group Head.docx'),
  build(admin, 'JBS Contracts test script - Admin.docx'),
]).then(() => console.log('Built 4 test scripts.'))
