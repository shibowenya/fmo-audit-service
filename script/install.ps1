# ============================================================
# FMO Audit Service（修改版，含白名单功能 © BG2GZK）一键安装脚本 (Windows)
# 用法: powershell -ExecutionPolicy Bypass -File install.ps1 -Package <zip路径> [-InstallDir <目录>]
#   示例: .\install.ps1 -Package .\fmo-audit-service-win-x64.zip
# 行为: 解压 → %LOCALAPPDATA%\FMOAuditService\ → 防火墙放行 9527 →
#       注册计划任务 fmo-fas（登录自启 + 崩溃自动重启）→ 立即启动
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$InstallDir = "$env:LOCALAPPDATA\FMOAuditService",
    [int]$Port = 9527
)
$ErrorActionPreference = "Stop"
$TaskName = "fmo-fas"   # 固定任务名，与 OTA 升级提示（Start-ScheduledTask fmo-fas）对齐

function Info($m) { Write-Host "[+] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[!] $m" -ForegroundColor Yellow }

# ---- 取包 ----
if (-not (Test-Path $Package)) { throw "包不存在: $Package" }
$Tmp = Join-Path $env:TEMP ("fas-install-" + [Guid]::NewGuid().ToString("N"))
try {
    Info "解压 $Package"
    Expand-Archive -Path $Package -DestinationPath $Tmp -Force
    $Exe = Join-Path $Tmp "fmo-audit-service.exe"
    if (-not (Test-Path $Exe)) { throw "包内未找到 fmo-audit-service.exe" }

    # ---- 停旧服务（升级/重装场景）----
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Get-Process fmo-audit-service -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    # ---- 安装 ----
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item $Exe (Join-Path $InstallDir "fmo-audit-service.exe") -Force
    Info "已安装到 $InstallDir"

    # ---- 防火墙放行（需管理员；失败仅提示，不影响安装）----
    try {
        netsh advfirewall firewall add rule name="FMO Audit Service" dir=in action=allow `
            protocol=TCP localport=$Port | Out-Null
        Info "防火墙已放行 TCP $Port"
    } catch { Warn "防火墙放行失败（可能无管理员权限），请手动放行 TCP $Port" }

    # ---- 计划任务：登录自启 + 崩溃自动重启（无执行时长限制）----
    $action   = New-ScheduledTaskAction -Execute (Join-Path $InstallDir "fmo-audit-service.exe") -WorkingDirectory $InstallDir
    $trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $settings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Info "计划任务 $TaskName 已注册并启动"
} finally {
    Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Info "安装完成！本机访问 http://127.0.0.1:$Port ，局域网访问 http://<本机IP>:$Port"
Info "常用: Start-ScheduledTask / Stop-ScheduledTask $TaskName"
Info "升级: Stop-ScheduledTask $TaskName; & `"$InstallDir\fmo-audit-service.exe`" --update; Start-ScheduledTask $TaskName"
