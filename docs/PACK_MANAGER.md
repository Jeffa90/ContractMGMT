# Pack Manager: using solution packs without code

Status (2026-10-17): **Implemented** as server-rendered forms. **Not verified against a live browser accessibility audit** (keyboard and landmark structure follow the existing pages; no assistive-technology test has been run). Nothing here declares production approval; that is external governance.

A **solution pack** is a ready-made starting point for a business: a contract lifecycle, a Variation workflow and a notification policy, plus the starting categories and contract types. A pack is **data only**. It never runs code, SQL, JavaScript or scripts. Installing a pack makes it *available*; it changes nothing until you give it to an organisation, and even then it only prepares **drafts** that you review and publish in the normal way.

The product ships with two packs (**Blank**, and a synthetic **reference** pack for a procurement business). They are ordinary pack files; nothing in the platform treats either specially. Your own pack is added the same way, without a rebuild or redeploy.

| Capability | Label |
|---|---|
| Catalogue, details, upload, validation report, installation preview, install, installed packs, organisation assignment, history, trusted publishers, checks | Implemented |
| Download of the validation report (CSV, formulas defused) | Implemented |
| Retire and reinstate an installed pack version | Implemented |
| Upgrade workflow across versions, per-item merge, pack export | Future |
| Marketplace, online catalogue, automatic download, billing | Future (not started by design) |
| Graphical designer, JSON editing in the browser | Future (not started by design) |

## 1. Who can do what

Permissions are allocated by role in `requirements/02-reference/role-permission-matrix.csv` and recorded in `requirements/01-authoritative/CONFIRMED_DECISIONS.md` (D7a). A test reads that matrix.

| Permission | What it allows | Tenant Admin | Organisation Admin | Business Config Admin | Auditor |
|---|---|---|---|---|---|
| `packs.read` | See the catalogue, details and checks | Yes | Yes | Yes | Yes |
| `packs.upload` | Upload a file to the holding area | Yes | No | Yes | No |
| `packs.validate` | See the result of the checks and the preview | Yes | Yes | Yes | No |
| `packs.install` | Install a checked pack | Yes | No | Yes | No |
| `packs.assign` | Give an organisation a pack (creates drafts) | Yes | Yes (own organisation) | Yes (own organisation) | No |
| `packs.retire` | Retire or reinstate an installed version | Yes | No | No | No |
| `packs.trust.manage` | Register or revoke a trusted publisher key | Yes | No | No | No |
| `packs.audit.read` | Read the Pack Manager history | Yes | No | No | Yes |

Trusted publisher keys decide which packs the whole tenant will accept, so they are **not** given to business configuration administrators. Install, retire, assign and trust changes also require a **recent sign-in** (within 10 minutes); if yours is older you are asked to confirm it is you, then returned to the page.

## 2. Walkthrough for a business administrator

1. **Open Administration, Solution packs.** The table lists every pack this installation can offer: name, version, publisher, whether it is shipped or installed, its **signature** (signed and trusted, **NOT SIGNED**, or not trusted), whether it is compatible with this platform, its status, how many organisations use it and when it was installed.
2. **Upload a pack.** Choose **Upload a pack**, pick the `.ecpkg` file from a publisher you trust and press **Upload and check**. The file goes to a safe holding area. Nothing is installed.
3. **Read the result.** The Review page shows who published it, what it contains, how it is signed, and a table of anything found: level (error or warning), a stable code, where, what was found and **how to fix it**. You can **download the report (CSV)** to send to the publisher. If there is an error you cannot install; discard the upload.
4. **Preview.** For a pack that passes, the page states what installing will do and what it will not: it publishes nothing, changes no organisation and no contract. It lists the kinds of configuration the pack carries, new capabilities, organisations already using that pack key and the next steps.
5. **Install.** Tick the confirmation and press **Install this pack**. The file is checked again from the held copy before it is stored. Installing the identical file again changes nothing. A different file with the same pack key and version is refused: a published version never changes, so publish the change as a new version.
6. **Give an organisation the pack.** Open **Organisation packs**, choose the organisation and the pack, and press **Preview what would change**. For each kind of configuration the page says whether something is already published, whether a draft is already waiting and what will happen. Press **Assign this pack and prepare drafts**. Drafts are created where none is waiting; an existing draft is kept and reported. Published configuration and contracts are never touched.
7. **Review and publish.** Open **Workflow configuration** (and the Configuration Studio), review each draft, validate it and publish it. Second-person publication applies where your installation requires it. Only then does the organisation run on the new definitions.
8. **Look back.** **History** shows every upload, check, install, retirement, assignment and trusted-key change, with who did it and the outcome.

