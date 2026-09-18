param(
    [Parameter(Mandatory = $true)]
    [string]$InputFile,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$LanguageCode,
    [string]$OutputFile = "",
    [string]$DuplicateReport = "",
    [string]$OverrideFile = ""
)

$ErrorActionPreference = "Stop"

function Repair-UnclosedRichTextTags([string]$Value) {
    $selfClosing = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$selfClosing.Add("br")
    [void]$selfClosing.Add("sprite")
    $openTags = [Collections.Generic.List[string]]::new()
    $result = [Text.StringBuilder]::new()
    $tagPattern = '<(/?)([A-Za-z]+)(?:[^>]*)?>'
    $position = 0

    foreach ($match in [regex]::Matches($Value, $tagPattern)) {
        [void]$result.Append($Value.Substring($position, $match.Index - $position))
        $isClosing = $match.Groups[1].Value -eq "/"
        $tagName = $match.Groups[2].Value.ToLowerInvariant()
        if ($selfClosing.Contains($tagName)) {
            [void]$result.Append($match.Value)
            $position = $match.Index + $match.Length
            continue
        }

        if ($isClosing) {
            $found = -1
            for ($index = $openTags.Count - 1; $index -ge 0; $index--) {
                if ($openTags[$index] -eq $tagName) {
                    $found = $index
                    break
                }
            }
            if ($found -ge 0) {
                for ($index = $openTags.Count - 1; $index -gt $found; $index--) {
                    [void]$result.Append("</$($openTags[$index])>")
                    [void]$openTags.RemoveAt($index)
                }
                [void]$result.Append($match.Value)
                [void]$openTags.RemoveAt($found)
            }
            $position = $match.Index + $match.Length
            continue
        }

        [void]$result.Append($match.Value)
        [void]$openTags.Add($tagName)
        $position = $match.Index + $match.Length
    }

    [void]$result.Append($Value.Substring($position))
    for ($index = $openTags.Count - 1; $index -ge 0; $index--) {
        [void]$result.Append("</$($openTags[$index])>")
    }
    return $result.ToString()
}

if (-not (Test-Path -LiteralPath $InputFile -PathType Leaf)) {
    throw "Input file not found: $InputFile"
}

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $inputDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $InputFile).Path
    $OutputFile = Join-Path $inputDirectory "labels.$LanguageCode.txt"
}

if ([string]::IsNullOrWhiteSpace($OverrideFile)) {
    $outputBase = [IO.Path]::GetFileNameWithoutExtension($OutputFile)
    $outputDirectory = Split-Path -Parent $OutputFile
    $OverrideFile = Join-Path $outputDirectory "$outputBase.overrides.txt"
}

$lines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $InputFile).Path, [Text.UTF8Encoding]::new($false))
$records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$recordOrder = [Collections.Generic.List[string]]::new()
$duplicateKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$duplicateRecords = [Collections.Generic.List[object]]::new()
$invalidLines = [Collections.Generic.List[int]]::new()
$emptyTranslations = [Collections.Generic.List[int]]::new()

# A previous review export may have been prepended to the source file. When the
# marker exists, ignore that report and process only the actual label section.
$startIndex = 0
$labelsMarker = [array]::IndexOf($lines, "# Unique labels")
if ($labelsMarker -ge 0) {
    $startIndex = $labelsMarker + 1
}

for ($index = $startIndex; $index -lt $lines.Count; $index++) {
    $lineNumber = $index + 1
    $line = $lines[$index]

    if ($lineNumber -eq 1) {
        $line = $line.TrimStart([char]0xFEFF)
    }

    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#') -or $line.StartsWith('//')) {
        continue
    }

    $separator = $line.IndexOf('=')
    if ($separator -le 0) {
        $invalidLines.Add($lineNumber)
        continue
    }

    # Split only at the first '='. The value may legally contain '=' characters.
    $key = $line.Substring(0, $separator)
    $translation = $line.Substring($separator + 1)

    if ([string]::IsNullOrWhiteSpace($translation)) {
        $emptyTranslations.Add($lineNumber)
        continue
    }

    $record = [pscustomobject]@{
        Key = $key
        LineNumber = $lineNumber
        RawLine = $line
        Translation = $translation
    }

    if ($records.ContainsKey($key)) {
        if ($duplicateKeys.Add($key)) {
            $duplicateRecords.Add($records[$key])
        }
        $duplicateRecords.Add($record)
        continue
    }

    $records[$key] = $record
    $recordOrder.Add($key)
}

