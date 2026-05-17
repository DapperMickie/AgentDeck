#!/usr/bin/env bash
set -euo pipefail

signer_id="${1:-}"
out_dir="${2:-signers.d}"

if [[ -z "$signer_id" ]]; then
  echo "usage: $0 <signer-id> [output-directory]" >&2
  exit 2
fi

mkdir -p "$out_dir"
private_key="$out_dir/$signer_id.private.pem"
public_key="$out_dir/$signer_id.pem"

if [[ -e "$private_key" || -e "$public_key" ]]; then
  echo "refusing to overwrite existing key material in $out_dir" >&2
  exit 1
fi

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$private_key"
openssl rsa -pubout -in "$private_key" -out "$public_key"
chmod 600 "$private_key"
chmod 644 "$public_key"

echo "Generated signer '$signer_id'"
echo "  private: $private_key  (keep secret; configure only on the coordinator/signing host)"
echo "  public:  $public_key   (copy to runner signers.d/ or configure Coordinator:TrustedManifestSigners)"
