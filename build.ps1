param(
    [switch]$Publish,
    [switch]$ComposeOnly,
    [string]$Message
)

Set-Location $PSScriptRoot

$projectFile = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.csproj' -File | Select-Object -First 1
if (-not $projectFile) {
    Write-Error "No project file found in $PSScriptRoot."
    exit 1
}

$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectFile.Name)
$gitDirectory = Join-Path $PSScriptRoot '.git'
$pendingFile = Join-Path $PSScriptRoot '.git\release-pending-message.txt'
$releaseDllPaths = @(
    "bin\Release\net7.0-windows\$projectName.dll",
    "bin\Release\net10.0-windows\$projectName.dll"
)
$helpGeneratorPath = Join-Path $PSScriptRoot 'GenerateCommandHelp.ps1'
$helpSourcePath = Join-Path $PSScriptRoot 'README.md'
$helpTemplatePath = Join-Path $PSScriptRoot 'Help\vToolsHelp.template.html'
$generatedHelpPath = Join-Path $PSScriptRoot 'Help\vToolsHelp.html'
$helpOutputPaths = @(
    'bin\Release\net7.0-windows\vToolsHelp.html',
    'bin\Release\net10.0-windows\vToolsHelp.html',
    'bin\Debug\net7.0-windows\vToolsHelp.html',
    'bin\Debug\net10.0-windows\vToolsHelp.html'
)

function Normalize-ChangedTextFiles {
    $changedPaths = @(
        & git diff --name-only --diff-filter=ACMRTUXB
        & git diff --cached --name-only --diff-filter=ACMRTUXB
        & git ls-files --others --exclude-standard
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate changed files for CRLF normalization.'
    }

    $normalizedCount = 0
    foreach ($relativePath in $changedPaths) {
        $attribute = & git check-attr eol -- $relativePath
        if ($LASTEXITCODE -ne 0 -or $attribute -notmatch ':\s+eol:\s+crlf$') {
            continue
        }

        $fullPath = Join-Path $PSScriptRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($fullPath)
        if ($bytes.Length -eq 0 -or $bytes -contains 0) {
            continue
        }

        $hasUtf8Bom = $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF
        $offset = if ($hasUtf8Bom) { 3 } else { 0 }
        $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
        try {
            $text = $strictUtf8.GetString($bytes, $offset, $bytes.Length - $offset)
        } catch {
            continue
        }

        $normalized = [System.Text.RegularExpressions.Regex]::Replace(
            $text,
            '\r\n|\r|\n',
            "`r`n")
        if ([string]::Equals(
                $text,
                $normalized,
                [System.StringComparison]::Ordinal)) {
            continue
        }

        $encoding = New-Object System.Text.UTF8Encoding($hasUtf8Bom)
        [System.IO.File]::WriteAllText($fullPath, $normalized, $encoding)
        $normalizedCount++
    }

    if ($normalizedCount -gt 0) {
        Write-Host "Normalized $normalizedCount changed text file(s) to CRLF." -ForegroundColor Green
    }
}

