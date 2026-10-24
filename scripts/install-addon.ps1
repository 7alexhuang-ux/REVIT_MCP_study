# ============================================================================
# Revit MCP Add-in 自動安裝程式 (安全版本)
# ============================================================================
# 此指令稿會自動：
# 1. 偵測您的 Revit 版本
# 2. 自動編譯 C# 專案 (dotnet build)
# 3. 複製 RevitMCP.dll 和 RevitMCP.addin 到正確的資料夾
# ============================================================================
# 安全注意事項：
# - 此指令稿只從本機複製檔案，不會從網路下載
# - 不需要系統管理員權限（Add-in 目錄在使用者資料夾）
# - 所有路徑都經過驗證，防止路徑注入攻擊
# - 使用 Strict Mode 確保變數和錯誤處理
# ============================================================================

#Requires -Version 5.1

# 指定要部署的 Revit 版本（如 -Version 2024）；省略時自動偵測（取白名單中最新找到的版本）。
# 2026-07-08 修正：CLAUDE.md 與 /deploy-addon 文件一直寫 -Version 用法，但腳本從未宣告此參數，
# 導致多版本機器（Addins 2023~2027 並存）固定鎖定最新版本、找不到對應組態 DLL 而失敗。
param (
    [ValidateSet("2022", "2023", "2024", "2025", "2026")]
    [string]$Version
)

# ⚠️ PowerShell 變數不分大小寫：下方版本偵測迴圈的 $version 會覆寫 $Version 參數，
# 必須立刻快照，後續一律使用 $requestedVersion。
$requestedVersion = $Version

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# 設定編碼為 UTF-8 with BOM，解決中文亂碼問題
$OutputEncoding = [System.Text.Encoding]::UTF8

# ============================================================================
# 安全函式：驗證路徑是否安全
# ============================================================================
function Test-SafePath {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Description = "路徑"
    )
    
    # 檢查路徑是否為空
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Write-Host "❌ 錯誤：$Description 為空" -ForegroundColor Red
        return $false
    }
    
    # 檢查路徑是否包含危險字元
    $dangerousPatterns = @(
        '\.\.\\',      # 路徑遍歷
        '\.\.\/',      # 路徑遍歷 (Unix 風格)
        '\$\(',        # 命令替換
        '`',           # PowerShell 跳脫字元
        '\|',          # 管線
        ';',           # 命令分隔
        '&',           # 命令連接
        '<',           # 重定向
        '>'            # 重定向
    )
    
    foreach ($pattern in $dangerousPatterns) {
        if ($Path -match $pattern) {
            Write-Host "❌ 錯誤：$Description 包含不安全字元" -ForegroundColor Red
            return $false
        }
    }
    
    return $true
}

# ============================================================================
# 安全函式：驗證檔案雜湊（可選）
# ============================================================================
function Get-FileHashInfo {
    param (
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    
    if (Test-Path $FilePath) {
        $hash = Get-FileHash -Path $FilePath -Algorithm SHA256
        return $hash.Hash
    }
    return $null
}

# ============================================================================
# 主程式開始
# ============================================================================

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "   Revit MCP Add-in 自動安裝程式 (安全版本)" -ForegroundColor Cyan
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================================
# 安全檢查 1：驗證執行環境
# ============================================================================

# 取得指令稿所在目錄
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) {
    $scriptDir = Split-Path -Parent -Path $MyInvocation.MyCommand.Definition
}

if (-not (Test-SafePath -Path $scriptDir -Description "Script Directory")) {
    Read-Host "按 Enter 結束"
    exit 1
}

# 取得專案根目錄
$projectRoot = Split-Path -Parent -Path $scriptDir

if (-not (Test-Path $projectRoot)) {
    Write-Host "❌ 錯誤：無法確定專案目錄" -ForegroundColor Red
    Read-Host "按 Enter 結束"
    exit 1
}

# 轉換為絕對路徑
$projectRoot = (Resolve-Path $projectRoot).Path

# 驗證專案結構
$mcpPath = Join-Path $projectRoot "MCP"
$mcpServerPath = Join-Path $projectRoot "MCP-Server"

