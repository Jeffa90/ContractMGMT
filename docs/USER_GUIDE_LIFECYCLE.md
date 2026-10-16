# User guide: contract lifecycle, Legal review, approvals, Variations, renewals, notifications and dashboards

For contract owners, Legal reviewers, approvers, procurement administrators and configuration administrators. The reference configuration is the JBS procurement pack; a business administrator can change the steps, wording and reminder rules without code (section 9). Labels used below: *Implemented*, *Mocked* (Demo and Test only), *Not Verified* (built, but no live service was available to prove it), *Future*.

## 1. Where things are

| I want to… | Go to |
|---|---|
| See what needs me today | **Dashboards** (`/Dashboards`), then **Notifications** (`/Notifications`) |
| Move my contract to the next step | Open the contract, then **Lifecycle** (`/Contracts/Lifecycle/{id}`). Only the steps open to you are offered. |
| Send a contract to Legal, answer comments, resubmit | The contract's **Lifecycle** page; Legal works in **Legal** (`/Legal`) |
| Approve or reject registration | **Approvals** (`/Approvals`) |
| Change an Effective contract | **Variations** (`/Variations`), or an emergency unlock (section 6) |
| Renew a contract | **Renewals** (`/Renewals`) |
| Set how early I am reminded | The contract page, section **Reminders** |
| See failed emails and background jobs | **Admin → Notifications** (`/Admin/Notifications`) |
| Change the workflow or the reminder rules | **Admin → Configuration** (`/Admin/Configuration`) |

## 2. Contract owners: from draft to Effective

1. **Create** the contract (title, supplier, category, classification, dates, value). Choose whether Legal review is required.
2. **Legal** (if required): *Submit for Legal review*. Legal assigns, reviews, and either **clears** or **returns for rework** with a reason. Answer the comments on the contract, change what is needed, and *Resubmit*. Every round, comment and attachment is kept; earlier rounds are never overwritten. You can recall a submission before Legal starts, or withdraw.
3. **Initial Creation**: after clearance, register the initial version. Upload the **final agreement** (`/Contracts/Documents/{id}`).
4. **Send for signature**. *Manual* (Implemented): sign outside the system, then **upload the executed copy**. *Mock* (Demo and Test only) simulates the signing service. A connected signing service is *Future / Not Verified*. Clearance by Legal, signing and approval are three different things: none replaces another.
5. **Submit for register authorisation**. Needs the executed copy. You cannot approve your own submission.
6. The **Group Head of Procurement or a backup** approves (the contract becomes **Effective**, locked, with the Registered Date stamped in UTC) or rejects with a comment (the contract goes back to **Initial Creation**; fix and resubmit).

If two approvers respond at the same time, the first response is recorded and the second sees that it was already decided.

## 3. Legal reviewers

* **Legal** shows the queue. Filter by status, assigned to, outstanding for me, and subject (contract or Variation). You see only contracts you may open; Legal Privileged records need an explicit privileged-access grant.
* Open a review to **assign**, **begin**, **comment** (threaded), **attach** mark-ups, **return for rework** (a reason is required) or **clear**. The timeline shows who did what and when.

## 4. Approvers

**Approvals** lists requests for your approver group (or, as a backup, the group you back up). Open one to see the immutable **snapshot** that was submitted (reference, supplier, dates, value, documents with checksums, submitter). **Approve** or **Reject** (comment required). Your access to the contract exists only while the request is waiting. Disposition requests for ended contracts reach you the same way.

## 5. Variations (changes to an Effective contract)

1. From the Effective contract choose **Variation**, or go to **Variations → New**. State why, and enter the proposed values for the fields your organisation allows (usually value, currency, extension type, notice period, critical-notice flag, review date, end date). Other fields are refused.
2. While a Variation is open the contract stays **locked** and shows *Variation in progress*. Only one Variation can be open at a time.
3. The Variation follows its own steps: optional Legal review, prepare the variation document, sign, upload the executed copy, register authorisation.
4. **Approved**: the proposed values are applied and the before and after values are kept. The contract returns to Effective; the original Registered Date is not changed. **Rejected or cancelled**: nothing changes on the contract.

