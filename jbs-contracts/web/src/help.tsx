// Help for each screen, shown by the "?" beside the title. Update it whenever the screen changes (definition of done).
import type { HelpTopic } from '@/components/Help'

export const help = {
  home: {
    title: 'Your home page',
    body: (
      <>
        <p>Home shows what needs you. When something is waiting for you, it appears here with a button to deal with it.</p>
        <p>Use the menu on the left to move around. On a phone, the menu is behind the button at the top right.</p>
        <p>To change between light and dark, click your name at the bottom of the menu.</p>
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
        <p>The <strong>Key date</strong> is the notice deadline if there is one, otherwise the end date. It turns amber within 30 days and red once it has passed.</p>
        <p>Contracts marked Confidential only show for their owner and backup, Legal, approvers, admins and auditors.</p>
        <p>
          <strong>Excel</strong>: <strong>Export to Excel</strong> downloads the list. To add or change many at once, change or add rows in that file and use{' '}
          <strong>Import from Excel</strong>. You'll see what will be added, changed or not imported (and why) before anything changes. Nothing is deleted, and blank
          cells leave what's there.
        </p>
      </>
    ),
  },
  contract: {
    title: 'Working on a contract',
    body: (
      <>
        <p>The top of the page shows where the contract is up to: Draft, Legal review, Signing, Approval, then Effective.</p>
        <p>
          <strong>While it is a draft</strong>, click <strong>Edit details</strong> to fill in or change anything. Only a title is needed to start; the rest can wait.
        </p>
        <p>
          <strong>Documents</strong>: choose the kind, then <strong>Add document</strong>. Use <strong>New version</strong> to replace a document while keeping the earlier ones.
          Files can be PDF, Word, Excel, Outlook email, text or images, up to 25 MB.
        </p>
        <p>
          <strong>Activity</strong> lists everything that has happened to the contract, newest first. It can't be changed.
        </p>
        <p>If a draft isn't going ahead, use <strong>Withdraw this draft</strong> at the bottom and say why. Nothing is deleted.</p>
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
