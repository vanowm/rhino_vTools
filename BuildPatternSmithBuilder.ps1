param(
    [string]$SdkDirectory = $env:PATTERNSMITH_SDK_DIRECTORY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Builder inputs and classic CLR output conventions; paths stay repository-relative.
$requiredVersion = '10.2.17.873' # Exact vendor assembly version; no cross-version schema substitution.
$requiredToken = 'b1742680da7848dc' # Vendor strong-name identity, not a substitute for trusted installation media.
$vendorNames = @('Autometrix.Core', 'Autometrix.Presentation.Core.Data', 'Autometrix.Conversion')
$source = Join-Path $PSScriptRoot 'Commands\vExportPatternSmith.cs'
$resource = Join-Path $PSScriptRoot 'Resources\PatternSmith\Builder.bin'
$hashFile = Join-Path $PSScriptRoot 'Resources\PatternSmith\Builder.source.sha256'

function Get-Sha256 {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

$sourceHash = Get-Sha256 $source

function Test-SdkDirectory {
    param([string]$Directory)
    foreach ($name in $vendorNames) {
        $path = Join-Path $Directory ($name + '.dll')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        try {
            $identity = [Reflection.AssemblyName]::GetAssemblyName($path)
            $token = [BitConverter]::ToString($identity.GetPublicKeyToken()).Replace('-', '').ToLowerInvariant()
            if ($identity.Name -cne $name -or $identity.Version.ToString() -ne $requiredVersion -or $token -ne $requiredToken) {
                return $false
            }
        } catch [System.BadImageFormatException] { return $false }
        catch [System.IO.IOException] { return $false }
    }
    return $true
}

try {
    $sdk = $null
    if (-not [string]::IsNullOrWhiteSpace($SdkDirectory)) {
        $candidate = [IO.Path]::GetFullPath($SdkDirectory)
        if (-not (Test-SdkDirectory $candidate)) {
            throw "The supplied SDK directory must contain the signed Autometrix SDK $requiredVersion assemblies."
        }
        $sdk = $candidate
    } else {
        $roots = @([Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86')) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
        $candidates = foreach ($root in $roots) {
            Join-Path $root ('AutometrixSDK' + $requiredVersion)
            $vendorRoot = Join-Path $root 'Autometrix'
            if (Test-Path -LiteralPath $vendorRoot -PathType Container) {
                Get-ChildItem -LiteralPath $vendorRoot -Directory |
                    Where-Object { $_.Name -like 'PatternSmith*' -or $_.Name -eq ('AutometrixSDK' + $requiredVersion) } |
                    Select-Object -ExpandProperty FullName
            }
        }
        foreach ($candidate in ($candidates | Sort-Object -Unique)) {
            if (Test-SdkDirectory $candidate) { $sdk = $candidate; break }
        }
    }

    if ($null -eq $sdk) {
        if ((Test-Path -LiteralPath $resource -PathType Leaf) -and
            (Test-Path -LiteralPath $hashFile -PathType Leaf) -and
            ([IO.File]::ReadAllText($hashFile).Trim() -match '^[0-9a-fA-F]{64}$') -and
            ([IO.File]::ReadAllText($hashFile).Trim() -eq $sourceHash)) {
            Write-Host "PatternSmith SDK $requiredVersion not installed; using the source-matched checked-in builder."
            exit 0
        }
        throw "The embedded PatternSmith builder is missing or stale. Install Autometrix SDK $requiredVersion (or set PATTERNSMITH_SDK_DIRECTORY) and regenerate it before building vTools."
    }

    # Use Roslyn's csc, but reference only classic .NET Framework assemblies. The
    # Windows-inbox C# 5 compiler cannot honor /deterministic and must not be used.
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $installed = @(& $dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate the installed .NET SDK compilers.' }
    $compilers = foreach ($line in $installed) {
        if ($line -match '^(\S+)\s+\[(.+)\]$') {
            $version = $Matches[1]
            $path = Join-Path (Join-Path $Matches[2] $version) 'Roslyn\bincore\csc.dll'
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                [pscustomobject]@{ Version = [version]($version.Split('-')[0]); Path = $path }
            }
        }
    }
    $compiler = $compilers | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $compiler) { throw 'A local .NET SDK with deterministic Roslyn csc is required to regenerate the builder.' }
    $framework = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
    if (-not (Test-Path -LiteralPath $framework -PathType Container)) {
        $framework = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319'
    }
    $references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Xml.dll', 'System.Xml.Linq.dll',
        'System.IO.Compression.dll', 'System.Runtime.Serialization.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll') |
        ForEach-Object {
            $path = Join-Path $framework $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing classic .NET Framework reference: $path" }
            $path
        }
    $references += $vendorNames | ForEach-Object { Join-Path $sdk ($_ + '.dll') }

    $stageRoot = Join-Path $PSScriptRoot 'obj\PatternSmithBuilder'
    $stage = Join-Path $stageRoot ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    try {
        $executable = Join-Path $stage 'Builder.exe'
        $arguments = @($compiler.Path, '/nologo', '/noconfig', '/nostdlib+', '/target:exe', '/platform:anycpu',
            '/deterministic+', '/optimize+', '/debug-', '/langversion:5', '/utf8output', '/define:PATTERNSMITH_BUILDER',
            ('/pathmap:' + $PSScriptRoot + '=/_/vTools,' + $sdk + '=/_/AutometrixSDK,' + $framework + '=/_/NETFramework'),
            ('/out:' + $executable))
        $arguments += $references | ForEach-Object { '/reference:' + $_ }
        $arguments += $source
        & $dotnet @arguments
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw 'PatternSmith builder compilation failed; the existing resource was not replaced.'
        }
        if ((Get-Sha256 $source) -ne $sourceHash) {
            throw 'The PatternSmith command source changed during compilation; retry after edits finish.'
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resource)) | Out-Null
        $builtHash = Get-Sha256 $executable
        if (-not (Test-Path -LiteralPath $resource -PathType Leaf) -or
            (Get-Sha256 $resource) -ne $builtHash) {
            Copy-Item -LiteralPath $executable -Destination $resource -Force
        }
        if (-not (Test-Path -LiteralPath $hashFile -PathType Leaf) -or [IO.File]::ReadAllText($hashFile).Trim() -ne $sourceHash) {
            [IO.File]::WriteAllText($hashFile, $sourceHash + "`r`n", [Text.UTF8Encoding]::new($false))
        }
        Write-Host "Built the embedded classic .NET Framework PatternSmith writer with SDK $requiredVersion."
    } finally {
        $checkedRoot = [IO.Path]::GetFullPath($stageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $checkedStage = [IO.Path]::GetFullPath($stage)
        if ($checkedStage.StartsWith($checkedRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $checkedStage -PathType Container)) {
            Remove-Item -LiteralPath $checkedStage -Recurse -Force
        }
    }
} catch {
    Write-Error $_
    exit 1
}