## 6. Emergency unlock (rare)

Only Procurement Administrators with the unlock permission. From the contract page choose **Emergency unlock**. You must have signed in recently (otherwise you are asked to confirm your sign-in), give a reason of at least 20 characters, choose the fields to open and a duration within the limit (default 60 minutes, at most 240). Only you can change the opened fields; classification is never offered. When you finish, or when time runs out, the contract is locked again automatically. Everything is in the audit trail, with the unlock id. A second-person confirmation is *Future*.

## 7. Renewals

From an Effective contract choose **Start renewal**. A **draft successor** is created with only the configured information copied (for example description, currency, value, extension type, notice period, reminder lead time; and Schedules). Dates, status and history are not copied; the start date can follow the day after the predecessor ends. The two contracts link to each other. Take the successor through the normal steps. When it is Effective, **complete** the renewal. **Cancel** withdraws the draft successor and puts the predecessor back as it was. The predecessor is never changed by starting a renewal.

## 8. Notifications, reminders and dashboards

* **Notifications** is your inbox, with unread and needs-acknowledgement filters. A message about a record you can no longer open shows only that you can no longer open it.
* **Reminders**: each contract has a lead time of 30, 60, 90 or 120 days (set on the contract by an owner or administrator). You are reminded about the **Review Date** and **End Date**; once per window; whoever owns the category *at that time* is told. **Evergreen** contracts are never reminded about an End Date but are about the Review Date. **Critical Notice** contracts are reminded earlier and also before the notice deadline. **Acknowledge** a reminder to tell colleagues it is being handled. Unacknowledged reminders are listed on a dashboard; escalation messages are *Future* (the data is kept).
* Email is sent for the events that have it in the policy. *Demo log* and *dev capture* only record messages (Demo and Test). *SMTP* is built but *Not Verified* against a live mail server. *Microsoft Graph* is a scaffold.
* **Dashboards** show ten lists: My Contracts, Awaiting My Action, Awaiting Legal, Pending Approval, Variations in Progress, Renewals in Progress, Expiring Soon, Reviews Due, Critical Notice, Reminded and Unacknowledged. Each shows only what you could open from the register.

## 9. Administrators

* **Admin → Notifications**: counts of pending, failed and dead-lettered emails, each delivery with its attempts and correlation id, **Retry**, and the background jobs (event dispatch, email delivery, reminder scan, unlock expiry) with their last results; **Run the background work now** runs them once. Administrators see only their own organisations.
* **Admin → Configuration**: workflow definitions (contract lifecycle, Variation) and the notification policy are configuration. Edit a **draft**, **validate** (messages show the exact path to fix), **publish**, or **roll back**; every change is versioned and audited. New contracts use the published release; contracts already created keep the release they started on. Publishing is refused if a definition would unlock a record, drop an approval safeguard, leave a step with no way out, use an unknown guard or permission, or if a template uses anything but plain text and the allowed placeholders: `{{contract.reference}}`, `{{contract.title}}`, `{{actor.name}}`, `{{link}}`, `{{due.date}}`, `{{days.remaining}}`, `{{window.days}}`, `{{step.name}}`.
* **Admin → Email capture** (Demo and Test only): the development mailbox.

## 10. What to do when something looks wrong

| Symptom | Likely cause and action |
|---|---|
| A step you expect is missing | You lack the permission, the contract is in a different state, or an earlier step is not finished. The page says which. |
| "This contract was changed by someone else" | The screen was out of date. Reload and repeat. |
| No reminder arrived | The date is past, the contract is Evergreen (End Date), the window has not opened, or the category had no owner (the organisation's Procurement Administrators are told instead). |
| An email is in *Dead letter* | The provider failed repeatedly. Fix the provider settings, then **Retry**. In-app messages were not affected. |
| An approver cannot open a contract | Access exists only while a request is pending for their group. |
