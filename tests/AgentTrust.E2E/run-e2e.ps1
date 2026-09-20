[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5188",
    [string]$Scenarios = "grocery,rate-limit",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$apiProject = Join-Path $repoRoot "src\AgentTrust.Api\AgentTrust.Api.csproj"
$migrationProject = Join-Path $repoRoot "src\AgentTrust.Data.Migrations.SqlServer\AgentTrust.Data.Migrations.SqlServer.csproj"
$e2eProject = Join-Path $repoRoot "tests\AgentTrust.E2E\AgentTrust.E2E.csproj"
$connection = $env:AGENTTRUST_E2E_SQLSERVER_CONNECTION

if ([string]::IsNullOrWhiteSpace($connection)) {
    throw "Set AGENTTRUST_E2E_SQLSERVER_CONNECTION to a dedicated SQL Server test database."
}
if ([string]::IsNullOrWhiteSpace($env:AGENTTRUST_E2E_SUBJECT) -or
    [string]::IsNullOrWhiteSpace($env:AGENTTRUST_E2E_PASSWORD)) {
    throw "Set AGENTTRUST_E2E_SUBJECT and AGENTTRUST_E2E_PASSWORD for the test user."
}

$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("agenttrust-e2e-" + [Guid]::NewGuid().ToString("N"))
$publishDirectory = Join-Path $runRoot "api"
$dataProtectionDirectory = Join-Path $runRoot "data-protection"
$stdoutPath = Join-Path $runRoot "api.stdout.log"
$stderrPath = Join-Path $runRoot "api.stderr.log"
New-Item -ItemType Directory -Path $publishDirectory, $dataProtectionDirectory -Force | Out-Null

function New-Base64Key {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes)
}

$apiProcess = $null
try {
    Write-Host "Building the exact API binary under test..."
    dotnet publish $apiProject -c $Configuration -o $publishDirectory --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "API publish failed." }

    $apiDll = Join-Path $publishDirectory "AgentTrust.Api.dll"
    $expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $apiDll).Hash

    Write-Host "Applying SQL Server migrations..."
    dotnet build $migrationProject -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Migration project build failed." }
    dotnet ef database update --project $migrationProject --startup-project $migrationProject --context AgentTrustDbContext --connection $connection --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }

    $uri = [Uri]$BaseUrl
    $env:ASPNETCORE_ENVIRONMENT = "E2E"
    $env:ASPNETCORE_URLS = $BaseUrl
    $env:ConnectionStrings__SqlServer = $connection
    $env:Authentication__Development__SigningKey = New-Base64Key
    $env:PURCHASE_AUTHORISATION_KEY = New-Base64Key
    $env:SERVICE_ACTION_AUTHORISATION_KEY = New-Base64Key
    $env:DATA_PROTECTION_KEYS_PATH = $dataProtectionDirectory
    $env:AGENTTRUST_E2E_BASE_URL = $BaseUrl
    $env:AGENTTRUST_E2E_EXPECTED_API_SHA256 = $expectedHash
    $env:AGENTTRUST_E2E_RESULTS = Join-Path $repoRoot "results\e2e"

    Write-Host "Starting isolated E2E API at $BaseUrl..."
    $apiProcess = Start-Process dotnet -ArgumentList @("`"$apiDll`"") -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $ready = $false
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        if ($apiProcess.HasExited) { throw "API exited during startup. See $stderrPath" }
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd('/') + "/health/live") -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "API did not become ready. See $stdoutPath and $stderrPath" }

    Write-Host "Running black-box scenarios against binary $expectedHash..."
    dotnet run --project $e2eProject -c $Configuration --no-restore -- "--scenarios=$Scenarios"
    if ($LASTEXITCODE -ne 0) { throw "One or more E2E scenarios failed. Review results\e2e and $stdoutPath" }
}
finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) {
        Write-Host "Stopping owned API process $($apiProcess.Id)..."
        Stop-Process -Id $apiProcess.Id -Force
        $apiProcess.WaitForExit()
    }
    Write-Host "E2E logs: $runRoot"
}
