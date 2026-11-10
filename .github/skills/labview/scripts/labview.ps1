[CmdletBinding()]
param(
    [switch]$ListTools,
    [string]$DescribeTool,
    [string]$Invoke,
    [string]$ArgsFile,
    [string]$OutFile,
    [string]$Executable,
    [int]$TimeoutSeconds = 300,
    [int]$Port,
    [string]$AixmlBackend,
    [string]$LVVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $modes = @($ListTools.IsPresent, [bool]$DescribeTool, [bool]$Invoke) | Where-Object { $_ }
    if (@($modes).Count -ne 1) { throw 'Choose exactly one of -ListTools, -DescribeTool or -Invoke.' }
    if ($Invoke -and -not $ArgsFile) { throw '-Invoke requires -ArgsFile; use a JSON object, {} for no arguments.' }
    if ($ArgsFile -and -not $Invoke) { throw '-ArgsFile requires -Invoke.' }

    $skillRoot = Split-Path -Parent $PSScriptRoot
    if (-not $Executable) { $Executable = $env:LABVIEW_SKILL_EXE }
    if (-not $Executable) {
        foreach ($candidate in @(
            (Join-Path $skillRoot 'bin\LabVIEWMCP.exe'),
            (Join-Path $skillRoot '..\..\..\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe')
        )) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $Executable = $candidate; break }
        }
    }
    if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        throw 'No CLI executable found. Build the checkout or set LABVIEW_SKILL_EXE / -Executable to an approved build.'
    }
    $Executable = (Resolve-Path -LiteralPath $Executable).Path
    $cliArgs = @()
    if ($ListTools) { $cliArgs += '--list-tools' }
    elseif ($DescribeTool) { $cliArgs += @('--describe-tool', $DescribeTool) }
    else { $cliArgs += @('--invoke', $Invoke, '--args-file', $ArgsFile) }
    $cliArgs += @('--timeout', $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($OutFile) { $cliArgs += @('--out', $OutFile) }
    if ($PSBoundParameters.ContainsKey('Port')) { $cliArgs += @('--port', $Port.ToString()) }
    if ($AixmlBackend) { $cliArgs += @('--aixml-backend', $AixmlBackend) }
    if ($LVVersion) { $cliArgs += @('--lvversion', $LVVersion) }

    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    & $Executable @cliArgs
    exit $LASTEXITCODE
} catch {
    [ordered]@{ ok = $false; errorKind = 'launcher'; error = $_.Exception.Message } |
        ConvertTo-Json -Depth 4
    exit 2
}
