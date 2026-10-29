[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Register,
    [switch]$IncludeMachine,
    [Parameter(DontShow = $true)][string]$InspectAssembly
)

$ErrorActionPreference = 'Stop'
if ($Register -and -not $WhatIfPreference -and $env:CODEX_WINDOWS_SANDBOX_PACKAGE_FAMILY) {
    throw 'Run registration from ordinary Windows PowerShell or from Rhino, not the Codex Windows sandbox. Its registry view can differ from the one Rhino uses. No registrations changed.'
}
if ($InspectAssembly) {
    # Inspect each same-named runtime variant in a fresh process without executing plug-in code.
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($InspectAssembly)
    $sdk = @($assembly.GetReferencedAssemblies() | Where-Object Name -EQ 'RhinoCommon')
    if ($sdk.Count -ne 1) { throw "Cannot identify Rhino SDK: $InspectAssembly" }
    @{ Host = $sdk[0].Version.Major; Version = $assembly.GetName().Version.ToString(); Name = $assembly.GetName().Name } | ConvertTo-Json -Compress
    exit 0
}
$root = $PSScriptRoot
$project = Get-ChildItem -LiteralPath $root -Filter '*.csproj' -File | Select-Object -First 1
if (-not $project) { throw 'No plug-in project found.' }
$name = $project.BaseName
$separateIdentities = $name -eq 'vAltGrShortcutFilter'
$name8 = if ($separateIdentities) { "$name.Rhino8" } else { $name }
$name9 = if ($separateIdentities) { "$name.Rhino9" } else { $name }
$variants = @(
    @{ Host = 8; Runtime = 'net7.0-windows'; Name = $name8 },
    @{ Host = 8; Runtime = 'net10.0-windows'; Name = $name8 },
    @{ Host = 9; Runtime = 'net10.0-windows'; Name = $name9 }
)
$version = $null
foreach ($variant in $variants) {
    $variant.Directory = Join-Path $root "bin/Release/Rhino$($variant.Host)/$($variant.Runtime)"
    $variant.Dll = Join-Path $variant.Directory "$($variant.Name).dll"
    $json = & powershell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -InspectAssembly $variant.Dll
    if ($LASTEXITCODE) { throw "Cannot inspect assembly: $($variant.Dll)" }
    $variant.Identity = $json | ConvertFrom-Json
    if ($variant.Identity.Host -ne $variant.Host -or $variant.Identity.Name -ne $variant.Name) { throw "Wrong Rhino SDK/identity: $($variant.Dll)" }
    if ($null -eq $version) { $version = $variant.Identity.Version }
    if ($version -ne $variant.Identity.Version) { throw 'All three installed runtime variants must have the same build version.' }
    if ($separateIdentities) {
        $ids = [regex]::Matches([IO.File]::ReadAllText((Join-Path $root 'PluginBuildIdentity.cs')), 'Id\s*=\s*"([0-9a-fA-F-]{36})"')
        $index = if ($variant.Host -eq 8) { 0 } else { 1 }
        $variant.Id = $ids[$index].Groups[1].Value
    } else {
        $match = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'Properties/AssemblyInfo.cs')), '\bGuid\s*\(\s*"([0-9a-fA-F-]{36})"')
        $variant.Id = $match.Groups[1].Value
    }
    if (-not $variant.Id -or [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($variant.Dll)).IndexOf($variant.Id, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "Missing/mismatched plug-in GUID: $($variant.Dll)" }
}

if ($Register) {
    $hives = @{ CurrentUser = [Microsoft.Win32.Registry]::CurrentUser; LocalMachine = [Microsoft.Win32.Registry]::LocalMachine }
    $updates = @()
    foreach ($major in 8, 9) {
        $variant = $variants | Where-Object Host -EQ $major | Select-Object -First 1
        $base = "Software\McNeel\Rhinoceros\$major.0\Plug-ins\$($variant.Id)"
        $hiveNames = if ($IncludeMachine) { @('CurrentUser', 'LocalMachine') } else { @('CurrentUser') }
        foreach ($hiveName in $hiveNames) {
          # The root FileName requests startup registration; PlugIn/FileName is only Rhino's cached runtime path.
          foreach ($value in @(@{ Subkey = ''; Name = 'FileName' }, @{ Subkey = 'PlugIn'; Name = 'FileName' }, @{ Subkey = ''; Name = 'RuiFile' })) {
            $path = if ($value.Subkey) { "$base\$($value.Subkey)" } else { $base }
            $key = $hives[$hiveName].OpenSubKey($path)
            if (-not $key) { continue }
            try {
                $old = $key.GetValue($value.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $new = if ($value.Name -eq 'RuiFile') { Join-Path $variant.Directory "$name.rui" } else { $variant.Dll }
                if (-not (Test-Path -LiteralPath $new)) { continue }
                if ($old -cne $new) {
                    $updates += [pscustomobject]@{ Hive = $hiveName; Key = $path; Value = $value.Name; Old = $old; Kind = if ($null -ne $old) { $key.GetValueKind($value.Name).ToString() } else { 'String' }; New = $new }
                }
            } finally { $key.Dispose() }
          }
        }
    }
    foreach ($update in $updates) {
        Write-Verbose "Registration request: $($update.Hive)\$($update.Key) [$($update.Value)] -> $($update.New)"
    }
    if ($updates.Count -and $PSCmdlet.ShouldProcess('Existing user/machine Rhino 8/9 plug-in registrations', "Register $name directly from Release")) {
        # Check access to every existing key before changing any registration.
        foreach ($update in $updates) {
            $key = $hives[$update.Hive].OpenSubKey($update.Key, $true)
            if (-not $key) { throw "Cannot update $($update.Hive)\$($update.Key). No registrations changed." }
            $key.Dispose()
        }
        $backupRoot = Join-Path $root 'backups/registration'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $updates | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')) -Encoding UTF8
        try {
            foreach ($update in $updates) {
                $key = $hives[$update.Hive].OpenSubKey($update.Key, $true)
                try { $key.SetValue($update.Value, $update.New, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
            }
        } catch {
            foreach ($update in $updates) {
                $key = $hives[$update.Hive].OpenSubKey($update.Key, $true)
                try { if ($null -eq $update.Old) { $key.DeleteValue($update.Value, $false) } else { $key.SetValue($update.Value, $update.Old, [Microsoft.Win32.RegistryValueKind]$update.Kind) } } finally { $key.Dispose() }
            }
            throw
        }
    }
}
Write-Host "$name ${version}: verified Rhino 8 (.NET 7/10) and Rhino 9 (.NET 10) Release installations."
