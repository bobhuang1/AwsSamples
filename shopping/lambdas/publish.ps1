# Builds both Lambda projects and zips them into terraform/dist/ for terraform apply.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "terraform\dist"
$publish = Join-Path $root "lambdas"

New-Item -ItemType Directory -Path $dist -Force | Out-Null

$projects = @(
    @{ Name = "ShoppingApi";     Project = "ShoppingApi\ShoppingApi.csproj"     },
    @{ Name = "OrderWorkflow";   Project = "OrderWorkflow\OrderWorkflow.csproj" }
)

foreach ($p in $projects) {
    $out = Join-Path $publish "$($p.Name)\publish"
    Write-Host "Publishing $($p.Name)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $publish $p.Project) -c Release -f net8.0 -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($p.Name)" }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath (Join-Path $dist "$($p.Name).zip") -Force
    Write-Host "  -> terraform/dist/$($p.Name).zip" -ForegroundColor Green
}

Write-Host "Done. Now run: cd terraform; terraform init; terraform apply" -ForegroundColor Green