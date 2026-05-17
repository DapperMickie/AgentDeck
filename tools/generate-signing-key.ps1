param(
    [Parameter(Mandatory=$true)][string]$SignerId,
    [string]$OutputDirectory = "signers.d"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$privateKey = Join-Path $OutputDirectory "$SignerId.private.pem"
$publicKey = Join-Path $OutputDirectory "$SignerId.pem"

if ((Test-Path $privateKey) -or (Test-Path $publicKey)) {
    throw "Refusing to overwrite existing key material in $OutputDirectory"
}

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out $privateKey
openssl rsa -pubout -in $privateKey -out $publicKey

Write-Host "Generated signer '$SignerId'"
Write-Host "  private: $privateKey  (keep secret; configure only on the coordinator/signing host)"
Write-Host "  public:  $publicKey   (copy to runner signers.d/ or configure Coordinator:TrustedManifestSigners)"
