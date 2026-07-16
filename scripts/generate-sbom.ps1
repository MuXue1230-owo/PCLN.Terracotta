#Requires -Version 7
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$out = Join-Path $root "artifacts/sbom"
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "Generating .NET SBOM via `dotnet list package`..."
dotnet list PCLN.Terracotta.slnx package --include-transitive |
    Out-File -FilePath (Join-Path $out "dotnet-packages.txt") -Encoding utf8

Write-Host "Generating Cargo SBOM via cargo tree..."
cargo tree --manifest-path src/Terracotta.Helper/Cargo.toml --locked --prefix none |
    Out-File -FilePath (Join-Path $out "cargo-tree.txt") -Encoding utf8

if (Get-Command cargo-cyclonedx -ErrorAction SilentlyContinue) {
    cargo cyclonedx --manifest-path src/Terracotta.Helper/Cargo.toml --format json --output-cdx (Join-Path $out "terracotta-helper.cdx.json")
} else {
    @"
# Optional: install cargo-cyclonedx for CycloneDX JSON
# cargo install cargo-cyclonedx
"@ | Out-File -FilePath (Join-Path $out "README-cyclonedx.txt") -Encoding utf8
}

Write-Host "SBOM artifacts written to $out"
