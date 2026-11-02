# Windows scheduled task for tools/nightly-tests.ps1: daily at -At for the current user, at below normal priority and
# without a window, and as soon as the machine is back if it was off then (the run first waits until the machine has been
# up -BootDelayMinutes). No admin rights; it runs only while the user is logged on. The script is copied to -Root, so
# the task does not depend on what is checked out in -Repo; run this again to take a newer one. -Uninstall removes the
# task and leaves the reports.
#
# usage: pwsh tools/install-nightly-task.ps1 -Client <ROO_PC folder> [-Repo <checkout>] [-Root <folder>] [-At 06:00] [-NotifyEnv <file>]
#        pwsh tools/install-nightly-task.ps1 -Uninstall
param(
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [string]$Client = $env:RO3_CLIENT_DIR,
    [string]$Root,
    [string]$Data,
    [string]$At = '06:00',
    [string]$NotifyEnv = (Join-Path $HOME '.codex/notify-secrets.env'),
    [int]$BootDelayMinutes = 30,
    [string]$TaskName = 'RO3Emu nightly tests',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Output "removed the task '$TaskName'"
    return
}
if (-not $Client -or -not (Test-Path -LiteralPath $Client)) { throw "Pass -Client with your ROO_PC folder (now '$Client')." }
if (-not $Root) { $Root = Join-Path (Split-Path $Repo -Parent) 'ro3emu-nightly' }
if (-not $Data) { $Data = Join-Path $Repo 'data' }
New-Item -ItemType Directory -Force $Root | Out-Null
$script = Join-Path $Root 'nightly-tests.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'nightly-tests.ps1') -Destination $script -Force

$pwsh = (Get-Command pwsh).Source
# conhost --headless: a scheduled console program opens no window at all, where -WindowStyle Hidden still flashes one.
$arguments = "--headless `"$pwsh`" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" " +
    "-Repo `"$Repo`" -Client `"$Client`" -Root `"$Root`" -Data `"$Data`" -NotifyEnv `"$NotifyEnv`" -BootDelayMinutes $BootDelayMinutes"
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument $arguments -WorkingDirectory $Root
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RunOnlyIfNetworkAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 3) -Priority 7
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Output "task '$TaskName': daily at $At; reports in $Root\runs, the last one in $Root\latest.txt"
