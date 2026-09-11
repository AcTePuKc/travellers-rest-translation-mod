param(
    [Parameter(Mandatory = $true)]
    [string]$InputFile,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$LanguageCode,
    [string]$OutputFile = ""
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
$output = [Collections.Generic.List[string]]::new()
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$duplicateKeys = [Collections.Generic.List[string]]::new()
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

    if (-not $keys.Add($key)) {
        $duplicateKeys.Add($key)
        continue
    }

    # Do not decode or replace \n here. The plugin decodes it at runtime.
    $output.Add("$key=$translation")
}

$parent = Split-Path -Parent $OutputFile
if ($parent) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines((Resolve-Path -LiteralPath $OutputFile -ErrorAction SilentlyContinue)?.Path ?? $OutputFile, $output, $utf8NoBom)

[pscustomobject]@{
    InputFile = (Resolve-Path -LiteralPath $InputFile).Path
    OutputFile = (Resolve-Path -LiteralPath $OutputFile).Path
    OutputEntries = $output.Count
    EmptyTranslationsSkipped = $emptyTranslations.Count
    DuplicateKeysSkipped = $duplicateKeys.Count
    InvalidLinesSkipped = $invalidLines.Count
    DuplicateKeys = @($duplicateKeys)
    InvalidLineNumbers = @($invalidLines)
    Verified = ($duplicateKeys.Count -eq 0 -and $invalidLines.Count -eq 0)
} | ConvertTo-Json -Depth 3
