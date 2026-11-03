# OpenWigiDash Widget Development - Deploy Script (branch main)
# Build e deploy do plugin OBS Studio para o OpenWigiDashManager.

param(
    [string]$Configuration = "Debug",
    [switch]$RestartManager = $false,
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = 'Stop'

$projectPath = "$PSScriptRoot"
$name = "OpenWigiDash.ObsPlugin"
$widgetsPath = Join-Path $env:LOCALAPPDATA 'OpenWigiDash\Widgets'
# Mesma pasta que a Loja de plugins usa (o id do pacote): assim o build de teste substitui o instalado pela loja,
# em vez de os dois carregarem e o segundo falhar com "Id ... já usado".
$folder = "OpenWigiDash.OBS"
$targetFolder = Join-Path $widgetsPath $folder
# Pasta dos deploys antigos deste script (com o nome da DLL); se ficar, carrega junto e dá o mesmo conflito.
$oldFolder = Join-Path $widgetsPath $name
$staging = Join-Path $projectPath "bin\publish\$Configuration"

Write-Host "=== OBS Studio (OpenWigiDash) Deploy ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host ""

# Passo 1: build (publish leva as dependências do plugin; SDK e MudBlazor ficam com o host)
Write-Host "[1/4] Building..." -ForegroundColor Green
if (-not $SkipBuild) {
    dotnet publish "$projectPath\ObsPlugin.csproj" -c $Configuration -o $staging
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
}
# Os arquivos web do MudBlazor vêm do host (o pacote do NuGet já tira; aqui tiramos à mão).
Remove-Item -LiteralPath (Join-Path $staging 'wwwroot\_content\MudBlazor') -Recurse -Force -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath (Join-Path $staging "$name.dll"))) {
    throw "Build output not found: $staging"
}
# A pasta não tem o nome da DLL: o host acha a entrada pelo plugin.json (o pacote do NuGet já leva um).
Set-Content -LiteralPath (Join-Path $staging 'plugin.json') -Value "{ `"entry`": `"$name.dll`" }" -Encoding UTF8

# Passo 2: o manager segura a DLL carregada; precisa estar fechado para trocar
Write-Host "[2/4] Checking for running OpenWigiDash Manager..." -ForegroundColor Green
# Só o Manager desta sessão do Windows: com mais de um usuário logado, o dos outros não é nosso (e nem dá para fechar).
$mySession = (Get-Process -Id $PID).SessionId
$manager = Get-Process -Name 'OpenWigiDashManager' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession }
if ($manager) {
    if ($RestartManager) {
        $managerPath = $manager[0].Path
        Write-Host "Stopping OpenWigiDash Manager to release file locks..." -ForegroundColor Yellow
        $manager | Stop-Process -Force
        Start-Sleep -Seconds 3
    }
    else {
        Write-Host "OpenWigiDash Manager is running. Use -RestartManager to close it automatically." -ForegroundColor Red
        exit 1
    }
}

# Passo 3: backup e cópia
Write-Host "[3/4] Copying widget files..." -ForegroundColor Green
foreach ($existing in @($targetFolder, $oldFolder)) {
    if (Test-Path $existing) {
        $backupFolder = Join-Path $env:LOCALAPPDATA ("OpenWigiDash\Backups\" + (Split-Path $existing -Leaf) + "\" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
        New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
        Copy-Item -Path (Join-Path $existing '*') -Destination $backupFolder -Recurse -Force
        Write-Host "Previous widget backed up to: $backupFolder" -ForegroundColor Cyan
        Remove-Item $existing -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $targetFolder -Recurse -Force
Write-Host "Widget deployed to: $targetFolder" -ForegroundColor Cyan

# Passo 4: reabrir o manager
if ($RestartManager -and $managerPath) {
    Write-Host "[4/4] Restarting OpenWigiDash Manager..." -ForegroundColor Green
    Start-Process $managerPath -ArgumentList '--tray'
}
else {
    Write-Host "[4/4] Open the OpenWigiDash Manager to load the widget." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Deploy Complete! ===" -ForegroundColor Green
