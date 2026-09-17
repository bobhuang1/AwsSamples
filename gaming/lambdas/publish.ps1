# Builds the GameBackend Lambda zip into terraform/dist/ for terraform apply.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "terraform\dist"
$proj = Join-Path $root "lambdas\GameBackend\GameBackend.csproj"

New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host "Publishing GameBackend..." -ForegroundColor Cyan
dotnet publish $proj -c Release -f net8.0 -o (Join-Path $root "lambdas\GameBackend\publish") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Compress-Archive -Path (Join-Path $root "lambdas\GameBackend\publish\*") -DestinationPath (Join-Path $dist "GameBackend.zip") -Force

Write-Host "  -> terraform/dist/GameBackend.zip" -ForegroundColor Green
Write-Host "Done. Now run: cd terraform; terraform init; terraform apply" -ForegroundColor Green