# RASD Payload Builder v2.1
# Run as Administrator from the RASD\ folder

param()
$ErrorActionPreference = "Stop"
$OriginalDir = $PWD.Path
$RasdRoot    = $PSScriptRoot
$StagingDir  = Join-Path $RasdRoot "RASD_Staging\Payload_Staging"
$OutputZip   = Join-Path $RasdRoot "RASD_Setup\Payload.zip"

function Run-InDir($dir, $exe, $cmdArgs) {
    # Use cmd.exe /c so that .cmd scripts (npm.cmd, etc.) are resolved correctly on Windows
    $result = Start-Process -FilePath "cmd.exe" -ArgumentList "/c $exe $cmdArgs" `
        -WorkingDirectory $dir -Wait -PassThru -NoNewWindow
    return $result.ExitCode
}

Write-Host "RASD Payload Builder v2.1" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan

# 1: Clean staging
Write-Host ""
Write-Host "[1/7] Cleaning staging directory..." -ForegroundColor Yellow
if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
Write-Host "      Done." -ForegroundColor Gray

# 2: Build Web Client
Write-Host "[2/7] Building React Web Dashboard..." -ForegroundColor Yellow
$WebClientDir = Join-Path $RasdRoot "Web_Client"
Run-InDir $WebClientDir "npm" "install --silent" | Out-Null
$buildCode = Run-InDir $WebClientDir "npm" "run build"
if ($buildCode -ne 0) {
    Write-Host "      ERROR: React build failed (exit $buildCode)." -ForegroundColor Red
    exit 1
}
Write-Host "      OK: Web client built." -ForegroundColor Green

# 3: Stage API Server
Write-Host "[3/7] Staging API Server..." -ForegroundColor Yellow
$ApiSrc  = Join-Path $RasdRoot "API_Server"
$ApiDest = Join-Path $StagingDir "API_Server"
New-Item -ItemType Directory -Path (Join-Path $ApiDest "db")     -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ApiDest "routes") -Force | Out-Null

Copy-Item (Join-Path $ApiSrc "server.js")      $ApiDest -Force
Copy-Item (Join-Path $ApiSrc "setup_admin.js") $ApiDest -Force
Copy-Item (Join-Path $ApiSrc "package.json")   $ApiDest -Force
Copy-Item (Join-Path $ApiSrc ".env")           $ApiDest -Force
Copy-Item (Join-Path $ApiSrc "db\pool.js")     (Join-Path $ApiDest "db") -Force
Copy-Item (Join-Path $ApiSrc "db\schema.sql")  (Join-Path $ApiDest "db") -Force
Get-ChildItem (Join-Path $ApiSrc "routes\*.js") | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $ApiDest "routes") -Force
}

$npmCode = Run-InDir $ApiDest "npm" "install --omit=dev --silent"
if ($npmCode -ne 0) {
    Write-Host "      ERROR: npm install failed for API_Server (exit $npmCode)." -ForegroundColor Red
    exit 1
}
Write-Host "      OK: API Server staged with node_modules." -ForegroundColor Green

# 4: Stage Web Client dist
Write-Host "[4/7] Staging Web Client build output..." -ForegroundColor Yellow
$WebDest = Join-Path $StagingDir "Web_Client\dist"
New-Item -ItemType Directory -Path $WebDest -Force | Out-Null
Copy-Item (Join-Path $WebClientDir "dist\*") $WebDest -Recurse -Force
Write-Host "      OK: Web client dist staged." -ForegroundColor Green

# 5: Stage compiled executables
Write-Host "[5/7] Staging compiled executables..." -ForegroundColor Yellow

function Stage-Exe($SearchDir, $ExeName) {
    $found = Get-ChildItem $SearchDir -Filter $ExeName -Recurse -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -like "*\Release\*" -or $_.FullName -like "*\Release\net*" } |
             Select-Object -First 1
    if (-not $found) {
        $found = Get-ChildItem $SearchDir -Filter $ExeName -Recurse -ErrorAction SilentlyContinue |
                 Select-Object -First 1
    }
    if (-not $found) {
        Write-Host ("      FATAL: " + $ExeName + " not found under " + $SearchDir + " - build it first!") -ForegroundColor Red
        exit 1
    }

    # --- STALE-BINARY GUARD ---
    # Abort if the EXE is older than the newest source file in its project.
    # (A stale binary was silently shipped in V6 — this must never happen again.)
    $newestSrc = Get-ChildItem $SearchDir -Recurse -Include *.cs,*.xaml -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
                 Sort-Object LastWriteTime -Descending |
                 Select-Object -First 1
    if ($newestSrc -and ($found.LastWriteTime -lt $newestSrc.LastWriteTime)) {
        Write-Host ""
        Write-Host ("      FATAL: STALE BINARY DETECTED for " + $ExeName) -ForegroundColor Red
        Write-Host ("             EXE  built : " + $found.LastWriteTime + "  (" + $found.FullName + ")") -ForegroundColor Red
        Write-Host ("             Source edit: " + $newestSrc.LastWriteTime + "  (" + $newestSrc.FullName + ")") -ForegroundColor Red
        Write-Host  "             Rebuild this project in Release before packaging. Aborting." -ForegroundColor Red
        exit 1
    }

    Copy-Item $found.FullName (Join-Path $StagingDir $ExeName) -Force
    Write-Host ("      OK: " + $ExeName + " -> " + $found.FullName + "  (built " + $found.LastWriteTime + ")") -ForegroundColor Green
}

# RASD.exe (launcher) — built from RASD_Launcher project
Stage-Exe (Join-Path $RasdRoot "RASD_Launcher") "RASD.exe"
# RASD_Relay.exe — renamed Admin_Watcher (AssemblyName changed in .csproj)
Stage-Exe (Join-Path $RasdRoot "Admin_Watcher") "RASD_Relay.exe"
Stage-Exe (Join-Path $RasdRoot "Client_Connector\RASD_Connector") "RASD_Connector.exe"
Stage-Exe (Join-Path $RasdRoot "Client_Connector\RASD_Protector") "RASD_Protector.exe"
Stage-Exe (Join-Path $RasdRoot "RASD_Uninstaller")                "RASD_Uninstaller.exe"
# RASD_ClientTray.exe — endpoint status tray app (Phase 2)
Stage-Exe (Join-Path $RasdRoot "RASD_ClientTray")                 "RASD_ClientTray.exe"
# RASD_Shell.exe — WebView2 native dashboard shell (Phase 4 B1)
Stage-Exe (Join-Path $RasdRoot "RASD_Shell")                      "RASD_Shell.exe"
# WebView2 SDK DLLs + native loader must travel next to the shell exe
$shellBin = Join-Path $RasdRoot "RASD_Shell\bin\Release"
foreach ($dll in @("Microsoft.Web.WebView2.Core.dll","Microsoft.Web.WebView2.Wpf.dll","WebView2Loader.dll")) {
    $src = Join-Path $shellBin $dll
    if (Test-Path $src) { Copy-Item $src (Join-Path $StagingDir $dll) -Force; Write-Host ("      OK: " + $dll) -ForegroundColor Green }
    else { Write-Host ("      FATAL: " + $dll + " missing from RASD_Shell output.") -ForegroundColor Red; exit 1 }
}

# 6: Stage assets (ICO, etc.)
Write-Host "[6/7] Staging assets..." -ForegroundColor Yellow
$AssetsSrc  = Join-Path $RasdRoot "Assets"
$AssetsDest = Join-Path $StagingDir "assets"
New-Item -ItemType Directory -Path $AssetsDest -Force | Out-Null

# Generate icon first if not present
$IcoFile = Join-Path $AssetsSrc "rasd.ico"
if (-not (Test-Path $IcoFile)) {
    Write-Host "      Generating rasd.ico..." -ForegroundColor Yellow
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RasdRoot "CREATE_ICON.ps1")
}

if (Test-Path $AssetsSrc) {
    Get-ChildItem $AssetsSrc -File | ForEach-Object {
        Copy-Item $_.FullName $AssetsDest -Force
        Write-Host ("      OK: assets\" + $_.Name) -ForegroundColor Green
    }
} else {
    Write-Host "      SKIP: No Assets folder found." -ForegroundColor Gray
}

# 6b: Generate the shared discovery key (A1). A fresh 32-byte key is embedded in
# every build so the admin and all clients from the same installer share it. The
# installer copies it to C:\ProgramData\RASD\node.key and ACL-locks it.
Write-Host "[6b] Generating shared discovery key (node.key)..." -ForegroundColor Yellow
$keyBytes = New-Object 'System.Byte[]' 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($keyBytes)
$keyB64 = [System.Convert]::ToBase64String($keyBytes)
$KeyFile = Join-Path $StagingDir "node.key"
[System.IO.File]::WriteAllText($KeyFile, $keyB64)
Write-Host "      OK: node.key ($($keyB64.Length) b64 chars) staged at payload root." -ForegroundColor Green

# 7: Create Payload.zip
Write-Host "[7/7] Creating Payload.zip..." -ForegroundColor Yellow
if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $StagingDir, $OutputZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipMB = [Math]::Round((Get-Item $OutputZip).Length / 1MB, 1)

Write-Host ""
Write-Host "BUILD COMPLETE" -ForegroundColor Green
Write-Host "--------------" -ForegroundColor Green
Write-Host ("Output : " + $OutputZip) -ForegroundColor White
Write-Host ("Size   : " + $zipMB + " MB") -ForegroundColor White
Write-Host ""
Write-Host "NEXT: Open RASD_Setup.csproj in Visual Studio 2022" -ForegroundColor Cyan
Write-Host "      Build -> Release -> Any CPU" -ForegroundColor Cyan
Write-Host "      The installer EXE will be at:" -ForegroundColor Cyan
Write-Host "      RASD_Setup\bin\Release\RASD_Setup.exe" -ForegroundColor White
Write-Host ""
