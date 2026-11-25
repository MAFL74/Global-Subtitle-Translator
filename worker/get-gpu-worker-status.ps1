param()

$ErrorActionPreference = 'Continue'
$TaskName = 'GST GPU Worker'
$LogFile = Join-Path $env:ProgramData 'Global Subtitle Translator\GPU Worker\logs\worker.log'
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host 'GST GPU Worker is not installed.' -ForegroundColor Yellow
    exit 1
}

$info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
Write-Host "Task: $TaskName"
Write-Host "State: $($task.State)"
if ($info) {
    Write-Host "Last run: $($info.LastRunTime)"
    Write-Host "Last result: $($info.LastTaskResult)"
    Write-Host "Next run: $($info.NextRunTime)"
}

if (Test-Path -LiteralPath $LogFile) {
    Write-Host "`nRecent worker log: $LogFile" -ForegroundColor Cyan
    Get-Content -LiteralPath $LogFile -Tail 30
} else {
    Write-Host "`nNo worker log yet: $LogFile" -ForegroundColor Yellow
}

$portLine = if (Test-Path -LiteralPath $LogFile) { Select-String -LiteralPath $LogFile -Pattern 'lytter på .*:(\d+)' | Select-Object -Last 1 } else { $null }
if ($portLine -and $portLine.Matches.Count -gt 0) {
    $port = $portLine.Matches[0].Groups[1].Value
    Write-Host "`nLocal dashboard: http://127.0.0.1:$port/"
}