Staying on the Blank configuration is always possible: choose **None (start from the Blank configuration)** during onboarding, or never assign a pack.

### Unsigned packs

An unsigned pack is shown with a red **NOT SIGNED** warning wherever it appears. It can be used only in a Demo or Test installation that has switched on `Ecp:Packs:AllowUnsignedSyntheticPacks`, and only if the pack is marked synthetic. **Production refuses it.** Do not use an unsigned pack for real contracts.

### Trusted publishers

**Trusted publishers** lists the public keys the tenant accepts (and keys set by the person who runs the installation, shown as read-only). To trust a publisher, paste their **public** key (it begins `-----BEGIN PUBLIC KEY-----`) with a key identifier and a label; a private key is refused and never displayed. Revoking a key makes packs signed only with it untrusted immediately, without deleting anything. See `docs/PACK_SIGNING.md`.

### Checks

**Checks** reports whether the shipped packs were found and are usable, which shipped files were not accepted and why, and which organisations point at a pack that cannot be used. It only reports.

### Terminology in a pack (S4)

A pack may carry a terminology document. The upload and review pages list it under the pack's documents and show **Terminology introduced**: each label the pack would give, beside what the organisation reads today. Assigning the pack creates an organisation Terminology draft (never published, never overwriting published wording or an open draft). Terminology is edited under Admin → Terminology with its own permissions (`terminology.*`; see `docs/TERMINOLOGY.md` §11); the `packs.*` permissions neither grant nor need them. Pack versions in this slice: Blank 1.2.0, JBS reference 2.2.0.

## 3. Organisations that need attention (report only)

Before this change an organisation with no pack, or whose pack could not be found, silently received the reference definitions. Nothing is rewritten now. **Checks** lists:

* `pack_unresolved`: the organisation is assigned a pack key that is not available (not installed, not trusted here, retired or incompatible). Published configuration is unaffected.
* `starting_definitions_from_other_pack`: a published release was installed from a different pack than the one assigned.

Manual options, in order of preference: (1) install the missing pack, then reopen the organisation; (2) assign the pack the organisation should be on and review the drafts, leaving the published releases in force until you publish; (3) if the published releases are wrong for the business, publish corrected definitions (every earlier release stays in history and can be rolled back); (4) choose **None** and keep the current published definitions as they are. No option deletes anything.

## 4. Operating notes

* **Where files live.** Shipped packs are read-only files in the `packs` folder next to the application (`Ecp:Packs:ShippedDirectory`). Installed packs are stored under the data directory (`Ecp:Packs:StorageDirectory`, default `<data directory>/packs`), outside any publicly served path, with a row per pack in the database.
* **Upgrade and rollback.** The database migration `AddSolutionPacks` is additive and idempotent: it adds three tenant tables with row-level security and changes no existing table. Rollback is by **restoring the database backup taken before the upgrade** (the migration has no down-step that preserves data); restore the matching application version with it.
* **Caches.** Opened packages are cached by tenant and package hash; a changed file, a retirement or a trust change is seen on the next read. If the cache is empty or unavailable the loader reads the authoritative storage.
* **Readiness.** A missing pack folder is *Unhealthy*; no usable shipped pack (for example unsigned in Production) is *Degraded* with the reason. See `docs/SETUP_AND_ONBOARDING.md`.
