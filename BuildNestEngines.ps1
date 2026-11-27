Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'Resources\NestEngines'
$cache = Join-Path $PSScriptRoot 'obj\NestEngines'
$output = Join-Path $cache 'Runtime.zip'
$stamp = "$output.sha256"
$manifest = Join-Path $source 'Runtime.json'
$worker = Join-Path $source 'worker.py'
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $hash = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
        finally { $hash.Dispose() }
    } finally { $stream.Dispose() }
}
$identity = (@($PSCommandPath, $manifest, $worker) | ForEach-Object { Get-Sha256 $_ }) -join '-'

[IO.Directory]::CreateDirectory($cache) | Out-Null
$mutex = [Threading.Mutex]::new($false, "Local\vToolsNestEngines-$($identity.Substring(0,64))")
try {
    try { $null = $mutex.WaitOne() } catch [Threading.AbandonedMutexException] {}
    try {
        if ((Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $stamp) -and
            [IO.File]::ReadAllText($stamp) -eq $identity) { return }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        Add-Type -AssemblyName System.IO.Compression
        $temporary = "$output.$PID.tmp"
        try {
            $bundle = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($package in (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)) {
                    $path = Join-Path $cache $package.name
                    if (!(Test-Path -LiteralPath $path) -or (Get-Sha256 $path) -ne $package.sha256) {
                        $download = "$path.$PID.tmp"
                        try {
                            Write-Host "Fetching vNest dependency $($package.name)"
                            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                            $client = [Net.WebClient]::new()
                            try { $client.DownloadFile($package.url, $download) } finally { $client.Dispose() }
                            if ((Get-Sha256 $download) -ne $package.sha256) {
                                throw "SHA-256 mismatch for $($package.name)."
                            }
                            Move-Item -LiteralPath $download -Destination $path -Force
                        } finally {
                            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
                        }
                    }
                    $zip = [IO.Compression.ZipFile]::OpenRead($path)
                    try {
                        foreach ($entry in $zip.Entries) {
                            if (!$entry.Name) { continue }
                            if ($entry.FullName -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))' -or !$names.Add($entry.FullName)) {
                                throw "Unexpected archive entry: $($entry.FullName)"
                            }
                            $inputStream = $entry.Open()
                            try {
                                $target = $bundle.CreateEntry($entry.FullName).Open()
                                try { $inputStream.CopyTo($target) } finally { $target.Dispose() }
                            } finally { $inputStream.Dispose() }
                        }
                    } finally { $zip.Dispose() }
                }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bundle, $worker, 'worker.py') | Out-Null
            } finally { $bundle.Dispose() }
            Move-Item -LiteralPath $temporary -Destination $output -Force
            [IO.File]::WriteAllText($stamp, $identity)
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    } finally { $mutex.ReleaseMutex() }
} finally { $mutex.Dispose() }
