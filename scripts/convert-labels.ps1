param(
    [Parameter(Mandatory = $true)]
    [string]$InputFile,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$LanguageCode,
    [string]$OutputFile = "",
    [string]$DuplicateReport = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $InputFile -PathType Leaf)) {
    throw "Input file not found: $InputFile"
}

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $inputDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $InputFile).Path
    $OutputFile = Join-Path $inputDirectory "labels.$LanguageCode.txt"
}

$lines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $InputFile).Path, [Text.UTF8Encoding]::new($false))
$records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$recordOrder = [Collections.Generic.List[string]]::new()
$duplicateKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$duplicateRecords = [Collections.Generic.List[object]]::new()
$invalidLines = [Collections.Generic.List[int]]::new()
$emptyTranslations = [Collections.Generic.List[int]]::new()

for ($index = 0; $index -lt $lines.Count; $index++) {
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

# Preserve runtime escapes. Older local exports used " / / " as a paragraph-break marker;
# normalize that legacy form to the format consumed by the plugin.
$output = [Collections.Generic.List[string]]::new()
foreach ($key in $recordOrder) {
    if ($duplicateKeys.Contains($key)) {
        continue
    }

    $translation = $records[$key].Translation.Replace(" / / ", "\n\n").Replace("—", "-")
    $output.Add("$key=$translation")
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
    DuplicateKeysExcluded = $duplicateKeys.Count
    InvalidLinesSkipped = $invalidLines.Count
    DuplicateReport = if ($duplicateRecords.Count -gt 0) { (Resolve-Path -LiteralPath $DuplicateReport).Path } else { $null }
    DuplicateKeys = @($duplicateKeys)
    InvalidLineNumbers = @($invalidLines)
    Verified = ($duplicateKeys.Count -eq 0 -and $invalidLines.Count -eq 0)
} | ConvertTo-Json -Depth 3