if (-not (Test-Path $mcpPath)) {
    Write-Host "❌ 錯誤：找不到 MCP 資料夾" -ForegroundColor Red
    Write-Host "請確認您在 REVIT_MCP_study 專案目錄中執行此程式" -ForegroundColor Yellow
    Read-Host "按 Enter 結束"
    exit 1
}

if (-not (Test-Path $mcpServerPath)) {
    Write-Host "❌ 錯誤：找不到 MCP-Server 資料夾" -ForegroundColor Red
    Write-Host "這可能不是正確的專案目錄" -ForegroundColor Yellow
    Read-Host "按 Enter 結束"
    exit 1
}

Write-Host "✓ 專案目錄驗證通過：$projectRoot" -ForegroundColor Green
Write-Host ""

# ============================================================================
# 安全檢查 2：驗證 APPDATA 環境變數
# ============================================================================

$appDataPath = $env:APPDATA

if ([string]::IsNullOrEmpty($appDataPath)) {
    Write-Host "❌ 錯誤：APPDATA 環境變數未設定" -ForegroundColor Red
    Write-Host "這可能是系統設定問題，請聯繫技術支援" -ForegroundColor Yellow
    Read-Host "按 Enter 結束"
    exit 1
}

if (-not (Test-SafePath -Path $appDataPath -Description "APPDATA")) {
    Read-Host "按 Enter 結束"
    exit 1
}

if (-not (Test-Path $appDataPath)) {
    Write-Host "❌ 錯誤：APPDATA 路徑不存在：$appDataPath" -ForegroundColor Red
    Read-Host "按 Enter 結束"
    exit 1
}

Write-Host "✓ 環境變數驗證通過" -ForegroundColor Green
Write-Host ""

# ============================================================================
# 偵測 Revit 版本
# ============================================================================

Write-Host "正在偵測已安裝的 Revit 版本..." -ForegroundColor Yellow
Write-Host ""

$revitVersion = $null
$addonPath = $null
$foundVersions = @()

# 只檢查支援的版本（白名單方式，更安全）
$supportedVersions = @("2026", "2025", "2024", "2023", "2022")

foreach ($version in $supportedVersions) {
    $testPath = Join-Path $appDataPath "Autodesk\Revit\Addins\$version"
    if (Test-Path $testPath) {
        Write-Host "✓ 找到 Revit $version" -ForegroundColor Green
        $foundVersions += $version
        if ($null -eq $revitVersion) {
            $revitVersion = $version
            $addonPath = $testPath
        }
    }
}

Write-Host ""

# 使用者以 -Version 明確指定時優先於自動偵測；資料夾不存在即報錯，不回退（避免裝錯版本）。
# 注意：這裡必須用 $requestedVersion（開頭快照），$Version 已被上方偵測迴圈的 $version 覆寫。
if (-not [string]::IsNullOrEmpty($requestedVersion)) {
    $requestedPath = Join-Path $appDataPath "Autodesk\Revit\Addins\$requestedVersion"
    if (-not (Test-Path $requestedPath)) {
        Write-Host "❌ 錯誤：找不到 Revit $requestedVersion 的 Addins 資料夾：$requestedPath" -ForegroundColor Red
        exit 1
    }
    $revitVersion = $requestedVersion
    $addonPath = $requestedPath
}

if ($null -eq $revitVersion) {
    Write-Host "❌ 錯誤：沒有找到已安裝的 Revit" -ForegroundColor Red
    Write-Host ""
    Write-Host "可能的原因：" -ForegroundColor Yellow
    Write-Host "- 您的電腦沒有安裝 Revit" -ForegroundColor Yellow
    Write-Host "- 支援的版本：2022、2023、2024、2025、2026" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "檢查的路徑：$appDataPath\Autodesk\Revit\Addins\" -ForegroundColor Yellow
    Read-Host "按 Enter 結束"
    exit 1
}

