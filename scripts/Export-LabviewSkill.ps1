<#
.SYNOPSIS
    Export a standalone CLI-only Skill from an already built/published executable.
.DESCRIPTION
    Destination must not exist. No MCP registration, plugin, hooks or agents are installed.
    Local Debug builds are for development only; releases use the CI-published executable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$SourceSkill
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "No executable at '$Executable'." }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$runtime = Split-Path -Parent $Executable
if (Test-Path -LiteralPath $Destination) { throw "Destination already exists: '$Destination'. Choose a new directory." }
if (-not $SourceSkill) {
    foreach ($candidate in @(
        (Join-Path (Split-Path -Parent $PSScriptRoot) '.github\skills\labview'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'skill')
    )) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'SKILL.md')) { $SourceSkill = $candidate; break }
    }
}
if (-not $SourceSkill -or -not (Test-Path -LiteralPath (Join-Path $SourceSkill 'SKILL.md'))) {
    throw 'Cannot find the Skill template. Pass -SourceSkill explicitly.'
}
foreach ($directory in @('scripts', 'docs', 'pylabview')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtime $directory) -PathType Container)) {
        throw "Runtime directory '$directory' is missing beside the executable."
    }
}

$destinationPath = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationPath | Out-Null
foreach ($asset in @('SKILL.md', 'references', 'scripts')) {
    Copy-Item -LiteralPath (Join-Path $SourceSkill $asset) -Destination $destinationPath -Recurse
}
$bin = Join-Path $destinationPath 'bin'
New-Item -ItemType Directory -Path $bin | Out-Null
Copy-Item -LiteralPath $Executable -Destination (Join-Path $bin 'LabVIEWMCP.exe')
# Framework-dependent development builds need their managed dependencies as well.
foreach ($file in Get-ChildItem -LiteralPath $runtime -File |
    Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('LabVIEWMCP.deps.json', 'LabVIEWMCP.runtimeconfig.json') }) {
    Copy-Item -LiteralPath $file.FullName -Destination $bin
}
foreach ($directory in @('scripts', 'docs', 'pylabview')) {
    $sourceDirectory = Join-Path $runtime $directory
    foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' -and $_.Extension -ne '.pyc' }) {
        $relative = $file.FullName.Substring($runtime.Length).TrimStart('\')
        $target = Join-Path $bin $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
Copy-Item -LiteralPath (Join-Path $runtime 'LICENSE') -Destination $destinationPath

$references = Join-Path $destinationPath 'references'
$catalogue = Join-Path $references 'tools.json'
& (Join-Path $bin 'LabVIEWMCP.exe') --list-tools --out $catalogue | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Tool discovery failed (exit $LASTEXITCODE)." }
$tools = (Get-Content -LiteralPath $catalogue -Raw -Encoding UTF8 | ConvertFrom-Json).tools
$rows = @(
    '# Tool index'
    ''
    'Generated from the packaged executable. Use -DescribeTool for full schemas and safety hints.'
    ''
    '| Tool | Purpose | Read-only hint |'
    '|---|---|---|'
)
foreach ($tool in $tools) {
    $title = $tool.title -replace '\|', '\|' -replace '[\r\n]+', ' '
    $rows += ('| `{0}` | {1} | {2} |' -f $tool.name, $title, $tool.annotations.readOnlyHint)
}
[IO.File]::WriteAllText((Join-Path $references 'tool-index.md'), ($rows -join "`n") + "`n",
    [Text.UTF8Encoding]::new($false))
Write-Host "Exported $($tools.Count) tools to $destinationPath"