function Update-CommandHelp {
    foreach ($requiredPath in @($helpGeneratorPath, $helpSourcePath, $helpTemplatePath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Command-help source not found: $requiredPath"
        }
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File $helpGeneratorPath `
        -SourcePath $helpSourcePath `
        -TemplatePath $helpTemplatePath `
        -OutputPath $generatedHelpPath
    if ($LASTEXITCODE -ne 0) {
        throw "Command-help generation failed with exit code $LASTEXITCODE."
    }

    foreach ($relativeOutputPath in $helpOutputPaths) {
        $outputPath = Join-Path $PSScriptRoot $relativeOutputPath
        $outputDirectory = Split-Path -Parent $outputPath
        if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
            continue
        }

        Copy-Item -LiteralPath $generatedHelpPath -Destination $outputPath -Force -ErrorAction Stop
    }
}

function Test-FileLocked {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        return $false
    } catch [System.IO.IOException] {
        return $true
    } catch [System.UnauthorizedAccessException] {
        return $true
    } finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Resolve-ReleaseOutputDirectory {
    param([Parameter(Mandatory = $true)][string]$RelativeDllPath)

    $releaseRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot 'bin\Release')).TrimEnd('\') + '\'
    $outputDirectory = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot (Split-Path -Parent $RelativeDllPath)))
    if (-not $outputDirectory.StartsWith(
            $releaseRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to manage Release output outside '$releaseRoot': $outputDirectory"
    }
    return $outputDirectory
}

function New-ReleaseOutputSnapshot {
    param([Parameter(Mandatory = $true)][string[]]$DllPaths)

    $snapshotRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("$projectName-release-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $snapshotRoot -Force -ErrorAction Stop | Out-Null

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($relativeDllPath in $DllPaths) {
        $outputDirectory = Resolve-ReleaseOutputDirectory -RelativeDllPath $relativeDllPath
        $snapshotDirectory = Join-Path $snapshotRoot ([System.IO.Path]::GetFileName($outputDirectory))
        $existed = Test-Path -LiteralPath $outputDirectory -PathType Container
        if ($existed) {
            New-Item -ItemType Directory -Path $snapshotDirectory -Force -ErrorAction Stop | Out-Null
            Get-ChildItem -LiteralPath $outputDirectory -Force |
                Copy-Item -Destination $snapshotDirectory -Recurse -Force -ErrorAction Stop
        }
        $entries.Add([pscustomobject]@{
            OutputDirectory = $outputDirectory
            SnapshotDirectory = $snapshotDirectory
            Existed = $existed
        })
    }

    return [pscustomobject]@{
        Root = $snapshotRoot
        Entries = $entries.ToArray()
    }
}

function Restore-ReleaseOutputSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)

    foreach ($entry in $Snapshot.Entries) {
        $outputDirectory = [System.IO.Path]::GetFullPath($entry.OutputDirectory)
        $releaseRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot 'bin\Release')).TrimEnd('\') + '\'
        if (-not $outputDirectory.StartsWith(
                $releaseRoot,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to restore Release output outside '$releaseRoot': $outputDirectory"
        }

        if (Test-Path -LiteralPath $outputDirectory) {
            Remove-Item -LiteralPath $outputDirectory -Recurse -Force -ErrorAction Stop
        }
        if (-not $entry.Existed) {
            continue
        }

        New-Item -ItemType Directory -Path $outputDirectory -Force -ErrorAction Stop | Out-Null
        Get-ChildItem -LiteralPath $entry.SnapshotDirectory -Force |
            Copy-Item -Destination $outputDirectory -Recurse -Force -ErrorAction Stop
    }
}

function Remove-ReleaseOutputSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)

    $temporaryRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $snapshotRoot = [System.IO.Path]::GetFullPath($Snapshot.Root)
    if (-not $snapshotRoot.StartsWith(
            $temporaryRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove snapshot outside '$temporaryRoot': $snapshotRoot"
    }
    if (Test-Path -LiteralPath $snapshotRoot) {
        Remove-Item -LiteralPath $snapshotRoot -Recurse -Force -ErrorAction Stop
    }
}

if (-not $Publish -and -not $ComposeOnly -and -not [string]::IsNullOrWhiteSpace($Message)) {
    Write-Error 'The -Message option is only valid with -Publish or -ComposeOnly.'
    exit 1
}

if ($Publish -or $ComposeOnly) {
    if (-not (Test-Path -LiteralPath $gitDirectory -PathType Container)) {
        Write-Error 'Publishing requires a Git working copy. Run build.ps1 without -Publish for a standalone build.'
        exit 1
    }

    try {
        Normalize-ChangedTextFiles
    } catch {
        Write-Error "Unable to normalize changed text files: $($_.Exception.Message)"
        exit 1
    }

    $messageWasSupplied = -not [string]::IsNullOrWhiteSpace($Message)
    $messageWasPrompted = $false

    if ($messageWasSupplied) {
        $summary = $Message.Trim()
    } elseif (Test-Path -LiteralPath $pendingFile) {
        $summary = [System.IO.File]::ReadAllText($pendingFile).Trim()
        Write-Host "Using existing semantic pending message: $summary" -ForegroundColor Green
    } elseif ($ComposeOnly) {
        Write-Error 'A semantic release message is required. Supply it with -Message.'
        exit 1
    } else {
        $promptedMessage = Read-Host 'Describe net plug-in and build changes relative to HEAD; omit intermediate changes that were later reverted'
        $summary = if ($null -eq $promptedMessage) { '' } else { $promptedMessage.Trim() }
        $messageWasPrompted = $true
    }

    $genericPart = '(?i)(^|;\s*)(?:add commands?:\s*[^;]+|update commands?:\s*[^;]+|[^:;]+:\s*update|build:\s*(?:align release workflow|publish release binary)|maintenance:\s*apply project updates)(?=\s*(?:;|$))'
    if ([string]::IsNullOrWhiteSpace($summary) -or $summary.Length -lt 20 -or $summary -match $genericPart) {
        Write-Error 'The release message must describe the actual behavior changed; category-only summaries such as "panel: update" are rejected.'
        exit 1
    }

    if ($summary -match '(?i)\b[^\s;]+\.py\b') {
        Write-Error 'Release messages must describe plug-in behavior without naming source script files.'
        exit 1
    }

    if ($messageWasSupplied -or $messageWasPrompted) {
        $encoding = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($pendingFile, $summary, $encoding)
        Write-Host "Saved semantic pending message: $summary" -ForegroundColor Green
    }

    if ($ComposeOnly) { exit 0 }
}

try {
    Update-CommandHelp
} catch {
    Write-Error "Unable to update command help: $($_.Exception.Message)"
    exit 1
}

$sourceFiles = Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Filter '*.cs' |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj|logs|backups|\.git)\\' -and
        $_.Name -notlike '*.Generated*'
    }
$latestSource = $sourceFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$expectedVersion = if ($latestSource) {
    $latestSource.LastWriteTime.ToString('yy.M.d.Hmm')
} else {
    [DateTime]::Now.ToString('yy.M.d.Hmm')
}
$releaseOutputsCurrent = $null -ne $latestSource

foreach ($relativePath in $releaseDllPaths) {
    $dllPath = Join-Path $PSScriptRoot $relativePath
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        $releaseOutputsCurrent = $false
        break
    }

    $dll = Get-Item -LiteralPath $dllPath
    $dllVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll.FullName).FileVersion
    if ($dllVersion -ne $expectedVersion -or $dll.LastWriteTimeUtc -lt $latestSource.LastWriteTimeUtc) {
        $releaseOutputsCurrent = $false
        break
    }
}

if ($releaseOutputsCurrent) {
    if ($Publish) {
        Write-Host "$projectName Release DLLs already match source version $expectedVersion; skipping compilation." -ForegroundColor Green
        Write-Host 'Continuing publish commit flow with the existing Release outputs.' -ForegroundColor Green
        $publishArguments = @(
            'msbuild',
            $projectFile.FullName,
            '-t:CommitReleaseVersion',
            '-p:Configuration=Release',
            "-p:BuildVersion=$expectedVersion",
            "-p:Version=$expectedVersion"
        )
        & dotnet @publishArguments
        exit $LASTEXITCODE
    }

    Write-Host "$projectName Release DLLs already match source version $expectedVersion; skipping compilation." -ForegroundColor Green
    exit 0
}

$lockedReleaseDlls = @(
    foreach ($relativePath in $releaseDllPaths) {
        $dllPath = Join-Path $PSScriptRoot $relativePath
        if (Test-FileLocked -Path $dllPath) {
            $dllPath
        }
    }
)

if ($lockedReleaseDlls.Count -gt 0) {
    Write-Host 'WARNING: Release build skipped because the following DLL is locked:' -ForegroundColor Yellow
    $lockedReleaseDlls | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    if ($Publish) {
        Write-Host 'The pending message remains for the next build.' -ForegroundColor Yellow
    }
    exit 0
}

$buildArguments = @('build', $projectFile.FullName, '-c', 'Release', '--no-incremental')
if (-not $Publish) {
    $buildArguments += '-p:AutoCommitVersionOnRelease=false'
}

try {
    $releaseSnapshot = New-ReleaseOutputSnapshot -DllPaths $releaseDllPaths -ErrorAction Stop
} catch {
    Write-Error "Unable to preserve the existing Release outputs before building: $($_.Exception.Message)"
    exit 1
}
$buildOutput = @()
$buildExitCode = 1
$buildInvocationException = $null
try {
    $buildOutput = @(& dotnet @buildArguments 2>&1)
    $buildExitCode = $LASTEXITCODE
} catch {
    $buildInvocationException = $_
}
$buildOutput | ForEach-Object { Write-Host $_ }

if ($buildExitCode -ne 0) {
    try {
        Restore-ReleaseOutputSnapshot -Snapshot $releaseSnapshot
        Write-Host 'Restored the previous Release outputs after the failed build.' -ForegroundColor Yellow
    } catch {
        Write-Error "Release build failed and the previous outputs could not be restored: $($_.Exception.Message)"
        Remove-ReleaseOutputSnapshot -Snapshot $releaseSnapshot
        exit $buildExitCode
    }
    Remove-ReleaseOutputSnapshot -Snapshot $releaseSnapshot

    if ($null -ne $buildInvocationException) {
        Write-Error "Unable to start the Release build: $($buildInvocationException.Exception.Message)"
        exit $buildExitCode
    }

    $lockedAfterBuild = @(
        foreach ($relativePath in $releaseDllPaths) {
            $dllPath = Join-Path $PSScriptRoot $relativePath
            if (Test-FileLocked -Path $dllPath) {
                $dllPath
            }
        }
    )

    if ($lockedAfterBuild.Count -gt 0) {
        Write-Host 'WARNING: Release build failed because the following DLL became locked:' -ForegroundColor Yellow
        $lockedAfterBuild | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        if ($Publish) {
            Write-Host 'The pending message remains for the next build.' -ForegroundColor Yellow
        }
        exit 0
    }
    exit $buildExitCode
}

Remove-ReleaseOutputSnapshot -Snapshot $releaseSnapshot
