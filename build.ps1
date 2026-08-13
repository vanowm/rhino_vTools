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

if (-not $Publish -and -not $ComposeOnly -and -not [string]::IsNullOrWhiteSpace($Message)) {
    Write-Error 'The -Message option is only valid with -Publish or -ComposeOnly.'
    exit 1
}

if ($Publish -or $ComposeOnly) {
    if (-not (Test-Path -LiteralPath $gitDirectory -PathType Container)) {
        Write-Error 'Publishing requires a Git working copy. Run build.ps1 without -Publish for a standalone build.'
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

if ($Publish) {
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

$buildOutput = @(& dotnet @buildArguments 2>&1)
$buildExitCode = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }

if ($buildExitCode -ne 0) {
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
