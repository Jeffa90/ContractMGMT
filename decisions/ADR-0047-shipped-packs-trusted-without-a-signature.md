# ADR-0047: Shipped solution packs are trusted in Production without a signature

Status: Implemented; tests pass (`PackSigningTests` ADR_0047 cases, `ShippedPackTrustScenarios`); **Not Verified on IIS**. Owner decision 2026-11-24 (Jeff, on Clayton's advice that the QA install should not need a pack signing key). Makes ADR-0046 optional; builds on ADR-0014 and ADR-0038. Production approval remains external governance and is not declared.

## Context

Production accepted only packs with a valid signature from a trusted key (docs/PACK_SIGNING.md section 5). The packs built from `templates/packs` ship unsigned inside the application folder (the packs folder), so a Production-mode install could not use them until an operator signed them. ADR-0046 automated that with a key pair held on each GitHub environment. The owner does not want to manage a signing key for packs that come from the repository with the code.

## Decision

1. In Production, a pack read from the **shipped** folder that has **no signature** is trusted with the new status `ShippedWithApplication` (key source "shipped with the application"), when `Ecp:Packs:TrustShippedPacks` is true (the default) **and** the shipped folder is inside the application folder. Anyone who can change that folder can already change the application, so a signature there adds nothing.
2. Unchanged: **uploaded** packs (Pack Manager, tenant storage) always need a valid signature from a trusted key in Production; a shipped pack that **has** a signature that fails (unknown, revoked or expired key, tampered content) is still refused; Demo and Test rules are unchanged.
3. ADR-0046 becomes optional: the build still signs the shipped packs if an environment holds `PACK_SIGNING_KEY` (with the trusted key variables); without the secret the step is skipped and nothing else is needed.

## Consequences

* QA and Production need no pack signing key, secret or trusted-key variables for the shipped packs.
* `Ecp:Packs:TrustShippedPacks=false` restores the earlier rule (shipped packs must be signed).
* A shipped folder configured outside the application folder (`Ecp:Packs:ShippedDirectory`) is not covered by this rule: its packs need signatures.
