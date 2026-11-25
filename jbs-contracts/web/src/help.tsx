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
      </>
    ),
  },
} satisfies Record<string, HelpTopic>