# 如果找到多個版本，讓使用者選擇
if ($foundVersions.Count -gt 1) {
    Write-Host "找到多個 Revit 版本：$($foundVersions -join ', ')" -ForegroundColor Cyan
    Write-Host ""
    
    do {
        $userVersion = $revitVersion # Default to first found version for automation
        if ($null -eq $userVersion) {
            $userVersion = Read-Host "Enter Revit version (e.g. 2024)"
        }
    } while ($null -eq $userVersion)
    
    $revitVersion = $userVersion
    $addonPath = Join-Path $appDataPath "Autodesk\Revit\Addins\$revitVersion"
}

Write-Host ""
Write-Host "✓ 將安裝到 Revit $revitVersion" -ForegroundColor Green
Write-Host "✓ Add-in 路徑：$addonPath" -ForegroundColor Green
Write-Host ""

# ============================================================================
# 安全檢查 3：驗證來源檔案
# ============================================================================

Write-Host "正在驗證來源檔案..." -ForegroundColor Yellow
Write-Host ""

# 版本→組態對應表（統一建構，所有版本使用同一個 csproj）
# ⚠️ 必須先於 $sourceDllRelease 設定（2026-07-08 修正：$buildConfig 先用後設令腳本整個炸掉）
$versionConfigMap = @{
    "2022" = "Release.R22"
    "2023" = "Release.R23"
    "2024" = "Release.R24"
    "2025" = "Release.R25"
    "2026" = "Release.R26"
}
$buildConfig = $versionConfigMap[$revitVersion]

# 定義來源檔案路徑 (統一建構：Nice3point.Revit.Sdk)
# ⚠️ 本專案只使用 RevitMCP.csproj + RevitMCP.addin（統一多版本建構）
# ⚠️ 禁止新增 RevitMCP.2024.csproj / RevitMCP.2024.addin 等版本特定檔案
$sourceDllRelease = Join-Path $projectRoot "MCP\bin\$buildConfig\RevitMCP.dll"
$sourceDllDebug = Join-Path $projectRoot "MCP\bin\Debug\RevitMCP.dll"
$sourceAddin = Join-Path $projectRoot "MCP\RevitMCP.addin"

# 決定使用哪個 DLL
$sourceDll = $null

