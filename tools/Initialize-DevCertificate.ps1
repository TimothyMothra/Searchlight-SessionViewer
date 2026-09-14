[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([switch]$Trust)
$ErrorActionPreference = 'Stop'
$subject = 'CN=Searchlight Development'

if ($Trust) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Trust installation requires an elevated PowerShell. No certificate was created or trusted.'
        }
    }
    finally { $identity.Dispose() }
}

$certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -ceq $subject -and $_.HasPrivateKey -and
        $_.NotAfter -gt [datetime]::Now.AddDays(7) -and $_.NotBefore -le [datetime]::Now } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    if (-not $PSCmdlet.ShouldProcess($subject, 'Create a non-exportable development code-signing key')) { return }
    # ASSUMPTION: Dev signing stays on this machine. Never export its private key
    # or use a self-signed Dev identity as a production signing default.
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -FriendlyName 'Searchlight Development' -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -NotAfter ([datetime]::Now.AddYears(1))
}

if ($Trust -and -not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)")) {
    if (-not $PSCmdlet.ShouldProcess($certificate.Thumbprint, 'Trust this public certificate in LocalMachine\TrustedPeople')) { return }
    $directory = Join-Path $env:LOCALAPPDATA 'Searchlight.Build\signing'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $publicPath = Join-Path $directory "$($certificate.Thumbprint).cer"
    Export-Certificate -Cert $certificate -FilePath $publicPath | Out-Null
    Import-Certificate -FilePath $publicPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}

[pscustomobject]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    NotAfter = $certificate.NotAfter
    Trusted = Test-Path "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
}
