$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET SDK is not installed. Install .NET SDK 9 from https://dot.net and run this script again."
}
if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error "Node.js and npm are required to build the web interface. Install Node.js 22 or later, then run this script again."
}

Write-Host "Building the web interface..."
Push-Location "$root\web\studio"
npm install
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
npm run build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Pop-Location

Write-Host "Starting IL2CPP Recovery Studio..."
$server = Start-Process -FilePath "dotnet" -ArgumentList "run","--project","$root\src\Il2CppRecover.Web\Il2CppRecover.Web.csproj" -PassThru -NoNewWindow
$url = "http://127.0.0.1:5088"
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    try {
        Invoke-WebRequest -Uri "$url/api/health" -UseBasicParsing | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $ready) {
    Write-Error "The server did not become ready. Stop it with: Stop-Process -Id $($server.Id)"
}
Write-Host "IL2CPP Recovery Studio is ready at $url"
Write-Host "Stop the server with: Stop-Process -Id $($server.Id)"
Write-Host "Stopping the server does not delete projects."
Start-Process $url
Wait-Process -Id $server.Id
