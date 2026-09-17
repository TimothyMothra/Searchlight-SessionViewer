$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-MsixTools {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Install Visual Studio with WinUI/MSIX packaging tools first.' }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $vs) { throw 'A Visual Studio MSBuild installation is required.' }
    $msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $sdk = Get-ChildItem $kits -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
            (Test-Path (Join-Path $_.FullName 'x64\signtool.exe')) } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $sdk) { throw 'Install a Windows SDK containing MakeAppx and SignTool.' }
    @{
        MSBuild = $msbuild
        SignTool = Join-Path $sdk.FullName 'x64\signtool.exe'
        MakeAppx = Join-Path $sdk.FullName 'x64\makeappx.exe'
    }
}

function Invoke-MsixTool {
    param([string]$Tool, [string[]]$Arguments)
    & $Tool @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed with exit code $LASTEXITCODE." }
}

function Assert-MsixSigningCertificate {
    param([string]$Thumbprint, [string]$Publisher)
    if (-not $Thumbprint) { throw 'Supply CertificateThumbprint, or explicitly use Unsigned for inspection only.' }
    $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne $Publisher) {
        throw 'The signing certificate must have a private key and its Subject must exactly match Publisher.'
    }
    if ($certificate.NotAfter -le [datetime]::Now -or $certificate.NotBefore -gt [datetime]::Now) {
        throw 'The signing certificate is not currently valid.'
    }
}

function Invoke-MsixSigning {
    param([string]$SignTool, [string]$Path, [string]$Thumbprint, [uri]$TimestampUrl)
    $arguments = @('sign', '/fd', 'SHA256', '/sha1', $Thumbprint)
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256') }
    $arguments += $Path
    Invoke-MsixTool $SignTool $arguments
}

function Get-MsixManifest {
    param([string]$Path)
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw 'The package has no AppxManifest.xml.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Version = [version]$identity.Version
            Architecture = [string]$identity.ProcessorArchitecture
            ApplicationId = [string]$manifest.Package.Applications.Application.Id
            HasStartupRegistration = $manifest.SelectNodes("//*[local-name()='StartupTask' or @Category='windows.startupTask']").Count -gt 0
            Entries = @($zip.Entries.FullName)
        }
    }
    finally { $zip.Dispose() }
}

function Set-MsixStartupTask {
    param(
        [Parameter(Mandatory)][xml]$Manifest,
        [Parameter(Mandatory)][bool]$Enabled,
        [Parameter(Mandatory)][string]$DisplayName
    )
    $extensions = @($Manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.startupTask']"))
    $tasks = @($Manifest.SelectNodes("//*[local-name()='StartupTask']"))
    if ($extensions.Count -ne 1 -or $tasks.Count -ne 1 -or
        $tasks[0].ParentNode -ne $extensions[0] -or $tasks[0].GetAttribute('TaskId') -cne 'SearchlightStartup') {
        throw 'The manifest template must declare exactly one SearchlightStartup task inside its startup extension.'
    }
    if ($Enabled) {
        $tasks[0].SetAttribute('DisplayName', $DisplayName)
        $tasks[0].SetAttribute('Enabled', 'true')
    }
    else {
        # ASSUMPTION: omission removes the capability, not merely Enabled=false;
        # unrelated extensions and capabilities must survive unchanged.
        $container = $extensions[0].ParentNode
        [void]$container.RemoveChild($extensions[0])
        if ($container.SelectNodes('*').Count -eq 0) {
            [void]$container.ParentNode.RemoveChild($container)
        }
    }
}

function Get-MsixStartupTaskEnabled {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    # ASSUMPTION: inspect bytes only; loading a WinUI assembly would resolve host
    # dependencies and can lock package payloads. PEReader ships with PowerShell 7.
    $pe = [System.Reflection.PortableExecutable.PEReader]::new(
        $Stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
    try {
        if (-not $pe.HasMetadata) { throw 'Searchlight.dll has no managed metadata.' }
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (-not $metadata.IsAssembly) { throw 'Searchlight.dll is not a managed assembly.' }
        $values = @(
            foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $constructor = $metadata.GetMemberReference(
                    [System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                if ($constructor.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
                if ($metadata.GetString($type.Namespace) -cne 'System.Reflection' -or
                    $metadata.GetString($type.Name) -cne 'AssemblyMetadataAttribute') { continue }
                $blob = $metadata.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly metadata attribute prolog.' }
                $key = $blob.ReadSerializedString()
                $value = $blob.ReadSerializedString()
                if ($key -ceq 'SearchlightStartupTaskEnabled') { $value }
            }
        )
        $enabled = $false
        if ($values.Count -ne 1 -or -not [bool]::TryParse($values[0], [ref]$enabled)) {
            throw 'Searchlight.dll must contain exactly one boolean SearchlightStartupTaskEnabled assembly metadata value.'
        }
        return $enabled
    }
    finally { $pe.Dispose() }
}
