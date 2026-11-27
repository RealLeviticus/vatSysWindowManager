<#
.SYNOPSIS
  Builds the plugin and its tests into a scratch folder and runs the tests.

.DESCRIPTION
  Nothing is built into, or copied into, a vatSys Plugins folder. The tests load the installed
  vatSys.exe read-only to check that every internal member the plugin looks up exists.

.PARAMETER VatSysPath
  The vatSys install folder (the one containing bin\vatSys.exe).

.PARAMETER Layouts
  Optional folder of real layouts to load as a smoke test, for example
  "$env:USERPROFILE\Documents\vatSys Window Manager".

.PARAMETER Windows
  Also run the test that opens a few small plain windows on the desktop for a second or two and
  checks they are put back: place, size, minimised, maximised, hidden and stacking order.
#>
param(
    [string]$VatSysPath = 'C:\Program Files (x86)\vatSys',
    [string]$Layouts = '',
    [string]$Out = (Join-Path $env:TEMP 'vatSysWindowManager-tests'),
    [switch]$Windows
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'vatSysWindowManager.Tests.csproj'

dotnet build $project -c Release "-p:OutputPath=$Out\" "-p:VatSysPath=$VatSysPath" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$arguments = @($VatSysPath, $Layouts)
if ($Windows) { $arguments += '--windows' }
& (Join-Path $Out 'vatSysWindowManager.Tests.exe') @arguments
exit $LASTEXITCODE
