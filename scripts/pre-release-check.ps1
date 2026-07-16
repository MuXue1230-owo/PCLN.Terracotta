#Requires -Version 7
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step([string]$Name, [scriptblock]$Action) {
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
        throw "Step failed: $Name (exit $LASTEXITCODE)"
    }
}

Step "Helper cargo fmt" {
    cargo fmt --manifest-path src/Terracotta.Helper/Cargo.toml -- --check
}
Step "Helper cargo clippy" {
    cargo clippy --manifest-path src/Terracotta.Helper/Cargo.toml --locked --all-targets -- -D warnings
}
Step "Helper cargo test" {
    cargo test --manifest-path src/Terracotta.Helper/Cargo.toml --locked --all-targets
}
Step ".NET tests" {
    dotnet test PCLN.Terracotta.slnx -c Release --nologo --disable-build-servers -m:1
}
Step "Plugin package build (no native required)" {
    dotnet build src/PCLN.Terracotta.Plugin/PCLN.Terracotta.Plugin.csproj -c Release --nologo
}

Write-Host ""
Write-Host "Pre-release checks passed." -ForegroundColor Green
Write-Host "Optional with natives: dotnet build ... -p:TerracottaRequireNativeHelpers=true"
Write-Host "Optional EasyTier gate: -p:TerracottaRequireEasyTier=true"
