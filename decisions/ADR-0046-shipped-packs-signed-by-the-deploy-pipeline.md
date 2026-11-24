# ADR-0046: Shipped solution packs are signed by the deploy pipeline

Status: **Optional since ADR-0047** (shipped packs are trusted without a signature; the signing step runs only when an environment holds `PACK_SIGNING_KEY`). Implemented in `.github/actions/build-app/action.yml`; sign and Production verify dry-run locally with a throw-away key; **Not Verified on IIS**. Owner decision 2026-11-24 (Jeff, through Clayton). Builds on ADR-0014 and ADR-0038. Production approval remains external governance and is not declared.

## Context

Production mode accepts only packs signed by a trusted key (docs/PACK_SIGNING.md). The shipped packs are built unsigned from `templates/packs` by the same build that produces the application, so a Production-mode QA install could not get past the pack step. The other JBS apps trust their configuration because it comes from the repository through CI and the deploy runner. The shipped packs come by exactly that route.

## Decision

1. Each GitHub environment that runs Production mode holds a pack signing key pair. The **private** key is the environment secret `PACK_SIGNING_KEY` (PEM). The **public** key and key id are the variables `APPENV_Ecp__Packs__TrustedKeys__0__PublicKeyPem` and `APPENV_Ecp__Packs__TrustedKeys__0__KeyId`, which are also stamped into web.config so that the installation trusts the key.
2. The build signs every shipped `.ecpkg` with `ecp-pack sign --key-ref env:PACK_SIGNING_KEY` and verifies each one with `ecp-pack verify --production` against the public key before anything is deployed. The build manifest is written afterwards, so it records the signed files. Without the secret, packs stay unsigned (Demo and Test only).
3. Unchanged: uploaded packs, the Pack Manager's trusted publishers, and the Production refusal of unsigned, untrusted, revoked or expired packs.

## Consequences

* The private key exists only in the GitHub secret and in the signing step's process environment. It is never written to disk, web.config or the logs.
* Key custody (docs/PACK_SIGNING.md section 6): generate the pair offline, keep an offline copy of the private key, and rotate by setting a new pair (and a new key id). Packs signed with the old key need re-signing, which happens on the next deploy.
* Anyone who can change the environment secret can sign packs that the installation trusts. That is the same trust as the ability to deploy code.
