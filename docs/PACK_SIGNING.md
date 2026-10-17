# Signing solution packs

Status: **Implemented** for signing and verification. **Key custody for Production is an external operational responsibility** and is not provided by this repository. No private key is committed (an architecture test fails the build if one appears). Nothing here declares production approval.

## 1. What is signed

The signature covers the **canonical manifest**, and the manifest lists the SHA-256 of every content document, so the signature commits to the whole package. The signing input is the ASCII text `ECP-PACK-MANIFEST-V1` and a line feed followed by the canonical manifest bytes (a domain-separation prefix, so a signature made for a package cannot be valid for anything else). The verifier re-canonicalises the manifest and refuses a manifest whose stored bytes differ, which removes signature malleability.

`signature.json` (canonical JSON):

```json
{ "algorithm": "ES256", "formatVersion": 1, "keyId": "publisher-2026", "signature": "<base64>", "signedUtc": "2026-10-20T00:00:00Z" }
```

## 2. Algorithm

Format version 1 uses **ECDSA P-256 with SHA-256** (`ES256`), 64-byte IEEE P1363 signatures, SPKI (`-----BEGIN PUBLIC KEY-----`) public keys and PKCS#8 (`-----BEGIN PRIVATE KEY-----`) private keys. It is built into .NET; the earlier plan for Ed25519 needed a new dependency because .NET 10 has no Ed25519, and the owner approved ES256 instead (ADR-0026 D3). The algorithm name is stored in `signature.json`; an unsupported algorithm is refused (`PACK-S007`), so another can be added later without a format change.

## 3. Making and using keys

Generate a key pair on a trusted workstation (or in your HSM or Key Vault, where you export only the public key):

```
dotnet run --project src/Ecp.PackTool -- keygen --out-private publisher.key --out-public publisher.pub.pem
```

Keep `publisher.key` out of source control (`.gitignore` already excludes `*.key` and `*.pem`). Sign a package with the key in a file or an environment variable your pipeline supplies; the tool never prints, stores or logs it:

```
dotnet run --project src/Ecp.PackTool -- sign --package my-pack-1.0.0.ecpkg --out my-pack-1.0.0.signed.ecpkg --key-id publisher-2026 --key-file publisher.key
dotnet run --project src/Ecp.PackTool -- verify --package my-pack-1.0.0.signed.ecpkg --public-key publisher.pub.pem --key-id publisher-2026 --production
```

## 4. Telling an installation to trust a key

There are two places, both holding **public** keys only.

1. **Platform operator (host configuration).** In `appsettings` or environment variables, `Ecp:Packs:TrustedKeys:0:KeyId`, `...:PublicKeyPem`, optional `...:NotBeforeUtc`, `...:NotAfterUtc`, `...:Revoked`, or a JSON trust file named by `Ecp:Packs:TrustFile`:

   ```json
   { "keys": [ { "keyId": "publisher-2026", "algorithm": "ES256", "publicKeyPem": "-----BEGIN PUBLIC KEY-----...", "label": "Example publisher", "notAfterUtc": "2028-01-01T00:00:00Z" } ] }
   ```

   These keys are shown read-only in the Pack Manager and a tenant key cannot reuse their identifier.
2. **Tenant administrator (Pack Manager, Trusted publishers).** Requires `packs.trust.manage` and a recent sign-in. Registration and revocation are audited. A revoked key is kept in the list.

## 5. Production

Production accepts a package only if it has a valid signature from a **trusted, active, in-date key** with a supported algorithm and every hash matches. The shipped Blank and reference packs are built **unsigned** (no private key exists in the repository), so a Production operator must sign them with their own key and trust that key, or install their own signed packs, before organisations can be given a pack. Until then readiness reports *Degraded* with that reason. This is by design: it is the evidence that Production refuses unsigned packs.

| Situation | Result in Production | Code |
|---|---|---|
| No `signature.json` | Refused | `PACK-S010` |
| Signature does not verify (wrong key, altered manifest) | Refused | `PACK-S002` |
| Signed by a key nobody trusts | Refused | `PACK-S003` |
| Key revoked | Refused | `PACK-S004` |
| Key expired / not yet valid | Refused | `PACK-S005` / `PACK-S006` |
| Algorithm not supported | Refused | `PACK-S007` |
| Signature file malformed | Refused | `PACK-S008` |
| A document changed after signing | Refused | `PACK-H001` |

In Demo and Test, an unsigned pack is accepted only when **all** hold: the manifest says `synthetic: true`, its signature requirement is `AllowUnsignedSynthetic`, the environment setting `Ecp:Packs:AllowUnsignedSyntheticPacks` is on, and the mode is not Production. The warning `PACK-S009` is shown wherever the pack appears.

## 6. Key custody (operator responsibility)

Out of scope for this repository, and the owner's decision: generate keys offline or in an HSM, restrict signing to a release pipeline with a protected secret, rotate on a schedule (register the new public key, sign new versions with it, revoke the old key when no longer needed), keep an offline backup of the private key, and record who may sign. CI proves the path with a throw-away key that exists only in the job.

## 7. Audit

Every upload, verification outcome, install, key registration and key revocation is recorded in the audit history with the actor, outcome and a sanitised detail (outcome, hash, key identifier, algorithm; never key material or signature bytes).
