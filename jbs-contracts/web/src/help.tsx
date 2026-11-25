// Help for each screen, shown by the "?" beside the title. Update it whenever the screen changes (definition of done).
import type { HelpTopic } from '@/components/Help'

export const help = {
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
        <p>If a contract isn't going ahead (before it's approved), use <strong>Withdraw this contract</strong> at the bottom and say why. Nothing is deleted.</p>
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
        <p>Click a supplier's contract count to see their contracts.</p>
        <p>
          <strong>Excel</strong>: <strong>Export to Excel</strong> downloads the list. To add or change many at once, change or add rows in that file and use{' '}
          <strong>Import from Excel</strong>. You'll see what will be added, changed or not imported (and why) before anything changes. Nothing is deleted, and blank
          cells leave what's there.
        </p>
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
} satisfies Record<string, HelpTopic>
