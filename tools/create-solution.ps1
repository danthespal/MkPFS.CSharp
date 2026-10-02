<#
.SYNOPSIS
    Create the MkPFS .NET 10 solution skeleton (Phase 1 layout).
.NOTES
    Historical record of the initial skeleton. Later changes (own mkpfs_zlib instead of
    XenoAtom.Interop.zlib, xunit.v3 on Microsoft Testing Platform) are in git history.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File create-solution.ps1 -Root D:\TOOLS\PS5\MkPFS.NET
#>
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$SdkVersion = "10.0.401"
)

$ErrorActionPreference = "Stop"

function Invoke-Dotnet {
    # Stop on the first failing dotnet command so a half-built solution is obvious.
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed with exit code $LASTEXITCODE" }
}

if (Test-Path (Join-Path $Root "MkPFS.slnx")) { throw "Solution already exists in $Root" }
New-Item -ItemType Directory -Force $Root | Out-Null
Set-Location $Root

# Repo-level files
Invoke-Dotnet new gitignore
Invoke-Dotnet new editorconfig
Invoke-Dotnet new globaljson --sdk-version $SdkVersion --roll-forward latestFeature
Invoke-Dotnet new sln -n MkPFS

@'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <Deterministic>true</Deterministic>
    <InvariantGlobalization>true</InvariantGlobalization>
    <Version>2.0.0-alpha.1</Version>
    <Authors>PSBrew</Authors>
    <PackageLicenseExpression>GPL-3.0-only</PackageLicenseExpression>
  </PropertyGroup>
</Project>
'@ | Set-Content -Encoding utf8 Directory.Build.props

New-Item -ItemType Directory -Force src, tests, native | Out-Null
@'
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
</Project>
'@ | Set-Content -Encoding utf8 src\Directory.Build.props

# Projects
Invoke-Dotnet new classlib -n MkPFS.Core -o src/MkPFS.Core
Invoke-Dotnet new classlib -n MkPFS.Build -o src/MkPFS.Build
Invoke-Dotnet new classlib -n MkPFS.Repair -o src/MkPFS.Repair
Invoke-Dotnet new console -n MkPFS.Cli -o src/MkPFS.Cli
Invoke-Dotnet new xunit -n MkPFS.Tests -o tests/MkPFS.Tests
Invoke-Dotnet new xunit -n MkPFS.Parity -o tests/MkPFS.Parity
Remove-Item src\MkPFS.Core\Class1.cs, src\MkPFS.Build\Class1.cs, src\MkPFS.Repair\Class1.cs

Invoke-Dotnet sln MkPFS.slnx add --solution-folder src src/MkPFS.Core src/MkPFS.Build src/MkPFS.Repair src/MkPFS.Cli
Invoke-Dotnet sln MkPFS.slnx add --solution-folder tests tests/MkPFS.Tests tests/MkPFS.Parity

# References: Core <- Build/Repair <- Cli; tests see everything
Invoke-Dotnet add src/MkPFS.Build reference src/MkPFS.Core
Invoke-Dotnet add src/MkPFS.Repair reference src/MkPFS.Core
Invoke-Dotnet add src/MkPFS.Cli reference src/MkPFS.Core src/MkPFS.Build src/MkPFS.Repair
Invoke-Dotnet add tests/MkPFS.Tests reference src/MkPFS.Core src/MkPFS.Build src/MkPFS.Repair
Invoke-Dotnet add tests/MkPFS.Parity reference src/MkPFS.Core src/MkPFS.Build src/MkPFS.Cli

# Packages
Invoke-Dotnet add src/MkPFS.Core package XenoAtom.Interop.zlib
Invoke-Dotnet add src/MkPFS.Cli package System.CommandLine
Invoke-Dotnet add src/MkPFS.Cli package Spectre.Console

# CLI: native AOT single binary named mkpfs
$cli = "src\MkPFS.Cli\MkPFS.Cli.csproj"
$xml = [xml](Get-Content $cli)
$pg = $xml.Project.PropertyGroup | Select-Object -First 1
foreach ($pair in @(@("AssemblyName", "mkpfs"), @("PublishAot", "true"), @("InvariantGlobalization", "true"))) {
    $node = $xml.CreateElement($pair[0]); $node.InnerText = $pair[1]; $pg.AppendChild($node) | Out-Null
}
$xml.Save((Resolve-Path $cli))

New-Item -ItemType Directory -Force tests\fixtures | Out-Null
Invoke-Dotnet build MkPFS.slnx
Invoke-Dotnet test MkPFS.slnx
Write-Host "Solution ready: $Root\MkPFS.slnx"
