param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$TemplatePath
)

$ErrorActionPreference = 'Stop'
$topicsStartMarker = '<!-- GENERATED_TOPICS_START -->'
$topicsEndMarker = '<!-- GENERATED_TOPICS_END -->'

function Convert-PlainText {
    param(
        [string]$Text
    )

    return [System.Net.WebUtility]::HtmlEncode($Text)
}

function Convert-CodeSpan {
    param(
        [string]$Text
    )

    $encoded = [System.Net.WebUtility]::HtmlEncode($Text)
    return "<code>$encoded</code>"
}

function Convert-InlineMarkdown {
    param(
        [string]$Text,
        [string]$CurrentCommandName = ''
    )

    $fragments = [System.Collections.Generic.List[string]]::new()
    $fragmentPrefix = '@@VTOOLSHELP' + [Guid]::NewGuid().ToString('N') + '-'
    $working = [System.Text.RegularExpressions.Regex]::Replace(
        $Text,
        '`([^`\r\n]+)`',
        [System.Text.RegularExpressions.MatchEvaluator]{
            param($match)

            $index = $fragments.Count
            $fragments.Add((Convert-CodeSpan $match.Groups[1].Value))
            return $fragmentPrefix + $index + '@@'
        })
    $working = [System.Text.RegularExpressions.Regex]::Replace(
        $working,
        '\[([^]\r\n]+)\]\(([^)\r\n]+)\)',
        [System.Text.RegularExpressions.MatchEvaluator]{
            param($match)

            $index = $fragments.Count
            $labelText = $match.Groups[1].Value
            $targetText = $match.Groups[2].Value
            $label = [System.Net.WebUtility]::HtmlEncode($labelText)
            if ($targetText -match '^#(?<topic>v[A-Za-z0-9]+)-flow$') {
                $targetText = '#' + $Matches['topic'].ToLowerInvariant()
            }
            $target = [System.Net.WebUtility]::HtmlEncode($targetText)
            $fragments.Add("<a href=`"$target`">$label</a>")
            return $fragmentPrefix + $index + '@@'
        })
    $working = [System.Text.RegularExpressions.Regex]::Replace(
        $working,
        '\*\*([^*\r\n]+)\*\*',
        [System.Text.RegularExpressions.MatchEvaluator]{
            param($match)

            $index = $fragments.Count
            $content = Convert-PlainText $match.Groups[1].Value
            $fragments.Add("<strong>$content</strong>")
            return $fragmentPrefix + $index + '@@'
        })

    $encoded = Convert-PlainText $working
    for ($index = $fragments.Count - 1; $index -ge 0; $index--) {
        $encoded = $encoded.Replace($fragmentPrefix + $index + '@@', $fragments[$index])
    }
    return $encoded
}

function Format-CommandDescription {
    param([string]$Text)

    $description = $Text.Trim()
    if ([string]::IsNullOrWhiteSpace($description)) {
        return $description
    }

    if ($description -notmatch '[.!?]$') {
        $description += '.'
    }
    return $description
}

function Format-StandaloneDescription {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    return $Text.Substring(0, 1).ToUpperInvariant() + $Text.Substring(1)
}

function Convert-MarkdownBlock {
    param(
        [string]$Markdown,
        [string]$TopicId = '',
        [string]$CurrentCommandName = ''
    )

    $builder = New-Object System.Text.StringBuilder
    $listType = $null
    $orderedItemCount = 0
    $inCode = $false
    $inCommandOptions = $false
    foreach ($line in [System.Text.RegularExpressions.Regex]::Split($Markdown, '\r?\n')) {
        if ($line -match '^```') {
            if ($null -ne $listType) {
                [void]$builder.AppendLine("</$listType>")
                $listType = $null
            }
            if ($inCode) {
                [void]$builder.AppendLine('</code></pre>')
            } else {
                [void]$builder.AppendLine('<pre><code>')
            }
            $inCode = -not $inCode
            continue
        }

        if ($inCode) {
            [void]$builder.AppendLine([System.Net.WebUtility]::HtmlEncode($line))
            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($null -ne $listType) {
                [void]$builder.AppendLine("</$listType>")
                $listType = $null
            }
            continue
        }

        if ($line -match '^####\s+(.+)$') {
            if ($null -ne $listType) {
                [void]$builder.AppendLine("</$listType>")
                $listType = $null
            }
            $headingText = $Matches[1]
            $headingSlug = [System.Text.RegularExpressions.Regex]::Replace(
                $headingText.ToLowerInvariant(), '[^a-z0-9]+', '-').Trim('-')
            $headingId = if ([string]::IsNullOrWhiteSpace($TopicId)) {
                $headingSlug
            } else {
                $TopicId + '-' + $headingSlug
            }
            [void]$builder.AppendLine(
                "<h3 id=`"$([System.Net.WebUtility]::HtmlEncode($headingId))`">$(Convert-InlineMarkdown $headingText $CurrentCommandName)</h3>")
            $orderedItemCount = 0
            $inCommandOptions = $false
            continue
        }

        if ($line.Trim() -match '^Options(?:\s+\((?<scope>[^)]+)\))?:$') {
            if ($null -ne $listType) {
                [void]$builder.AppendLine("</$listType>")
                $listType = $null
            }
            $optionsTitle = if ([string]::IsNullOrWhiteSpace($Matches['scope'])) {
                'Command-line options'
            } else {
                $Matches['scope'] + ' options'
            }
            [void]$builder.AppendLine(
                "<p class=`"Dialog_Box_Title`">$([System.Net.WebUtility]::HtmlEncode($optionsTitle))</p>")
            $inCommandOptions = $true
            continue
        }

        if ($line.Trim() -eq 'Methods:') {
            if ($null -ne $listType) {
                [void]$builder.AppendLine("</$listType>")
                $listType = $null
            }
            [void]$builder.AppendLine('<p class="Dialog_Box_Title">Methods</p>')
            $inCommandOptions = $true
            continue
        }

        if ($line.Trim() -eq 'Notes:') {
            if ($null -ne $listType) {
                [void]$builder.AppendLine("</$listType>")
                $listType = $null
            }
            [void]$builder.AppendLine('<h3>Notes</h3>')
            $inCommandOptions = $false
            continue
        }

        $nextListType = $null
        $itemText = $null
        if ($line -match '^\s*\d+\.\s+(.+)$') {
            $nextListType = 'ol'
            $itemText = $Matches[1]
        } elseif ($line -match '^\s*[-*]\s+(.+)$') {
            $nextListType = 'ul'
            $itemText = $Matches[1]
        }

        if ($null -ne $nextListType) {
            if ($inCommandOptions -and $nextListType -eq 'ul' -and
                $itemText -match '^(?<name>`[^:]+):\s*(?<description>.*)$') {
                if ($null -ne $listType) {
                    [void]$builder.AppendLine("</$listType>")
                    $listType = $null
                }
                $optionName = $Matches['name'].Replace('`', '')
                $optionDescription = $Matches['description']
                [void]$builder.AppendLine('<div class="command-option">')
                [void]$builder.AppendLine("<h5>$(Convert-InlineMarkdown $optionName $CurrentCommandName)</h5>")
                [void]$builder.AppendLine("<p>$(Convert-InlineMarkdown $optionDescription $CurrentCommandName)</p>")
                [void]$builder.AppendLine('</div>')
                continue
            }
            if ($listType -ne $nextListType) {
                if ($null -ne $listType) {
                    [void]$builder.AppendLine("</$listType>")
                }
                if ($nextListType -eq 'ol' -and $orderedItemCount -gt 0) {
                    [void]$builder.AppendLine("<ol start=`"$($orderedItemCount + 1)`">")
                } else {
                    [void]$builder.AppendLine("<$nextListType>")
                }
                $listType = $nextListType
            }
            [void]$builder.AppendLine("<li>$(Convert-InlineMarkdown $itemText $CurrentCommandName)</li>")
            if ($nextListType -eq 'ol') {
                $orderedItemCount++
            }
            continue
        }

        if ($null -ne $listType) {
            [void]$builder.AppendLine("</$listType>")
            $listType = $null
        }

        if ($line -match '^>\s*(.+)$') {
            [void]$builder.AppendLine("<blockquote>$(Convert-InlineMarkdown $Matches[1] $CurrentCommandName)</blockquote>")
        } else {
            [void]$builder.AppendLine("<p>$(Convert-InlineMarkdown $line.Trim() $CurrentCommandName)</p>")
        }
        $inCommandOptions = $false
    }

    if ($null -ne $listType) {
        [void]$builder.AppendLine("</$listType>")
    }
    if ($inCode) {
        [void]$builder.AppendLine('</code></pre>')
    }
    return $builder.ToString()
}

$source = [System.IO.File]::ReadAllText($SourcePath)
$descriptionPattern = '(?m)^\s*-\s+\[(?<name>v[A-Za-z0-9]+)\]\(#[^)]+\)\s+\*\([^)]+\)\*\s+(?:\u2014|-)\s+(?<description>.+?)\s*$'
$descriptionMatches = [System.Text.RegularExpressions.Regex]::Matches(
    $source, $descriptionPattern)
$descriptions = [System.Collections.Generic.Dictionary[string, string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($descriptionMatch in $descriptionMatches) {
    $descriptions[$descriptionMatch.Groups['name'].Value] =
        Format-CommandDescription $descriptionMatch.Groups['description'].Value
}

$topicPattern = '(?ms)^###\s+(?<name>v[A-Za-z0-9]+)\s+flow\s*\r?\n(?<body>.*?)(?=^###\s+v[A-Za-z0-9]+\s+flow|^##\s+|\z)'
$matches = [System.Text.RegularExpressions.Regex]::Matches($source, $topicPattern)
if ($matches.Count -eq 0) {
    throw "No command help sections were found in '$SourcePath'."
}

$helpMatches = @($matches | Where-Object {
    $_.Groups['name'].Value -eq 'vHelp'
})
if ($helpMatches.Count -ne 1) {
    throw "README must contain exactly one vHelp flow section."
}
$commandMatches = @($matches | Where-Object {
    $_.Groups['name'].Value -ne 'vHelp'
})

$missingDescriptions = @(
    foreach ($match in $matches) {
        $commandName = $match.Groups['name'].Value
        if (-not $descriptions.ContainsKey($commandName)) {
            $commandName
        }
    }
)
if ($missingDescriptions.Count -gt 0) {
    throw "Missing command descriptions: $($missingDescriptions -join ', ')."
}

$topics = New-Object System.Text.StringBuilder
[void]$topics.AppendLine('<article class="help-topic" data-topic="vhelp">')
[void]$topics.AppendLine('<h1>vTools commands</h1>')
foreach ($match in $commandMatches) {
    $commandName = $match.Groups['name'].Value
    $topicId = $commandName.ToLowerInvariant()
    $displayName = [System.Net.WebUtility]::HtmlEncode($commandName)
    [void]$topics.AppendLine('<li class="command-option" type="number">')
    [void]$topics.AppendLine("<h5><a href=`"#$topicId`">$displayName</a></h5>")
    $standaloneDescription = Format-StandaloneDescription $descriptions[$commandName]
    [void]$topics.AppendLine("<p>$(Convert-InlineMarkdown $standaloneDescription)</p>")
    [void]$topics.AppendLine('</li>')
}
[void]$topics.AppendLine('</article>')

foreach ($match in $commandMatches) {
    $commandName = $match.Groups['name'].Value
    $topicId = $commandName.ToLowerInvariant()
    $displayName = [System.Net.WebUtility]::HtmlEncode($commandName)
    $body = $match.Groups['body'].Value.Trim()
    $sectionMatch = [System.Text.RegularExpressions.Regex]::Match(
        $body, '(?m)^(Options|Methods|Notes):\s*$')
    if ($sectionMatch.Success) {
        $stepsMarkdown = $body.Substring(0, $sectionMatch.Index).Trim()
        $detailsMarkdown = $body.Substring($sectionMatch.Index).Trim()
    } else {
        $stepsMarkdown = $body
        $detailsMarkdown = ''
    }

    [void]$topics.AppendLine("<article class=`"help-topic`" data-topic=`"$topicId`">")
    [void]$topics.AppendLine("<h1>$displayName</h1>")
    [void]$topics.AppendLine(
        "<p class=`"command-description`">The $displayName command $(Convert-InlineMarkdown $descriptions[$commandName] $commandName)</p>")
    [void]$topics.AppendLine('<div class="tutorialblock">')
    [void]$topics.AppendLine('<h4>Steps</h4>')
    [void]$topics.AppendLine((Convert-MarkdownBlock $stepsMarkdown $topicId $commandName))
    [void]$topics.AppendLine('</div>')
    if (-not [string]::IsNullOrWhiteSpace($detailsMarkdown)) {
        [void]$topics.AppendLine((Convert-MarkdownBlock $detailsMarkdown $topicId $commandName))
    }
    [void]$topics.AppendLine('<h2>See also</h2>')
    [void]$topics.AppendLine('<p><a href="#vhelp">vHelp</a></p>')
    [void]$topics.AppendLine('</article>')
}

$template = [System.IO.File]::ReadAllText($TemplatePath)
$topicRegionPattern = '(?s)' +
    [System.Text.RegularExpressions.Regex]::Escape($topicsStartMarker) +
    '.*?' +
    [System.Text.RegularExpressions.Regex]::Escape($topicsEndMarker)
$topicRegionMatches = [System.Text.RegularExpressions.Regex]::Matches(
    $template, $topicRegionPattern)
if ($topicRegionMatches.Count -ne 1) {
    throw "Help template must contain exactly one generated-topic region between '$topicsStartMarker' and '$topicsEndMarker'."
}

$topicRegion = $topicsStartMarker + "`r`n" +
    $topics.ToString().TrimEnd() + "`r`n" +
    $topicsEndMarker
$html = [System.Text.RegularExpressions.Regex]::Replace(
    $template, $topicRegionPattern, [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return $topicRegion
    })
$html = [System.Text.RegularExpressions.Regex]::Replace(
    $html, '\r?\n', "`r`n")

$outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$encoding = New-Object System.Text.UTF8Encoding($false)
$existing = if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
    [System.IO.File]::ReadAllText($OutputPath)
} else {
    $null
}
if ($existing -ne $html) {
    [System.IO.File]::WriteAllText($OutputPath, $html, $encoding)
}

Write-Host "Generated the vHelp index and $($commandMatches.Count) command topics: $OutputPath"
