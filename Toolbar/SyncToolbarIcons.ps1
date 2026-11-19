param(
  [string]$SourceRui = (Join-Path $PSScriptRoot "vTools.rui"),
  [string[]]$TargetRui,
  [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"
$pluginId = "2607512e-a1fc-4cf9-9329-a293431437a0"
$supportedVersions = @("8.0", "9.0")

if (-not (Test-Path -LiteralPath $SourceRui -PathType Leaf)) {
  throw "Source toolbar file not found: $SourceRui"
}

if (-not $TargetRui -or $TargetRui.Count -eq 0) {
  $profilesRoot = Join-Path $env:APPDATA "McNeel\Rhinoceros"
  $discoveredTargets = New-Object System.Collections.Generic.List[string]

  # Runtime switches change the registered RUI path. Keep every existing
  # supported Release toolbar synchronized, including the inactive runtime.
  $projectRoot = Split-Path -Parent $PSScriptRoot
  foreach ($variant in @("Rhino8\net7.0-windows", "Rhino8\net10.0-windows", "Rhino9\net10.0-windows")) {
    $releaseRui = Join-Path $projectRoot "bin\Release\$variant\vTools.rui"
    if (Test-Path -LiteralPath $releaseRui -PathType Leaf) {
      $discoveredTargets.Add($releaseRui)
    }
  }

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
  throw "No installed or active Rhino 8 or Rhino 9 toolbar files were found."
}

function Read-Rui([string]$Path) {
  $document = New-Object System.Xml.XmlDocument
  $document.PreserveWhitespace = $true
  $document.XmlResolver = $null
  $document.Load($Path)
  return $document
}

function Get-Section($Document, [string]$Name) {
  $section = $Document.DocumentElement.SelectSingleNode($Name)
  if ($null -eq $section) {
    $section = $Document.CreateElement($Name)
    [void]$Document.DocumentElement.AppendChild($section)
  }
  return ,$section
}

function Get-GuidIndex($Nodes) {
  $index = @{}
  foreach ($node in $Nodes) {
    $index[$node.GetAttribute("guid")] = $node
  }
  return $index
}

$source = Read-Rui $SourceRui
$sourceIcons = @($source.SelectNodes("/RhinoUI/icons/icon"))
if ($sourceIcons.Count -eq 0) {
  throw "The source toolbar contains no icon definitions."
}

$changesNeeded = $false
foreach ($targetPathValue in $TargetRui) {
  $targetPath = [System.IO.Path]::GetFullPath($targetPathValue)
  if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
    Write-Warning "Toolbar file not found: $targetPath"
    continue
  }

  $target = Read-Rui $targetPath
  $targetIcons = @($target.SelectNodes("/RhinoUI/icons/icon"))
  $updated = 0
  $addedIcons = 0
  $addedMacros = 0
  $addedButtons = 0
  $addedBars = 0
  $sourceBars = Get-GuidIndex $source.SelectNodes("/RhinoUI/tool_bars/tool_bar")
  $targetBars = Get-GuidIndex $target.SelectNodes("/RhinoUI/tool_bars/tool_bar")
  $targetMacros = Get-GuidIndex $target.SelectNodes("/RhinoUI/macros/macro_item")
  $iconIndex = Get-GuidIndex $targetIcons
  $requiredMacros = @{}
  $requiredIcons = @{}
  $pendingBars = New-Object System.Collections.Generic.Queue[string]
  $visitedBars = @{}
  $isPluginToolbar = $target.DocumentElement.GetAttribute("plug_in_guid") -eq $pluginId

  # Only seed a whole collection for this plug-in. In Rhino's default RUI,
  # extend matching toolbars and their flyouts without adding a second toolbar.
  foreach ($sourceBar in $source.SelectNodes("/RhinoUI/tool_bars/tool_bar")) {
    $id = $sourceBar.GetAttribute("guid")
    if ($isPluginToolbar -or $targetBars.ContainsKey($id)) {
      $pendingBars.Enqueue($id)
    }
  }
  while ($pendingBars.Count -gt 0) {
    $id = $pendingBars.Dequeue()
    if ($visitedBars.ContainsKey($id) -or -not $sourceBars.ContainsKey($id)) { continue }
    $visitedBars[$id] = $true
    $sourceBar = $sourceBars[$id]
    if (-not $targetBars.ContainsKey($id)) {
      $bar = $target.ImportNode($sourceBar, $true)
      [void](Get-Section $target "tool_bars").AppendChild($bar)
      $targetBars[$id] = $bar
      $addedBars++
      $addedButtons += $sourceBar.SelectNodes("tool_bar_item").Count
    }
    else {
      $bar = $targetBars[$id]
      $items = Get-GuidIndex $bar.SelectNodes("tool_bar_item")
      foreach ($item in $sourceBar.SelectNodes("tool_bar_item")) {
        $itemId = $item.GetAttribute("guid")
        if (-not $items.ContainsKey($itemId)) {
          $newItem = $target.ImportNode($item, $true)
          [void]$bar.AppendChild($newItem)
          $items[$itemId] = $newItem
          $addedButtons++
        }
      }
    }
    foreach ($macroId in $bar.SelectNodes("tool_bar_item/left_macro_id | tool_bar_item/right_macro_id")) {
      $requiredMacros[$macroId.InnerText.Trim()] = $true
    }
    foreach ($link in $bar.SelectNodes("tool_bar_item/link")) {
      $pendingBars.Enqueue($link.InnerText.Trim())
    }
    $requiredIcons[$bar.GetAttribute("bitmap_id")] = $true
  }
  foreach ($macro in $source.SelectNodes("/RhinoUI/macros/macro_item")) {
    $id = $macro.GetAttribute("guid")
    if (-not $requiredMacros.ContainsKey($id)) { continue }
    if (-not $targetMacros.ContainsKey($id)) {
      $newMacro = $target.ImportNode($macro, $true)
      [void](Get-Section $target "macros").AppendChild($newMacro)
      $targetMacros[$id] = $newMacro
      $addedMacros++
    }
    $requiredIcons[$targetMacros[$id].GetAttribute("bitmap_id")] = $true
  }

  foreach ($sourceIcon in $sourceIcons) {
    $guid = $sourceIcon.GetAttribute("guid")
    if (-not $iconIndex.ContainsKey($guid) -and $requiredIcons.ContainsKey($guid)) {
      [void](Get-Section $target "icons").AppendChild($target.ImportNode($sourceIcon, $true))
      $addedIcons++
      continue
    }
    $matches = @($targetIcons | Where-Object {
      [string]::Equals($_.GetAttribute("guid"), $guid,
        [System.StringComparison]::OrdinalIgnoreCase)
    })

    foreach ($targetIcon in $matches) {
      $changed = $false
      foreach ($theme in @("light", "dark")) {
        $sourceTheme = $sourceIcon.SelectSingleNode($theme)
        if ($null -eq $sourceTheme) { continue }

        $replacement = $target.ImportNode($sourceTheme, $true)
        $targetTheme = $targetIcon.SelectSingleNode($theme)
        if ($null -ne $targetTheme -and $targetTheme.OuterXml -ceq $sourceTheme.OuterXml) { continue }
        if ($null -ne $targetTheme) {
          [void]$targetIcon.ReplaceChild($replacement, $targetTheme)
        }
        else {
          [void]$targetIcon.AppendChild($replacement)
        }
        $changed = $true
      }
      if ($changed) { $updated++ }
    }
  }

  if (($updated + $addedIcons + $addedMacros + $addedButtons + $addedBars) -eq 0) {
    if (-not $CheckOnly) { Write-Host "No vTools toolbar changes needed in $targetPath" }
    continue
  }

  $changesNeeded = $true
  if ($CheckOnly) { continue }
  if (Get-Process Rhino -ErrorAction SilentlyContinue) {
    throw "Close Rhino before synchronizing toolbar buttons and icons."
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
  Write-Host "Synchronized vTools in ${targetPath}: added $addedButtons buttons, $addedMacros macros, $addedIcons icons, $addedBars toolbars; refreshed $updated icons."
  Write-Host "Backup: $backupPath"
}
if ($CheckOnly -and $changesNeeded) { exit 2 }
