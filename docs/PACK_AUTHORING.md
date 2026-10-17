# Authoring and building a solution pack

Status: **Implemented** (command-line tool and library). Read `docs/PACK_MANAGER.md` for how a business administrator installs a pack and `docs/PACK_SECURITY.md` for what a pack may not contain.

A pack is JSON. You write documents, a small recipe, and build a `.ecpkg` with the pack tool. You never compile or deploy anything to use it.

## 1. Source layout

Shipped examples live under `templates/packs/<key>/` (source, committed). The built `.ecpkg` files are **not** committed; the host build writes them to `<output>/packs/`.

```
templates/packs/my-pack/
  pack-source.json                    the recipe (below)
  pack.json                           catalogue entry and starting defaults
  workflows/contract-lifecycle.json   the contract lifecycle definition
  workflows/variation.json            the Variation workflow definition
  notifications/notification-policy.json
```

`pack-source.json` names each source file, where it goes in the package and which kind it is:

```json
{
  "packageId": "example.pack.my-pack", "packKey": "my-pack", "version": "1.0.0",
  "displayName": { "en-AU": "My pack" }, "description": { "en-AU": "What it is for." },
  "publisher": { "id": "example.publisher", "name": "Example publisher" },
  "createdUtc": "2026-10-20T00:00:00Z",
  "compatibility": { "minPlatformVersion": "1.0.0", "maxPlatformVersion": null, "ignoreUnknownOptionalKinds": false },
  "signature": { "requirement": "Required" },
  "synthetic": false, "capabilities": [], "dependencies": [],
  "content": [
    { "source": "pack.json", "path": "content/solution-pack.json", "kind": "SolutionPack", "scope": "catalogue", "schemaVersion": 1, "required": true },
    { "source": "workflows/contract-lifecycle.json", "path": "content/contract-lifecycle.json", "kind": "WorkflowContractLifecycle", "scope": "organisation", "schemaVersion": 1, "required": true }
  ]
}
```

Rules the builder enforces: a pack key is lower case with hyphens; the version is semantic; every content path is `content/<name>.json`; the `SolutionPack` document's `packId` and `version` must match the recipe (`PACK-V003`); `createdUtc` is taken from the recipe (not the clock) so builds are reproducible; a document may not contain `$type`, script, a web address or an assembly-qualified type name (`PACK-V002`).

Kinds a pack may carry today: `SolutionPack`, `WorkflowContractLifecycle`, `WorkflowVariation`, `NotificationPolicy`. The platform also recognises `ApprovalMatrix`, `ClassificationPolicy`, `CustomField`, `ContractListView` and `Branding` as packable kinds; no shipped pack uses them yet (**Future** for shipped content, accepted by the format).

## 2. Commands

Run the tool from the repository: `dotnet run --project src/Ecp.PackTool -- <command> ...` (or the built `Ecp.PackTool.dll`).

| Command | Purpose |
|---|---|
| `build --source <dir> --out <file.ecpkg>` | Build one pack |
| `build-all --source <dir-of-sources> --out <dir>` | Build every pack under a folder (the host build uses this for `templates/packs`) |
| `validate --package <file> [--platform-version x.y.z]` | Archive, manifest, hashes, content safety and compatibility |
| `hash --package <file>` | Print the package SHA-256 and the content digest |
| `inspect --package <file> [trust options]` | Show the manifest summary and, with a trust file or public key, the signature result |
| `sign --package <file> --out <file> --key-id <id> (--key-file <private.pem> \| --key-env <VARIABLE>)` | Add a signature (see `docs/PACK_SIGNING.md`) |
| `verify --package <file> (--trust-file keys.json \| --public-key key.pem --key-id id) [--production]` | Check the signature as Production would |
| `compare --a <file> --b <file>` | Reproducibility check: equal when everything except the signature is identical |
| `keygen --out-private <file> --out-public <file>` | Make a **new** key pair for testing; keep the private key out of source control |

Exit codes: 0 success, 1 the package or request is not acceptable, 2 usage error. Output never contains key material.

## 3. Building and checking the shipped packs

```
dotnet build src/Ecp.Host -c Release          # builds packs into src/Ecp.Host/bin/Release/net10.0/packs
dotnet run --project src/Ecp.PackTool -- build-all --source templates/packs --out /tmp/packs-a
dotnet run --project src/Ecp.PackTool -- build-all --source templates/packs --out /tmp/packs-b
dotnet run --project src/Ecp.PackTool -- compare --a /tmp/packs-a/blank-1.4.0.ecpkg --b /tmp/packs-b/blank-1.4.0.ecpkg
```

Building twice from the same sources gives byte-identical packages (the ZIP is deterministic and the manifest canonical). Signatures are not deterministic (ECDSA), so reproducibility is defined on the **unsigned** package and its content digest; `compare` ignores the signature.

### 3a. The terminology document (S4)

Optional pack file terminology/terminology.json, content type `Terminology`, mode `complete`:

```json
{ "schemaVersion": 1, "mode": "complete",
  "terms": [ { "key": "supplier", "locale": "en", "singular": "Counterparty", "plural": "Counterparties" } ] }
```

Every one of the 32 required keys (`docs/TERMINOLOGY.md` §4) needs a singular and plural for each base locale you declare (`en`; a regional locale such as `en-AU` needs only what differs). Optional per term: `short`, `description`, `help`. The document may also carry an optional `groups` section naming approver groups (`key`, `locale`, `name`, optional `description`; the key is the stable approver-group key your workflow uses) and a `roles` section choosing which roles are offered in pickers (`code`, `offered`); see docs/TERMINOLOGY.md section 6a. Neither section changes any permission. Plain text only; the builder and the content scan refuse markup, script and template syntax. Add it to the recipe in `pack-source.json` with content type `Terminology`; it is hashed and signed with the other documents. A pack without it is valid: its organisations read the neutral wording. Notification templates in a pack may use tokens such as `[[contract|lower]]`; unknown keys fail validation. To build a pack with different wording, copy templates/packs/blank/terminology/terminology.json, change labels, rebuild and sign.

### 3b. The capabilities document (S6)

Optional pack file capabilities/capabilities.json, content type `Capabilities`, mode `defaults`: for each of the five platform capabilities (`legal.review`, `register.authorisation`, `variations`, `renewals`, `signature.requests`) say whether the pack's organisations start with it on or off, with an optional plain-text note (at most 300 characters) that the Pack Manager and the onboarding pack step show beside the default. A capability the document does not mention is on. The document is data: no conditions, expressions or code, and the validator refuses markup and template syntax. It must be coherent on its own (Variations need Register Authorisation) and consistent with the pack's own workflows: a pack that switches a capability off must not ship a workflow that uses it (the readiness check `capabilities` and the tests report it). Add it to the recipe in `pack-source.json` with content type `Capabilities`. An organisation's own published choice always wins over the pack's defaults. A pack without the document is valid: its organisations have every capability on, as before S6. See `docs/CAPABILITIES.md`.

## 4. Versioning

A pack version never changes once it has been installed anywhere. To change a pack, raise its version. Installing the same key and version with different bytes is refused (`PACK-D002`). An organisation moves to a new version by assignment, which creates drafts; nothing is overwritten.

## 5. Adding a second business

1. Copy `templates/packs/blank` to `templates/packs/<new-key>` (or write a pack elsewhere; a pack does not have to live in this repository).
2. Edit the documents. The lifecycle states, transitions, permissions, approver groups and notification rules are data.
3. Build, validate, sign (for Production) and upload through the Pack Manager. No source change and no redeploy.

A test (`A_third_pack_the_repository_has_never_seen_runs_a_contract_with_no_source_change`) does exactly this with a pack built at test time from a lifecycle whose initial state does not exist anywhere in the code or the shipped packs.

## 6. A complete second business: the shipped Harborline pack (S9)

`templates/packs/construction-subcontracts` is a **worked example of a full pack** and the proof that a second business needs no source change (`docs/CONSTRUCTION_SUBCONTRACTS_PACK.md`). It carries eleven documents: catalogue entry, contract lifecycle, Variation workflow, notification policy, terminology, capabilities, custom fields and lookups, two approval matrices, shared list views, a classification policy and branding. It was written against the real validators and every document uploaded on the first complete attempt. What the exercise taught pack authors:

* **Organisation-dependent documents upload with warnings, not errors** (G11, ADR-0031). A matrix condition on a custom field, a view over custom fields and a classification-gated document warn at upload ("depends on the organisation"); they are checked for real when published. Do not expect a green upload to mean a publishable draft.
* **Publish in dependency order.** Lookups and fields first, then matrices (a workflow that names a matrix is refused until it is published), then workflows, terminology, capabilities, notification policy and views. Tenant-level kinds (classification policy, branding) create tenant drafts, not organisation drafts.
* **Variation `changeableFields`** accept core fields and `cf.<key>`; they do not accept `functionCode` (`PACK-V001`).
* **`requiredFields` on a step check the contract the step runs on**, not a change order that is still being prepared. Put a required change-order field on the step that submits it, or enforce it with a custom-field `required` flag.
* **A role-less state is allowed**; a role appears at most once per workflow. A fact that needs no behaviour (such as practical completion) is better recorded by a Variation than by a state with no reminders.
* **Notification events are a closed list** (`docs/LIFECYCLE_AND_NOTIFICATIONS.md`). A pack chooses which of the 28 events are on and rewrites their wording; it cannot invent an event, and custom date fields get no reminders.
* **Never spell a label.** A pack sets a term for every label it wants changed; a label the pack does not set shows the neutral default, which the render sweep treats as a defect.
* **Approver groups and roles keep their stable identifiers.** A pack names them for display through terminology (`groups`, `roles`) and may hide a role from pickers; it never renames a code.

Build, sign and check it like any pack: `tools/check-packs.sh` builds all three shipped packs twice, compares them byte for byte, signs and verifies each with an ephemeral key and refuses tampered, wrong-key and unsigned packs.
