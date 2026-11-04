#requires -Version 5.1
<#
.SYNOPSIS
    Regenerate the frozen reference outputs from the Python 1.1.0 oracle.

.DESCRIPTION
    Run this only while the Python reference still exists. The outputs under
    tests/golden are the parity contract for the C# rewrite; regenerating them
    changes that contract and must be a deliberate, reviewed change.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo 'claude_memory.py'
$fixture = Join-Path $repo 'tests\fixtures\fixture_root'
$share = Join-Path $repo 'tests\fixtures\share-branching.json'
$golden = Join-Path $repo 'tests\golden'

# argparse wraps the help at COLUMNS - 2; freeze the width so the capture is machine-independent.
$env:COLUMNS = '80'

if (-not (Test-Path -LiteralPath $cli)) {
    throw "Python reference not found: $cli"
}

if (Test-Path -LiteralPath $golden) {
    Remove-Item -LiteralPath $golden -Recurse -Force
}

New-Item -ItemType Directory -Path $golden | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)

function Invoke-Reference {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Arguments
    )

    $outFile = Join-Path $env:TEMP ('cm-golden-' + [guid]::NewGuid().ToString('N') + '.txt')
    $errFile = Join-Path $env:TEMP ('cm-golden-' + [guid]::NewGuid().ToString('N') + '.err')

    try {
        $line = 'py -3.12 "' + $cli + '" ' + $Arguments + ' > "' + $outFile + '" 2> "' + $errFile + '"'
        cmd.exe /d /c $line | Out-Null
        $code = $LASTEXITCODE

        if ($code -ne 0) {
            $stderr = if (Test-Path -LiteralPath $errFile) {
                [System.IO.File]::ReadAllText($errFile)
            } else {
                ''
            }
            throw "Reference command failed with exit code $code for $Name.`n$stderr"
        }

        $text = [System.IO.File]::ReadAllText($outFile, $utf8)
        if ([string]::IsNullOrWhiteSpace($text)) {
            throw "Reference produced no output for $Name."
        }

        # Windows Python text-mode stdout writes CRLF; the parity contract is LF.
        $text = $text.Replace("`r`n", "`n")

        [System.IO.File]::WriteAllText((Join-Path $golden $Name), $text, $utf8)
        Write-Host ('wrote {0} ({1} chars)' -f $Name, $text.Length)
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-CacheReference {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$PayloadPath
    )

    $script = Join-Path $env:TEMP ('cm-cache-' + [guid]::NewGuid().ToString('N') + '.py')
    $outFile = Join-Path $env:TEMP ('cm-golden-' + [guid]::NewGuid().ToString('N') + '.txt')

    try {
        # Mirrors claude_memory.py:1931 (--share-save): json.dumps(payload, indent=1,
        # ensure_ascii=False) + "\n". Kept as a snippet because --share-save also needs a
        # live fetch, which this offline generator must not perform.
        $body = @'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    payload = json.load(handle)
sys.stdout.write(json.dumps(payload, indent=1, ensure_ascii=False) + "\n")
'@
        [System.IO.File]::WriteAllText($script, $body, $utf8)

        $line = 'py -3.12 -X utf8 "' + $script + '" "' + $PayloadPath + '" > "' + $outFile + '"'
        cmd.exe /d /c $line | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Cache reference failed for $Name." }

        # Windows Python text-mode stdout writes CRLF; the parity contract is LF.
        $text = [System.IO.File]::ReadAllText($outFile, $utf8).Replace("`r`n", "`n")
        [System.IO.File]::WriteAllText((Join-Path $golden $Name), $text, $utf8)
        Write-Host ('wrote {0} ({1} chars)' -f $Name, $text.Length)
    }
    finally {
        Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-HelpReference {
    param(
        [Parameter(Mandatory = $true)][string]$Name
    )

    $outFile = Join-Path $env:TEMP ('cm-golden-' + [guid]::NewGuid().ToString('N') + '.txt')

    try {
        # --help is printed by argparse before configure_stdio(), but the text is pure ASCII.
        $line = 'py -3.12 "' + $cli + '" --help > "' + $outFile + '"'
        cmd.exe /d /c $line | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Help reference failed with exit code $LASTEXITCODE." }

        # Windows Python text-mode stdout writes CRLF; the parity contract is LF.
        $text = [System.IO.File]::ReadAllText($outFile, $utf8).Replace("`r`n", "`n")

        # The real home directory must not leak into a committed file. Mirrors DEFAULT_ROOT
        # (claude_memory.py:39): Path(expanduser("~")) / ".claude".
        $defaultRoot = (& py -3.12 -c "import os; print(os.path.join(os.path.expanduser('~'), '.claude'))").Trim()
        if ([string]::IsNullOrWhiteSpace($defaultRoot)) { throw 'Could not resolve the reference DEFAULT_ROOT.' }
        $text = $text.Replace($defaultRoot, '%DEFAULT_ROOT%')

        [System.IO.File]::WriteAllText((Join-Path $golden $Name), $text, $utf8)
        Write-Host ('wrote {0} ({1} chars)' -f $Name, $text.Length)
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    }
}

$rootArgs = '--root "' + $fixture + '"'

Invoke-Reference 'memory-plain.md' $rootArgs
Invoke-Reference 'memory-claude-md.md' ($rootArgs + ' --format claude-md')
Invoke-Reference 'memory-ascii.md' ($rootArgs + ' --ascii')
Invoke-Reference 'memory-lang-both.md' ($rootArgs + ' --lang both')
Invoke-Reference 'full-plain.md' ($rootArgs + ' --mode full')
Invoke-Reference 'transcript.md' ($rootArgs + ' --mode transcript')
Invoke-Reference 'json.json' ($rootArgs + ' --mode json')
Invoke-Reference 'list.md' ($rootArgs + ' --list')
Invoke-Reference 'share-memory.md' ('--share-file "' + $share + '" --only-share')
Invoke-CacheReference 'share-cache.json' $share
Invoke-HelpReference 'cli-help.txt'

Write-Host 'Golden files regenerated.'