[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Get-ChildItem -LiteralPath $root -Filter '*.csproj' -File | Select-Object -First 1
if (-not $project) { throw 'No plug-in project found.' }
$name = $project.BaseName
$name8 = if ($name -eq 'vAltGrShortcutFilter') { "$name.Rhino8" } else { $name }
$name9 = if ($name -eq 'vAltGrShortcutFilter') { "$name.Rhino9" } else { $name }
$variants = @(
    @{ Host = 8; Runtime = 'net7.0-windows'; Assembly = $name8 },
    @{ Host = 8; Runtime = 'net10.0-windows'; Assembly = $name8 },
    @{ Host = 9; Runtime = 'net10.0-windows'; Assembly = $name9 }
)
foreach ($variant in $variants) {
    $variant.Directory = Join-Path $root "bin/Release/Rhino$($variant.Host)/$($variant.Runtime)"
    $variant.Dll = Join-Path $variant.Directory "$($variant.Assembly).dll"
    $json = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'Register.ps1') -InspectAssembly $variant.Dll
    if ($LASTEXITCODE) { throw "Cannot inspect release assembly: $($variant.Dll)" }
    $identity = $json | ConvertFrom-Json
    if ($identity.Host -ne $variant.Host -or $identity.Name -ne $variant.Assembly -or $identity.Version -ne $Version) {
        throw "Release tag, Rhino SDK or assembly identity mismatch: $($variant.Dll)"
    }
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ('package-stage-' + [Guid]::NewGuid().ToString('N'))
$assets = @()
# Ship repository defaults and documentation, never deployed profiles, logs or traces.
$companions = @("$name.config.json", "${name}Options.json", "Toolbar/$name.rui", "Help/${name}Help.html", 'ThirdPartyNotices.md', 'README.md', 'LICENSE', 'LICENSE.md', 'LICENSE.txt')
foreach ($variant in $variants) {
    $destination = Join-Path $stage "Rhino$($variant.Host)/$($variant.Runtime)"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $variant.Dll -Destination $destination
    $deps = Join-Path $variant.Directory "$($variant.Assembly).deps.json"
    if (Test-Path -LiteralPath $deps) { Copy-Item -LiteralPath $deps -Destination $destination }
    foreach ($relative in $companions) {
        $source = Join-Path $root $relative
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $destination }
    }
    $asset = Join-Path $output "$name-Rhino$($variant.Host)-$($variant.Runtime).dll"
    Copy-Item -LiteralPath $variant.Dll -Destination $asset -Force
    $assets += $asset
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($major in 8, 9) {
    $zip = Join-Path $output "$name-Rhino$major-$Version.zip"
    if (Test-Path -LiteralPath $zip) { [IO.File]::Delete($zip) }
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $stage "Rhino$major"), $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $assets += $zip
}
$toolbar = Join-Path $root "Toolbar/$name.rui"
if (Test-Path -LiteralPath $toolbar) {
    $asset = Join-Path $output "$name.rui"
    Copy-Item -LiteralPath $toolbar -Destination $asset -Force
    $assets += $asset
}
@{ Version = $Version; Assets = $assets } | ConvertTo-Json -Depth 4