$overrides = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$invalidOverrideLines = [Collections.Generic.List[int]]::new()
if (Test-Path -LiteralPath $OverrideFile -PathType Leaf) {
    $overrideLines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $OverrideFile).Path, [Text.UTF8Encoding]::new($false))
    for ($index = 0; $index -lt $overrideLines.Count; $index++) {
        $line = $overrideLines[$index]
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#') -or $line.StartsWith('//')) {
            continue
        }
        $separator = $line.IndexOf('=')
        if ($separator -le 0 -or [string]::IsNullOrWhiteSpace($line.Substring($separator + 1))) {
            $invalidOverrideLines.Add($index + 1)
            continue
        }
        $overrideKey = $line.Substring(0, $separator)
        $overrides[$overrideKey] = $line.Substring($separator + 1)
    }
}

# Preserve runtime escapes. Older local exports used " / / " as a paragraph-break marker;
# normalize that legacy form to the format consumed by the plugin.
$output = [Collections.Generic.List[string]]::new()
$overridesApplied = [Collections.Generic.List[string]]::new()
foreach ($key in $recordOrder) {
    # The game now uses the sheet-qualified form for item labels. The old
    # unqualified item_name/item_description keys are redundant in the package.
    $isLegacyItemKey = $key -match '^item_(?:name|description)_-?\d+$'

    if ($duplicateKeys.Contains($key)) {
        if (-not $overrides.ContainsKey($key)) {
            continue
        }
        $translation = $overrides[$key]
        $overridesApplied.Add($key)
    } else {
        $translation = $records[$key].Translation
    }

    $translation = $translation.Replace(" / / ", "\n\n").Replace("—", "-")
    $translation = Repair-UnclosedRichTextTags $translation
    $outputKey = if ($isLegacyItemKey) { "Items/$key" } else { $key }
    $output.Add("$outputKey=$translation")
}

$parent = Split-Path -Parent $OutputFile
if ($parent) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines((Resolve-Path -LiteralPath $OutputFile -ErrorAction SilentlyContinue)?.Path ?? $OutputFile, $output, $utf8NoBom)

if ([string]::IsNullOrWhiteSpace($DuplicateReport)) {
    $DuplicateReport = "$OutputFile.duplicates.txt"
}

if ($duplicateRecords.Count -gt 0) {
    $report = [Collections.Generic.List[string]]::new()
    $report.Add("# Duplicate keys requiring manual review")
    $report.Add("# These entries were excluded from the generated labels file.")
    $report.Add("")
    foreach ($record in $duplicateRecords) {
        $report.Add("[$($record.Key)] input line $($record.LineNumber)")
        $report.Add($record.RawLine)
        $report.Add("")
    }
    [IO.File]::WriteAllLines($DuplicateReport, $report, $utf8NoBom)
}

[pscustomobject]@{
    InputFile = (Resolve-Path -LiteralPath $InputFile).Path
    OutputFile = (Resolve-Path -LiteralPath $OutputFile).Path
    OutputEntries = $output.Count
    EmptyTranslationsSkipped = $emptyTranslations.Count
    DuplicateKeysDetected = $duplicateKeys.Count
    DuplicateKeysUnresolved = @($duplicateKeys | Where-Object { -not $overrides.ContainsKey($_) }).Count
    OverridesApplied = @($overridesApplied)
    OverrideFile = if (Test-Path -LiteralPath $OverrideFile -PathType Leaf) { (Resolve-Path -LiteralPath $OverrideFile).Path } else { $null }
    InvalidOverrideLines = @($invalidOverrideLines)
    InvalidLinesSkipped = $invalidLines.Count
    DuplicateReport = if ($duplicateRecords.Count -gt 0) { (Resolve-Path -LiteralPath $DuplicateReport).Path } else { $null }
    DuplicateKeys = @($duplicateKeys)
    InvalidLineNumbers = @($invalidLines)
    Verified = ($duplicateKeys | Where-Object { -not $overrides.ContainsKey($_) }).Count -eq 0 -and $invalidLines.Count -eq 0 -and $invalidOverrideLines.Count -eq 0
} | ConvertTo-Json -Depth 3
