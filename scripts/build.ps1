param([string]$Runtime = 'win-x64', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet restore SehtMCP.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build SehtMCP.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        dotnet test SehtMCP.sln -c Release --no-build --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    $destination = Join-Path $projectRoot ('artifacts/publish/' + $Runtime)
    dotnet publish src/SehtMcp/SehtMcp.csproj -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true -o $destination
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath LICENSE, README.md, THIRD_PARTY_NOTICES.md, seht.example.json, CONTRIBUTING.md, CHANGELOG.md -Destination $destination
    Copy-Item -LiteralPath docs, clients, examples -Destination $destination -Recurse -Force
    Write-Output ('Published self-contained SehtMCP: ' + $destination)
} finally { Pop-Location }