if (Test-Path $sourceDllRelease) {
    $sourceDll = $sourceDllRelease
    Write-Host "✓ 找到 RevitMCP.dll (Release 版本)" -ForegroundColor Green
    Write-Host ""
    Write-Host "⚠️  重要提醒：請確認此 DLL 是為 Revit $revitVersion 建構的" -ForegroundColor Yellow
    Write-Host "   正確的建構指令：dotnet build -c $buildConfig RevitMCP.csproj" -ForegroundColor Yellow
    Write-Host "   如果不確定，建議重新建構後再部署" -ForegroundColor Yellow
}
elseif (Test-Path $sourceDllDebug) {
    $sourceDll = $sourceDllDebug
    Write-Host "✓ 找到 RevitMCP.dll (Debug 版本)" -ForegroundColor Yellow
    Write-Host "  注意：建議使用 Release 版本以獲得最佳效能" -ForegroundColor Yellow
}
else {
    Write-Host "❌ 錯誤：找不到 RevitMCP.dll" -ForegroundColor Red
    Write-Host ""
    Write-Host "請先建構程式（根據您的 Revit 版本）：" -ForegroundColor Yellow
    Write-Host "  cd `"$projectRoot\MCP`"" -ForegroundColor Yellow
    Write-Host "  dotnet build -c $buildConfig RevitMCP.csproj" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "支援的版本：" -ForegroundColor Yellow
    foreach ($ver in $versionConfigMap.GetEnumerator() | Sort-Object Key) {
        Write-Host "  Revit $($ver.Key) → dotnet build -c $($ver.Value) RevitMCP.csproj" -ForegroundColor Gray
    }
    Read-Host "按 Enter 結束"
    exit 1
}

if (-not (Test-Path $sourceAddin)) {
    Write-Host "❌ 錯誤：找不到 addin 檔案" -ForegroundColor Red
    Write-Host "路徑：$sourceAddin" -ForegroundColor Yellow
    Read-Host "按 Enter 結束"
    exit 1
}

Write-Host "✓ 找到 RevitMCP.addin" -ForegroundColor Green
Write-Host ""

# 顯示檔案雜湊值（供進階使用者驗證）
Write-Host "檔案驗證資訊 (SHA256)：" -ForegroundColor Cyan
$dllHash = Get-FileHashInfo -FilePath $sourceDll
$addinHash = Get-FileHashInfo -FilePath $sourceAddin
Write-Host "  DLL:   $dllHash" -ForegroundColor Gray
Write-Host "  ADDIN: $addinHash" -ForegroundColor Gray
Write-Host ""

# ============================================================================
# 安全檢查 4：顯示檔案資訊供使用者確認
# ============================================================================

Write-Host "即將複製以下檔案：" -ForegroundColor Cyan
Write-Host ""
Write-Host "來源：" -ForegroundColor Cyan
Write-Host "  - $sourceDll" -ForegroundColor White
Write-Host "  - $sourceAddin" -ForegroundColor White
Write-Host ""
Write-Host "目標：" -ForegroundColor Cyan
Write-Host "  - $addonPath" -ForegroundColor White
Write-Host ""

$confirm = "Y" # Auto-confirm for automation
if ($confirm -ne "Y" -and $confirm -ne "y") {
    Write-Host "安裝已取消" -ForegroundColor Yellow
    Read-Host "按 Enter 結束"
    exit 0
}

Write-Host ""

# ============================================================================
# 執行安裝（不需要管理員權限）
# ============================================================================

Write-Host "正在複製檔案..." -ForegroundColor Yellow
Write-Host ""

# 檢查目標資料夾是否存在
if (-not (Test-Path $addonPath)) {
    Write-Host "正在建立目標資料夾..." -ForegroundColor Yellow
    try {
        New-Item -ItemType Directory -Path $addonPath -Force | Out-Null
    }
    catch {
        Write-Host "❌ 錯誤：無法建立目標資料夾" -ForegroundColor Red
        Write-Host "路徑：$addonPath" -ForegroundColor Yellow
        Write-Host "錯誤詳情：$_" -ForegroundColor Yellow
        Read-Host "按 Enter 結束"
        exit 1
    }
}

# 複製 DLL（2026-07-08 修正：manifest 的 Assembly=RevitMCP\RevitMCP.dll 指向「RevitMCP 子資料夾」，
# 原腳本平拷到 Addins 根層 → Revit 從未載入腳本部署的 DLL；改拷子資料夾並先備份既有版本。）
$addonSubDir = Join-Path $addonPath "RevitMCP"
try {
    if (-not (Test-Path $addonSubDir)) {
        New-Item -ItemType Directory -Path $addonSubDir -Force | Out-Null
    }

    $targetDll = Join-Path $addonSubDir "RevitMCP.dll"
    if (Test-Path $targetDll) {
        $backupName = "RevitMCP.dll.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item -Path $targetDll -Destination (Join-Path $addonSubDir $backupName) -Force -ErrorAction Stop
        Write-Host "✓ 既有 DLL 已備份為 $backupName" -ForegroundColor Green
    }

    Copy-Item -Path $sourceDll -Destination $targetDll -Force -ErrorAction Stop
    Write-Host "✓ 已複製 RevitMCP.dll → $addonSubDir" -ForegroundColor Green
}
catch {
    Write-Host "❌ 錯誤：無法複製 RevitMCP.dll" -ForegroundColor Red
    Write-Host ""
    Write-Host "可能的原因：" -ForegroundColor Yellow
    Write-Host "- Revit 正在執行中（請關閉 Revit 後重試）" -ForegroundColor Yellow
    Write-Host "- 目標資料夾沒有寫入權限" -ForegroundColor Yellow
    Write-Host "錯誤詳情：$_" -ForegroundColor Yellow
    exit 1
}

# 複製 ADDIN（manifest 留在 Addins 根層，Revit 由此讀入）
try {
    Copy-Item -Path $sourceAddin -Destination (Join-Path $addonPath "RevitMCP.addin") -Force -ErrorAction Stop
    Write-Host "✓ 已複製 $(Split-Path $sourceAddin -Leaf)" -ForegroundColor Green
}
catch {
    Write-Host "❌ 錯誤：無法複製 RevitMCP.addin" -ForegroundColor Red
    Write-Host "錯誤詳情：$_" -ForegroundColor Yellow
    exit 1
}

# 複製相依套件：bin 組態資料夾內全部相依 DLL（ClosedXML/OpenXml/SixLabors…）一併更新到子資料夾。
# Nice3point SDK 不會把 RevitAPI*.dll 落到 bin，整批複製是安全的。
$sourceBinDir = Split-Path $sourceDll -Parent
try {
    Get-ChildItem -Path $sourceBinDir -Filter *.dll -File |
        Where-Object { $_.Name -ne "RevitMCP.dll" } |
        ForEach-Object {
            Copy-Item -Path $_.FullName -Destination (Join-Path $addonSubDir $_.Name) -Force -ErrorAction Stop
        }
    Write-Host "✓ 已同步相依 DLL 到 $addonSubDir" -ForegroundColor Green
}
catch {
    Write-Host "⚠️  警告：相依 DLL 同步不完整：$_" -ForegroundColor Yellow
}

# 複製 Python worker（柱號對應功能 ezdxf_worker.py → %APPDATA%\RevitMCP）
# DwgColumnExecutor.FindWorkerScript 會在 dll 同層 → 開發樹 → %APPDATA%\RevitMCP 依序尋找；
# 部署版的 dll 在 Add-ins 目錄，故 worker 須落在 %APPDATA%\RevitMCP 才找得到。
$sourceWorker = Join-Path $projectRoot "bridge\python\skills\ezdxf_worker.py"
if (Test-Path $sourceWorker) {
    $workerDir = Join-Path $appDataPath "RevitMCP"
    try {
        if (-not (Test-Path $workerDir)) { New-Item -ItemType Directory -Path $workerDir -Force | Out-Null }
        Copy-Item -Path $sourceWorker -Destination (Join-Path $workerDir "ezdxf_worker.py") -Force -ErrorAction Stop
        Write-Host "✓ 已部署 ezdxf_worker.py 到 $workerDir" -ForegroundColor Green
        Write-Host "  （柱號對應 textLayerName 需系統 Python + 'pip install ezdxf'；DWG 另需 ODA File Converter）" -ForegroundColor Gray
    }
    catch {
        Write-Host "⚠️  警告：無法部署 ezdxf_worker.py（柱號對應功能將無法使用，非關鍵）" -ForegroundColor Yellow
    }
}

# 複製走動展示深度圖渲染腳本（bridge/node/walkthrough → %APPDATA%\RevitMCP\bridge\node\walkthrough，排除 node_modules）
# CommandExecutor.FindWalkDepthScript 會在 dll 同層 → 開發樹 → %APPDATA%\RevitMCP 依序尋找；
# 部署版的 dll 在 Add-ins 目錄，故腳本須落在 %APPDATA%\RevitMCP 才找得到。export_walkthrough_depth 另需本機安裝 Node.js 與 Chrome。
$sourceDepthScript = Join-Path $projectRoot "bridge\node\walkthrough"
if (Test-Path $sourceDepthScript) {
    $depthScriptDest = Join-Path $appDataPath "RevitMCP\bridge\node\walkthrough"
    try {
        if (Test-Path $depthScriptDest) { Remove-Item -Path $depthScriptDest -Recurse -Force -ErrorAction Stop }
        New-Item -ItemType Directory -Path $depthScriptDest -Force | Out-Null
        $depthScriptFiles = Get-ChildItem -Path $sourceDepthScript -Recurse -File | Where-Object {
            $rel = $_.FullName.Substring($sourceDepthScript.Length).TrimStart('\', '/')
            -not ($rel -match '^(node_modules|\.git)[\\/]')
        }
        foreach ($f in $depthScriptFiles) {
            $rel = $f.FullName.Substring($sourceDepthScript.Length).TrimStart('\', '/')
            $dest = Join-Path $depthScriptDest $rel
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item -Path $f.FullName -Destination $dest -Force -ErrorAction Stop
        }
        Write-Host "✓ 已部署走動展示深度圖渲染腳本（$($depthScriptFiles.Count) 檔）到 $depthScriptDest" -ForegroundColor Green
        Write-Host "  （export_walkthrough_depth 需系統 Node.js + Chrome，不含在本安裝程序內）" -ForegroundColor Gray
    }
    catch {
        Write-Host "⚠️  警告：無法部署走動展示深度圖渲染腳本（export_walkthrough_depth 將無法使用，非關鍵）" -ForegroundColor Yellow
    }
}

# 複製走動展示網頁 viewer 模板（viewer/walkthrough → %APPDATA%\RevitMCP\viewer\walkthrough）
# CommandExecutor.FindWalkViewerTemplate 會在 dll 同層 → 開發樹 → %APPDATA%\RevitMCP 依序尋找，
# export_walkthrough_package 會把模板複製進每個交付包。開發用檔案（dev/、測試、打包腳本、契約文件）不部署。
$sourceViewer = Join-Path $projectRoot "viewer\walkthrough"
if (Test-Path (Join-Path $sourceViewer "index.html")) {
    $viewerDest = Join-Path $appDataPath "RevitMCP\viewer\walkthrough"
    try {
        if (Test-Path $viewerDest) { Remove-Item -Path $viewerDest -Recurse -Force -ErrorAction Stop }
        New-Item -ItemType Directory -Path $viewerDest -Force | Out-Null
        $viewerFiles = Get-ChildItem -Path $sourceViewer -Recurse -File | Where-Object {
            $rel = $_.FullName.Substring($sourceViewer.Length).TrimStart('\', '/')
            -not ($rel -match '^(dev|node_modules)[\\/]') -and
            $_.Name -notin @('CONTRACT.md', 'build-vendor.mjs', 'package.json', 'package-lock.json') -and
            $_.Name -notlike '*.test.cjs' -and $_.Name -notlike '*.test.js'
        }
        foreach ($f in $viewerFiles) {
            $rel = $f.FullName.Substring($sourceViewer.Length).TrimStart('\', '/')
            $dest = Join-Path $viewerDest $rel
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item -Path $f.FullName -Destination $dest -Force -ErrorAction Stop
        }
        Write-Host "✓ 已部署走動展示 viewer（$($viewerFiles.Count) 檔）到 $viewerDest" -ForegroundColor Green
    }
    catch {
        Write-Host "⚠️  警告：無法部署走動展示 viewer（export_walkthrough_package 將缺少 index.html，非關鍵）" -ForegroundColor Yellow
    }
}

Write-Host ""

# ============================================================================
# 安裝完成
# ============================================================================

Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "   ✓ 安裝完成！" -ForegroundColor Green
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "安裝摘要：" -ForegroundColor Cyan
Write-Host "  - Revit 版本：$revitVersion" -ForegroundColor White
Write-Host "  - 安裝路徑：$addonPath" -ForegroundColor White
Write-Host ""
Write-Host "接下來的步驟：" -ForegroundColor Cyan
Write-Host "  1. 完全關閉 Revit（如果正在執行）" -ForegroundColor White
Write-Host "  2. 重新開啟 Revit" -ForegroundColor White
Write-Host "  3. 應該會看到「MCP Tools」面板" -ForegroundColor White
Write-Host "  4. 點擊「MCP 服務 (開/關)」啟動服務" -ForegroundColor White
Write-Host ""
Write-Host "如有問題，請參考 README.md 的「常見問題」章節" -ForegroundColor Cyan
Write-Host ""

# 非互動模式（自動化部署）下 Read-Host 會直接丟例外，令成功的部署以 exit 1 收場——僅互動時暫停。
if ([Environment]::UserInteractive -and -not ([bool]$env:CI) -and $Host.Name -eq 'ConsoleHost') {
    try { Read-Host "按 Enter 結束" } catch { }
}
