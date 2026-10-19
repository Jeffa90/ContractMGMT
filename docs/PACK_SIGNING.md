# Signing solution packs

Status: **Implemented** for signing and verification, with a standalone tool that needs no source checkout (G-34, ADR-0038). **Key custody for Production is an external operational responsibility** and is not provided by this repository. No private key is committed (an architecture test fails the build if one appears). Nothing here declares production approval.

## 1. What is signed

The signature covers the **canonical manifest**, and the manifest lists the SHA-256 of every content document, so the signature commits to the whole package. The signing input is the ASCII text `ECP-PACK-MANIFEST-V1` and a line feed followed by the canonical manifest bytes (a domain-separation prefix, so a signature made for a package cannot be valid for anything else). The verifier re-canonicalises the manifest and refuses a manifest whose stored bytes differ, which removes signature malleability.

`signature.json` (canonical JSON):

```json
{ "algorithm": "ES256", "formatVersion": 1, "keyId": "publisher-2026", "signature": "<base64>", "signedUtc": "2026-10-20T00:00:00Z" }
```

## 2. Algorithm

Format version 1 uses **ECDSA P-256 with SHA-256** (`ES256`), 64-byte IEEE P1363 signatures, SPKI (`-----BEGIN PUBLIC KEY-----`) public keys and PKCS#8 (`-----BEGIN PRIVATE KEY-----`) private keys. It is built into .NET; the earlier plan for Ed25519 needed a new dependency because .NET 10 has no Ed25519, and the owner approved ES256 instead (ADR-0026 D3). The algorithm name is stored in `signature.json`; an unsupported algorithm is refused (`PACK-S007`), so another can be added later without a format change.

## 3. The tool, and who uses it

Signing is a **technical** task for the **Pack Publisher**. A business administrator never signs a pack, and the application cannot: it holds no private key, receives none and has no browser signing screen.

The tool is `ecp-pack`, shipped as a .NET tool package (`Ecp.PackTool.<version>.nupkg`, built by CI and uploaded as the build artefact `ecp-pack-tool`, with a SHA-256 list; a release process that attaches it to a release is the owner's decision). It needs the .NET runtime on the Pack Publisher's machine. It does **not** need the repository, the SDK sources or an IDE.

```
dotnet tool install Ecp.PackTool --tool-path ./ecp-tools --add-source ./folder-holding-the-nupkg
./ecp-tools/ecp-pack version
```

(Technical task: the install line above is run by the Pack Publisher, not by a business administrator.)

### Roles

| Who | Does | Never does |
|---|---|---|
| Pack Publisher (technical) | Builds from a pack source folder, validates, signs with a key held outside the repository, verifies, writes the sanitised report; hands over signed packs and the **public** key only | Hands over the private key, commits it, pastes it into a ticket |
| Tenant Administrator (permission `packs.trust.manage`, recent sign-in) | Registers the publisher's **public** key under Trusted publishers in the Pack Manager | Signs |
| Platform operator (System Administrator) | May instead trust a public key by host configuration (`Ecp:Packs:TrustFile`) | Holds the publisher's private key |
| Business Administrator | Assigns an installed, trusted pack to an organisation | Runs the tool |

### Key references

The private key is referenced, never typed on a command line and never printed: `--key-ref env:VARIABLE` (the variable holds the PEM text, which is how a pipeline supplies a secret) or `--key-ref file:/path/to/private.pem` (a file outside the repository, readable only by the publisher). `--key-file` and `--key-env` remain as shorter spellings. A key held in an HSM or Key Vault can be used only if the publisher's own pipeline supplies it through one of those references; signing directly with an HSM or Key Vault is **Future**. The tool writes no key to its output, its report or its error messages, and the build tests that.

### Commands

```
ecp-pack keygen --out-private publisher.key --out-public publisher.pub.pem        # new pair; refuses to overwrite; creates the folders
ecp-pack build --source my-pack --out out/my-pack-1.0.0.ecpkg                     # deterministic: the same source gives the same bytes
ecp-pack validate --package out/my-pack-1.0.0.ecpkg
ecp-pack sign --package out/my-pack-1.0.0.ecpkg --out out/my-pack-1.0.0.signed.ecpkg --key-id publisher-2026 --key-ref file:publisher.key
ecp-pack verify --package out/my-pack-1.0.0.signed.ecpkg --public-key publisher.pub.pem --key-id publisher-2026 --production
ecp-pack report --package out/my-pack-1.0.0.signed.ecpkg --public-key publisher.pub.pem --key-id publisher-2026 --out out/report.json
```

`report` is the sanitised hand-over record: pack identity, package and content hashes, validation findings and signature facts. It holds no pack content, no key, no signature bytes and no path from the publisher's machine.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | The package or request is not acceptable (validation failed, verification failed, unsigned package refused for Production, a key file would be overwritten) |
| 2 | Usage error (missing or unknown option); the message says which |
| 3 | A file or key could not be read or written (a missing package, an unwritable folder) or the key reference could not be used (not set, not found, not a usable ES256 key); the message never includes the key |

### What is verified

`PackToolArtefactTests` packs the tool, installs **that artefact alone** from a feed containing nothing else, and runs build twice (byte-identical), validate, report, keygen, sign by file and by environment reference, verify for Production, inspect, an unsigned package refused, a wrong key refused, a tampered package refused, the exit codes and the absence of key material in every output. CI runs it against the packed artefact. **Not Verified:** a self-contained single-file build for a machine without the .NET runtime (not built in this release), and signing with a key held in an HSM or Key Vault.

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
