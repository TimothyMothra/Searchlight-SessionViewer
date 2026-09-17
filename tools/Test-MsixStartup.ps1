[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Msix-Common.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$template = Join-Path $repo 'src\Searchlight\Package.appxmanifest.template'
$root = Join-Path ([IO.Path]::GetTempPath()) "Searchlight-msix-startup-test-$([guid]::NewGuid())"
[IO.Directory]::CreateDirectory($root) | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection containing '$Message'."
}

function New-TestPackage([string]$Architecture, [bool]$Startup, [string]$Assembly, [string]$Name = 'Searchlight.Test') {
    [xml]$manifest = Get-Content $template -Raw
    Set-MsixStartupTask $manifest $Startup 'Searchlight'
    $manifest.Package.Identity.SetAttribute('Name', $Name)
    $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $Architecture)
    $path = Join-Path $root "$([guid]::NewGuid()).msix"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('AppxManifest.xml').Open())
        try { $writer.Write($manifest.OuterXml) }
        finally { $writer.Dispose() }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Assembly, 'Searchlight.dll')
        foreach ($native in @('Searchlight.exe', 'coreclr.dll', 'e_sqlite3.dll')) {
            $writer = [IO.BinaryWriter]::new($zip.CreateEntry($native).Open())
            try {
                # ASSUMPTION: inert header-only native fixtures exercise architecture
                # validation, not Windows deployment or execution.
                $bytes = [byte[]]::new(128)
                [BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($bytes, 0)
                [BitConverter]::GetBytes([int]64).CopyTo($bytes, 60)
                [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes, 64)
                $machine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xaa64 }
                [BitConverter]::GetBytes([uint16]$machine).CopyTo($bytes, 68)
                $writer.Write($bytes)
            }
            finally { $writer.Dispose() }
        }
        foreach ($file in @('resources.pri', 'Microsoft.UI.Xaml.dll', 'hostfxr.dll')) {
            [void]$zip.CreateEntry($file)
        }
    }
    finally { $zip.Dispose() }
    return $path
}

function Test-Package([string]$Path) {
    $info = Get-MsixManifest $Path
    & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') -Path $Path -ExpectedName $info.Name `
        -ExpectedPublisher $info.Publisher -ExpectedVersion $info.Version -ExpectedArchitecture $info.Architecture
}

