# Runner signing keys

Runner update manifests, workflow packs, capability catalogs, and setup catalogs should be signed with a per-deployment RSA key. The built-in `agentdeck-dev` public key is for local development only; runners refuse that signer outside Development unless `RunnerLocalSecurityPolicy:AllowDevSignerInProduction=true` is set explicitly.

## Generate a deployment key

Linux/macOS:

```bash
tools/generate-signing-key.sh my-deployment
```

Windows PowerShell:

```powershell
./tools/generate-signing-key.ps1 -SignerId my-deployment
```

This creates:

- `signers.d/my-deployment.private.pem` — private key; keep only on the coordinator/signing host.
- `signers.d/my-deployment.pem` — public key; copy to each runner's `signers.d/` directory or configure it in `Coordinator:TrustedManifestSigners`.

## Configure runners

Prefer drop-in public keys so appsettings does not need editing:

```text
<runner install>/signers.d/my-deployment.pem
```

Set the runner-local signer allow-list:

```json
{
  "RunnerLocalSecurityPolicy": {
    "TrustedSignerIds": ["my-deployment"]
  }
}
```

The runner treats coordinator policy as additive. A coordinator can require stricter checks, but cannot disable signed manifests, provenance, artifact checksums/origin checks, or signed executable catalogs below the runner-local minimums.

## Rotate keys

1. Generate the new key pair.
2. Distribute the new public key to runners under `signers.d/<new-id>.pem`.
3. Temporarily allow both signer ids in `RunnerLocalSecurityPolicy:TrustedSignerIds`.
4. Update coordinator signing config to use the new private key and signer id.
5. After all runners have accepted signed payloads from the new signer, remove the old signer id and public key.

## Sign coordinator definitions

For each coordinator-published executable definition, configure either `PrivateKeyPem` (auto-sign at startup) or a precomputed detached `Signature` with the signer id:

```json
{
  "Coordinator": {
    "DesiredWorkflowPack": {
      "SignerId": "my-deployment",
      "PrivateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"
    },
    "DesiredCapabilityCatalog": {
      "SignerId": "my-deployment",
      "PrivateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"
    },
    "DesiredSetupCatalog": {
      "SignerId": "my-deployment",
      "PrivateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"
    }
  }
}
```

Do not commit private keys. Prefer environment-specific secret configuration.
