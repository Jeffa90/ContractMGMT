// Help for each screen, shown by the "?" beside the title. Update it whenever the screen changes (definition of done).
import type { HelpTopic } from '@/components/Help'

const topics = {
  home: {
    title: 'Your Home',
    body: (
      <>
        <p>
          Home is your to-do list. <strong>Tiles</strong> show only while something is waiting for you, for example contracts back from Legal, waiting for your
          approval, or with a notice deadline in the next 30 days. Click a tile to see those contracts.
        </p>
        <p>
          <strong>Urgent</strong> lists your Effective contracts whose notice deadline has passed (while the contract is still running) or is within 7 days.
        </p>
        <p>
          <strong>Choose what I see</strong> hides tiles you don't want and picks which group comes first. It only changes your own Home.
        </p>
        <p>
          <strong>Search</strong> (or Ctrl+K, Cmd+K on a Mac, from any page) finds a contract by title, reference, supplier or SAP number, or a supplier. Use the
          arrow keys and Enter, or click.
        </p>
      </>
    ),
  },
  people: {
    title: 'Adding and managing people',
    body: (
      <>
        <p>Only people added here can open JBS Contracts. Everyone signs in automatically with their Windows account.</p>
        <p>
          <strong>To add someone</strong>
        </p>
        <ol>
          <li>Click <strong>Add person</strong>.</li>
          <li>Type their Windows username. The domain (for example AMH\) is filled in for you.</li>
          <li>Add their name and work email (reminders and approvals go there).</li>
          <li>Tick what they can do, then click <strong>Add person</strong>.</li>
        </ol>
        <p>
          <strong>What each tick means</strong>
        </p>
        <ul>
          <li><strong>Contract owner</strong>: raises contracts and works them through to Effective.</li>
          <li><strong>Legal</strong>: reviews contracts and sends them back and forth with the owner.</li>
          <li><strong>Approver</strong>: approves contracts into the register. One approver is the <strong>Group Head</strong>; the others are backups.</li>
          <li><strong>Admin</strong>: manages people and settings.</li>
          <li><strong>Auditor</strong>: can read everything, including history, but can't change anything.</li>
        </ul>
        <p>With no ticks, a person can look at contracts but not change them.</p>
        <p>
          <strong>When someone leaves</strong>, open them and click <strong>Switch off access</strong>. Nothing they did is lost, and you can switch them
          back on later.
        </p>
        <p>
          <strong>Excel</strong>: <strong>Export to Excel</strong> downloads the list. To add or change many at once, change or add rows in that file and use{' '}
          <strong>Import from Excel</strong>. You'll see what will be added, changed or not imported (and why) before anything changes. Nothing is deleted, and blank
          cells leave what's there.
        </p>
      </>
    ),
  },
  contracts: {
    title: 'Finding contracts',
    body: (
      <>
        <p>
          <strong>My contracts</strong> shows the contracts you own or are the backup for. <strong>All</strong> shows every contract you can see.
        </p>
        <p>Type in the search box to find a contract by its title, reference (for example JBSAUS-0012), supplier or SAP number.</p>
        <p>
          <strong>Filters</strong> narrow the list, for example to one site or supplier. Choosing a category includes its subcategories. Your admin chooses which fields can
          be used, under Admin, Contract fields.
        </p>
        <p>The <strong>Key date</strong> is the notice deadline if there is one, otherwise the end date. It turns amber within 30 days and red once it has passed.</p>
        <p>Contracts marked Confidential only show for their owner and backup, Legal, approvers, admins and auditors.</p>
        <p>
          <strong>Excel</strong>: <strong>Export to Excel</strong> downloads the list. To add or change many at once, change or add rows in that file and use{' '}
          <strong>Import from Excel</strong>. You'll see what will be added, changed or not imported (and why) before anything changes. Nothing is deleted, and blank
          cells leave what's there.
        </p>
        <p>
          Fields your admin added have their own columns. For a pick-from-a-list field, type the option as it's shown (several are separated by commas); for a person,
          their Windows logon or full name. Only contracts you could edit on screen (drafts, or ones back with the owner from Legal) can be changed by import; rows for other contracts are fine as long as they change nothing.
        </p>
      </>
    ),
  },
  contract: {
    title: 'Working on a contract',
    body: (
      <>
        <p>The top of the page shows where the contract is up to (Draft, Legal review, Signing, Approval, then Effective), who has it and what happens next.</p>
        <p>
          <strong>While it is a draft</strong>, click <strong>Edit details</strong> to fill in or change anything. Only a title is needed to start; the rest can wait
          until you send it on, when the required fields are checked.
        </p>
        <p>
          <strong>Legal review</strong> passes back and forth. <strong>Send to Legal</strong> hands it over; someone in Legal clicks <strong>I'll take it</strong>.
          Legal can <strong>Send to owner</strong> with what's needed (often to negotiate with the supplier), and any contract owner can make the changes and send
          it back. On Legal's turn the details are frozen, but anyone involved can still add documents. When Legal is happy they click{' '}
          <strong>Legal cleared</strong> with a short outcome, and it moves to Signing. Each hand-over emails the people whose turn it is, and is kept in the{' '}
          <strong>Legal conversation</strong>.
        </p>
        <p>
          <strong>Documents</strong>: choose the kind, then <strong>Add document</strong>. Use <strong>New version</strong> to replace a document while keeping the earlier ones.
          Files can be PDF, Word, Excel, Outlook email, text or images, up to 25 MB.
        </p>
        <p>
          <strong>Activity</strong> lists everything that has happened to the contract, newest first. It can't be changed.
        </p>
        <p>
          <strong>Signing</strong>: sign it in DocuSign, then <strong>Send for approval</strong> with the signed copy and the effective date (it's required and never
          filled in for you). The Group Head confirms it and it becomes Effective and locked. If details must change during Signing, use{' '}
          <strong>Send back to Legal</strong> (or <strong>Back to draft</strong> if it didn't need Legal).
        </p>
        <p>
          <strong>Once it's Effective</strong>, the Renewal card is where you <strong>record a decision</strong> (Renew, Let it end or Renegotiate) before the
          notice deadline, mark a <strong>review done</strong>, or <strong>end it early</strong> with a reason. Reminder emails stop once there's a decision.
        </p>
        <p>
          <strong>Changes</strong> to an Effective (locked) contract: <strong>Start a Variation</strong> to change dates, value and the like;{' '}
          <strong>Hand over</strong> to change the owner or backup at any time. Admins can change Confidential (with a reason) and, for a mistake a Variation
          can't fix, use an <strong>emergency unlock</strong> of up to 4 hours.
        </p>
        <p>If a contract isn't going ahead (before it's approved), use <strong>Withdraw this contract</strong> at the bottom and say why. Nothing is deleted.</p>
      </>
    ),
  },
  variation: {
    title: 'Variations',
    body: (
      <>
        <p>
          A Variation is how an Effective contract changes: its dates, notice period, value, business unit, site and most added fields. Nothing changes on the
          contract until the Variation is approved; then every change applies at once and is recorded.
        </p>
        <p>
          Choose what changes (now and new are side by side) and <strong>Save changes</strong>. If <strong>Needs Legal review</strong> is ticked, send it to
          Legal; it goes back and forth like a contract's review until Legal clears it. Then <strong>Send for approval</strong>. Adding the signed variation
          document is optional; the approver decides whether it's needed.
        </p>
        <p>An approver who finds a problem sends it back with a reason. Withdrawing a Variation changes nothing. A contract has one open Variation at a time.</p>
      </>
    ),
  },
  renewals: {
    title: 'Renewals',
    body: (
      <>
        <p>
          Renewals lists Effective contracts whose <strong>notice deadline</strong> (the last day to give notice: the end date minus the notice period) or end date
          is coming up, overdue first. The dots show the same contracts along the next 30, 60, 90 or 180 days; hollow dots already have a decision.
        </p>
        <p>
          <strong>Record decision</strong>: <strong>Renew</strong> starts a new draft linked to this contract (it ends when the renewal becomes Effective);{' '}
          <strong>Let it end</strong> ends it on its end date (for an evergreen contract, on the date you choose); <strong>Renegotiate</strong> means you're
          changing terms through a Variation. Recording a decision stops the notice and end-date reminders.
        </p>
        <p>
          Reminder emails go to the owner and backup before notice deadlines, end dates and review dates. Seven days before a notice deadline with no decision,
          the Group Head is told too.
        </p>
      </>
    ),
  },
  reminders: {
    title: 'Emails and reminders',
    body: (
      <>
        <p>
          Every email JBS Contracts sends is here, grouped: reminders, the welcome email, Legal review hand-overs and approvals. Open one to change its wording.
        </p>
        <p>
          <strong>Wording</strong> is plain text with placeholders such as {'{Reference}'}, filled in for each email. Each email has its own list (the welcome
          email has no contract, for example); use the buttons to add them. A paragraph whose placeholder has nothing to fill, such as a note nobody wrote, is
          left out. Text is never treated as code or formatting. The preview shows it with a sample contract.
        </p>
        <p>
          <strong>Reminders</strong> are checked every morning at 7:00am Brisbane time; a missed morning catches up the next day and no reminder is sent twice.{' '}
          <strong>Days before</strong> sets when each one goes out. If a contract is added late, only the most recent reminder is sent.
        </p>
        <p>Every saved change is kept in the History, with who made it and when.</p>
      </>
    ),
  },
  approve: {
    title: 'Approving a contract',
    body: (
      <>
        <p>
          The contract has already been approved and signed in DocuSign. Approving it here confirms the signed copy and the details are right, puts it on the
          register and makes it <strong>Effective</strong> from its effective date. It is then locked.
        </p>
        <p>
          Open the signed copy and check it matches the details. If something isn't right, choose <strong>Send back</strong> and say what needs fixing; it goes back
          to Signing and the owner gets an email.
        </p>
        <p>You can't approve a contract you raised, own or are the backup for. Another approver can.</p>
      </>
    ),
  },
  suppliers: {
    title: 'Suppliers',
    body: (
      <>
        <p>Every contract is with a supplier. Add a supplier here, or straight from a contract with <strong>Add new supplier</strong>.</p>
        <p>The ABN is checked for typing mistakes (it doesn't look anything up). The SAP vendor number is optional.</p>
        <p>Click a supplier's name to open its page: its details and contacts, its documents (and those from its contracts), business reviews and contracts.</p>
        <p>
          <strong>Excel</strong>: <strong>Export to Excel</strong> downloads the list. To add or change many at once, change or add rows in that file and use{' '}
          <strong>Import from Excel</strong>. You'll see what will be added, changed or not imported (and why) before anything changes. Nothing is deleted, and blank
          cells leave what's there.
        </p>
      </>
    ),
  },
  supplier: {
    title: 'A supplier',
    body: (
      <>
        <p>Everything about one supplier in one place. Everyone can read it; people ticked Procurement (and admins) look after it.</p>
        <ul>
          <li>
            <strong>Overview</strong>: who looks after the supplier (the relationship owner), its tier, its risk and why, when the next business review is due, and
            its contacts.
          </li>
          <li>
            <strong>Documents</strong>: the supplier's own documents, such as insurance and certificates, with their expiry dates, and below them every document
            from its contracts. Tick <strong>Remind me before it expires</strong> on a document to get an email before it expires (60 and 30 days before, unless
            your admin changes it). When a renewed certificate arrives, use <strong>New version</strong> and give its new expiry date; earlier versions are kept.
          </li>
          <li>
            <strong>Business reviews</strong>: a log of each review with who attended, a summary, an overall rating, scores from 1 to 5, the actions agreed and the
            deck. Each action's owner is emailed and sees it on their Home until they tick it done. Procurement, admins and the relationship owner add reviews.
          </li>
          <li>
            <strong>Contracts</strong>: this supplier's contracts you can see.
          </li>
        </ul>
        <p>Every change is recorded. Removing a document or contact hides it; nothing is deleted.</p>
      </>
    ),
  },
  import: {
    title: 'Importing old contracts',
    body: (
      <>
        <p>Bring contracts across from the old system (for example an export from Medius), then their files. Admins only. Try it on QA first.</p>
        <ol>
          <li>
            <strong>Choose the spreadsheet</strong>: an Excel workbook (.xlsx) or a CSV, one contract per row, headings in row 1.
          </li>
          <li>
            <strong>Match the columns</strong>: for each column in the file, choose where it goes. Old reference, title, supplier, category and owner are needed (you
            can choose a default owner and category for rows without one). Your matching is remembered for next time.
          </li>
          <li>
            <strong>Check</strong>: the dry run shows each row: imported as Effective (current, locked, with its original dates), as Ended (expired), already
            imported, or can't be imported and why. Nothing changes yet. Fix problems in the file (or add missing people and categories) and check again.
          </li>
          <li>
            <strong>Import</strong>: the ready rows go in, in one step. Suppliers that aren't in JBS Contracts yet are added. Each contract keeps its old reference,
            shown on the contract and searchable, so importing the same file again skips the ones already there.
          </li>
          <li>
            <strong>Files</strong>: put the files in ZIPs (up to 25 MB each) with the old reference in each file's name or folder. One ZIP can mix signed
            copies, drafts, variations, cover pages and other files: words like <em>signed</em>, <em>executed</em>, <em>draft</em>, <em>variation</em>,{' '}
            <em>amendment</em>, <em>cover page</em> or <em>sign off</em> in a folder or file name say what it is, and the rest get the kind you choose. Check the
            list, change any that are wrong (one at a time, or every file of a kind at once), then add them. A file already on its contract is skipped.
          </li>
        </ol>
        <p>Every contract and file is recorded in its Activity and the audit trail, with the file it came from.</p>
      </>
    ),
  },
  lists: {
    title: 'Lists',
    body: (
      <>
        <p>These are the choices people pick from on a contract: categories (with subcategories), agreement types, business units, sites and currencies.</p>
        <p>
          <strong>Hide</strong> takes something out of the choices without changing contracts that already use it. You can show it again at any time. Nothing is deleted.
        </p>
        <p>Every change is recorded with who made it and when.</p>
        <p>
          <strong>Excel</strong>: <strong>Export to Excel</strong> downloads the list. To add or change many at once, change or add rows in that file and use{' '}
          <strong>Import from Excel</strong>. You'll see what will be added, changed or not imported (and why) before anything changes. Nothing is deleted, and blank
          cells leave what's there.
        </p>
      </>
    ),
  },
  fields: {
    title: 'Arranging the contract fields',
    body: (
      <>
        <p>This is what people fill in on a contract, and where it appears. The preview shows the Add contract form as people will see it.</p>
        <ul>
          <li>
            <strong>Add field</strong> under a section: choose its kind (text, number, money, date, yes/no, a list of options, a person, a supplier, a link or an email).
          </li>
          <li>Use the arrows to move fields and sections, and the pencil to rename them or change their help.</li>
          <li>
            <strong>Remove</strong> hides a field. Values already entered are kept and you can bring it back. Fields the contract rules need (like Title and End date) can be renamed and
            moved but not removed.
          </li>
          <li>
            Nothing changes for anyone until you click <strong>Save changes</strong>. <strong>History</strong> keeps every saved version, and you can put any of them back.
          </li>
        </ul>
        <p>A field's kind can't change once contracts have values in it. Add a new field instead and remove the old one.</p>
      </>
    ),
  },
  reports: {
    title: 'Reports',
    body: (
      <>
        <p>Reports are for admins and auditors. Each one can be downloaded to Excel. Confidential contracts are included and marked.</p>
        <ul>
          <li>
            <strong>Register</strong>: every Effective contract. Choose whether to include ended and archived ones. <strong>Columns</strong> lets you tick
            which fields to show, including the added ones; your choice is kept for next time and the printout uses it too. <strong>Filters</strong> narrow it
            down, as on the Contracts list. <strong>Print or save as PDF</strong> gives a clean landscape printout; in the print window, choose{' '}
            <em>Save as PDF</em> as the printer to keep a copy. <strong>Download Excel</strong> always has every column and follows your filters.
          </li>
          <li>
            <strong>Pipeline</strong>: how many contracts are in Draft, Legal review, Signing and Approval, how long they've been there, and which have waited longest.
          </li>
          <li>
            <strong>Approvals</strong>: what was approved or sent back, by whom, and how long each waited from <em>Send for approval</em> to the decision.
          </li>
          <li>
            <strong>Audit trail</strong>: everything that happened, newest first. Search by person, reference or words. The check at the top confirms no event has
            been changed or removed since it was recorded. The Excel download includes old and new values, and downloading it is itself recorded.
          </li>
          <li>
            <strong>Download everything</strong>: one Excel workbook with a sheet for each kind of record: contracts in every stage with every field,
            documents (details, not the files), Legal review, approvals, Variations, reminders sent, suppliers, people and lists. Admins can also download every
            document as a ZIP, a folder per contract, with an index. Both downloads are recorded in the audit trail.
          </li>
        </ul>
      </>
    ),
  },
  team: {
    title: 'The Team page',
    body: (
      <>
        <p>
          See at a glance who has what on their plate, so you can help people focus. It's for admins, auditors and Group Heads, and it only shows: nothing is
          changed or sent from here. The daily reminders already email owners.
        </p>
        <ul>
          <li>
            <strong>Overdue</strong>: an Effective contract whose notice deadline (or end date) has passed with no renewal decision recorded.
          </li>
          <li>
            <strong>Coming up</strong>: a notice deadline or end date in the period you choose (30 to 180 days), still with no decision. Once the owner records a
            decision it drops off.
          </li>
          <li>
            <strong>Their turn</strong>: Drafts to finish, contracts back from Legal, signed copies to attach and Variations waiting on them.
          </li>
          <li>
            <strong>Legal reviews</strong>: for Legal people, reviews they've picked up that are on Legal's turn. The With Legal total also counts ones nobody has picked
            up yet.
          </li>
          <li>
            <strong>Waiting on others</strong>: their contracts that are with Legal or waiting for approval. Nothing for them to do, but worth knowing.
          </li>
        </ul>
        <p>Click a person to see each contract with its supplier, value, category and end date, and how long it has waited. Click one to open it. Confidential contracts are counted only if you can see them.</p>
      </>
    ),
  },
} satisfies Record<string, HelpTopic>

/** Each screen's help, with its key as its id (the guides list the screens they belong to). */
export const help = Object.fromEntries(Object.entries(topics).map(([id, topic]) => [id, { ...topic, id }])) as { [K in keyof typeof topics]: HelpTopic }