try {
    # Evaluate the real build's selection and MSBuild arguments, without invoking
    # its version allocator, SDK tools, signing, or artifact generation.
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Build-Msix.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $selection = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$startupEnabled'
    }, $true)
    $argumentsNode = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$arguments'
    }, $true)
    if (-not $selection -or -not $argumentsNode) { throw 'Missing build capability/argument wiring.' }
    $projectRoot = Join-Path $repo 'src\Searchlight'
    $Architecture = 'x64'; $Configuration = 'Release'; $BuildName = '2026.09.17.01'
    $manifestPath = 'fixture-manifest'; $assets = 'fixture-assets'; $output = 'fixture-output'
    foreach ($case in @(
        @{ Channel = 'Production'; ForBundle = $false; Expected = $true },
        @{ Channel = 'Production'; ForBundle = $true; Expected = $false },
        @{ Channel = 'Dev'; ForBundle = $false; Expected = $false },
        @{ Channel = 'Dev'; ForBundle = $true; Expected = $false }
    )) {
        $Channel = $case.Channel; $ForBundle = $case.ForBundle
        $startupEnabled = & ([scriptblock]::Create($selection.Right.Extent.Text))
        if ($startupEnabled -ne $case.Expected) { throw 'Build startup selection is incorrect.' }
        $arguments = & ([scriptblock]::Create($argumentsNode.Right.Extent.Text))
        if ("-p:SearchlightStartupTaskEnabled=$($case.Expected.ToString().ToLowerInvariant())" -notin $arguments) {
            throw 'Build does not pass the selected startup capability to MSBuild.'
        }
        [xml]$manifest = Get-Content $template -Raw
        Set-MsixStartupTask $manifest $startupEnabled 'Searchlight'
        $tasks = $manifest.SelectNodes("//*[local-name()='StartupTask' or @Category='windows.startupTask']")
        if (($tasks.Count -gt 0) -ne $case.Expected) { throw 'Manifest startup selection is incorrect.' }
        if ($startupEnabled) {
            $task = $manifest.SelectSingleNode("//*[local-name()='StartupTask']")
            if ($task.Enabled -ne 'true' -or $task.DisplayName -ne 'Searchlight') {
                throw 'Standalone Production startup defaults changed.'
            }
        }
    }
    [xml]$manifest = Get-Content $template -Raw
    $container = $manifest.SelectSingleNode("//*[local-name()='Extensions']")
    $other = $manifest.CreateElement('desktop', 'Extension', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $other.SetAttribute('Category', 'windows.fullTrustProcess')
    [void]$container.AppendChild($other)
    $otherXml = $other.OuterXml
    $capabilities = $manifest.Package.Capabilities.OuterXml
    Set-MsixStartupTask $manifest $false 'Searchlight'
    if ($other.ParentNode -ne $container -or $other.OuterXml -cne $otherXml -or
        $manifest.Package.Capabilities.OuterXml -cne $capabilities) {
        throw 'Startup omission modified unrelated manifest declarations.'
    }
    Assert-Rejected { Set-MsixStartupTask $manifest $true 'Searchlight' } 'exactly one SearchlightStartup'

    $project = Join-Path $projectRoot 'Searchlight.csproj'
    $default = dotnet msbuild $project -nologo -getProperty:SearchlightStartupTaskEnabled
    if ($LASTEXITCODE -ne 0 -or "$default".Trim() -cne 'true') { throw 'Unpackaged default startup capability must be true.' }
    [xml]$projectXml = Get-Content $project -Raw
    $entry = $projectXml.SelectSingleNode("//AssemblyMetadata[@Include='SearchlightStartupTaskEnabled']")
    if (-not $entry -or $entry.Value -cne '$(SearchlightStartupTaskEnabled)') { throw 'Missing assembly capability metadata wiring.' }

    $assemblies = @{}
    foreach ($value in @('true', 'false', 'invalid', 'missing', 'duplicate')) {
        $assembly = Join-Path $root "$value.dll"
        $attribute = if ($value -eq 'missing') { '' }
            elseif ($value -eq 'duplicate') { '[assembly: System.Reflection.AssemblyMetadata("SearchlightStartupTaskEnabled", "true")][assembly: System.Reflection.AssemblyMetadata("SearchlightStartupTaskEnabled", "false")]' }
            else { "[assembly: System.Reflection.AssemblyMetadata(`"SearchlightStartupTaskEnabled`", `"$value`")]" }
        Add-Type -TypeDefinition "$attribute public sealed class Fixture_$([guid]::NewGuid().ToString('N')) {}" -OutputAssembly $assembly
        $assemblies[$value] = $assembly
    }
    $valid = @{}
    foreach ($architecture in @('x64', 'arm64')) {
        foreach ($startup in @($true, $false)) {
            $key = $startup.ToString().ToLowerInvariant()
            $path = New-TestPackage $architecture $startup $assemblies[$key]
            $result = Test-Package $path
            if ($result.HasStartupRegistration -ne $startup) { throw 'Package capability result changed.' }
            Assert-Rejected {
                & (Join-Path $PSScriptRoot 'Test-MsixPackage.ps1') -Path $path -ExpectedName $result.Name `
                    -ExpectedPublisher $result.Publisher -ExpectedVersion $result.Version `
                    -ExpectedArchitecture $architecture -ExpectedStartupRegistration (-not $startup)
            } 'Unexpected startup registration'
            if (-not $startup) { $valid[$architecture] = $path }
            $wrong = New-TestPackage $architecture (-not $startup) $assemblies[$key]
            Assert-Rejected { Test-Package $wrong } 'assembly metadata disagrees'
        }
    }
    $dev = New-TestPackage 'x64' $false $assemblies['false'] 'TimothyMothra.Searchlight.Dev'
    $null = Test-Package $dev
    $dev = New-TestPackage 'x64' $true $assemblies['true'] 'TimothyMothra.Searchlight.Dev'
    Assert-Rejected { Test-Package $dev } 'Dev packages must be startup-free'
    foreach ($value in @('invalid', 'missing', 'duplicate')) {
        $path = New-TestPackage 'x64' $false $assemblies[$value]
        Assert-Rejected { Test-Package $path } 'exactly one boolean'
    }
    foreach ($architecture in @('x64', 'arm64')) {
        $wrong = New-TestPackage $architecture $false $assemblies['true']
        $inputs = @{ X64Package = $valid.x64; Arm64Package = $valid.arm64 }
        if ($architecture -eq 'x64') { $inputs.X64Package = $wrong } else { $inputs.Arm64Package = $wrong }
        Assert-Rejected {
            & (Join-Path $PSScriptRoot 'Bundle-Msix.ps1') @inputs -OutputPath (Join-Path $root 'never.msixbundle') -Unsigned
        } 'assembly metadata disagrees'
    }
    & (Join-Path $PSScriptRoot 'Test-MsixBundle.ps1') -X64Package $valid.x64 -Arm64Package $valid.arm64 -ValidationOnly
    if (Get-ChildItem $root -Filter *.msixbundle) { throw 'Lightweight tests unexpectedly generated a bundle.' }
    Write-Host 'PASS: standalone Production, ForBundle, Dev, unpackaged default, declaration preservation, and PE metadata agreement/rejection.'
}
finally { [IO.Directory]::Delete($root, $true) }
