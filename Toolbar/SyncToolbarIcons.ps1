param(
  [string]$SourceRui = (Join-Path $PSScriptRoot "vTools.rui"),
  [string[]]$TargetRui
)

$ErrorActionPreference = "Stop"
$pluginId = "2607512e-a1fc-4cf9-9329-a293431437a0"
$supportedVersions = @("8.0", "9.0")

if (Get-Process Rhino -ErrorAction SilentlyContinue) {
  throw "Close Rhino before synchronizing toolbar icons."
}

if (-not (Test-Path -LiteralPath $SourceRui -PathType Leaf)) {
  throw "Source toolbar file not found: $SourceRui"
}

if (-not $TargetRui -or $TargetRui.Count -eq 0) {
  $profilesRoot = Join-Path $env:APPDATA "McNeel\Rhinoceros"
  $discoveredTargets = New-Object System.Collections.Generic.List[string]

  foreach ($version in $supportedVersions) {
    $defaultRui = Join-Path $profilesRoot "$version\UI\default.rui"
    if (Test-Path -LiteralPath $defaultRui -PathType Leaf) {
      $discoveredTargets.Add($defaultRui)
    }

    $pluginKey = "HKCU:\Software\McNeel\Rhinoceros\$version\Plug-Ins\$pluginId"
    if (Test-Path -LiteralPath $pluginKey) {
      $registeredRui = (Get-ItemProperty -LiteralPath $pluginKey -Name RuiFile -ErrorAction SilentlyContinue).RuiFile
      if (-not [string]::IsNullOrWhiteSpace($registeredRui) -and
          (Test-Path -LiteralPath $registeredRui -PathType Leaf)) {
        $discoveredTargets.Add($registeredRui)
      }
    }
  }

  $TargetRui = @(
    $discoveredTargets |
      ForEach-Object { [System.IO.Path]::GetFullPath($_) } |
      Sort-Object -Unique
  )
}

if (-not $TargetRui -or $TargetRui.Count -eq 0) {
  throw "No active Rhino 8 or Rhino 9 toolbar files were found."
}

function Read-Rui([string]$Path) {
  $document = New-Object System.Xml.XmlDocument
  $document.PreserveWhitespace = $true
  $document.XmlResolver = $null
  $document.Load($Path)
  return $document
}

$source = Read-Rui $SourceRui
$sourceIcons = @($source.SelectNodes("/RhinoUI/icons/icon"))
if ($sourceIcons.Count -eq 0) {
  throw "The source toolbar contains no icon definitions."
}

foreach ($targetPathValue in $TargetRui) {
  $targetPath = [System.IO.Path]::GetFullPath($targetPathValue)
  if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
    Write-Warning "Toolbar file not found: $targetPath"
    continue
  }

  $target = Read-Rui $targetPath
  $targetIcons = @($target.SelectNodes("/RhinoUI/icons/icon"))
  $updated = 0

  foreach ($sourceIcon in $sourceIcons) {
    $guid = $sourceIcon.GetAttribute("guid")
    $matches = @($targetIcons | Where-Object {
      [string]::Equals($_.GetAttribute("guid"), $guid,
        [System.StringComparison]::OrdinalIgnoreCase)
    })

    foreach ($targetIcon in $matches) {
      foreach ($theme in @("light", "dark")) {
        $sourceTheme = $sourceIcon.SelectSingleNode($theme)
        if ($null -eq $sourceTheme) { continue }

        $replacement = $target.ImportNode($sourceTheme, $true)
        $targetTheme = $targetIcon.SelectSingleNode($theme)
        if ($null -ne $targetTheme) {
          [void]$targetIcon.ReplaceChild($replacement, $targetTheme)
        }
        else {
          [void]$targetIcon.AppendChild($replacement)
        }
      }
      $updated++
    }
  }

  if ($updated -eq 0) {
    Write-Warning "No matching vTools icons found in $targetPath"
    continue
  }

  $backupPath = "$targetPath.vToolsIconSync.bak"
  $temporaryPath = "$targetPath.vToolsIconSync.tmp"
  $settings = New-Object System.Xml.XmlWriterSettings
  $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
  $settings.Indent = $false
  $settings.NewLineHandling = [System.Xml.NewLineHandling]::None

  $writer = [System.Xml.XmlWriter]::Create($temporaryPath, $settings)
  try {
    $target.Save($writer)
  }
  finally {
    $writer.Dispose()
  }

  [System.IO.File]::Replace($temporaryPath, $targetPath, $backupPath, $true)
  Write-Host "Synchronized $updated vTools icon definitions in $targetPath"
  Write-Host "Backup: $backupPath"
}
