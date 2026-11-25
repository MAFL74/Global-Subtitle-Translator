param()

$ErrorActionPreference = 'Stop'
$TaskName = 'GST GPU Worker'
$FirewallRuleName = 'GST GPU Worker (Jellyfin only)'
$InstallRoot = Join-Path $env:ProgramFiles 'Global Subtitle Translator\GPU Worker'
$DataRoot = Join-Path $env:ProgramData 'Global Subtitle Translator\GPU Worker'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell with Run as administrator, then run this script again.'
}

Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host 'Removed the GST GPU startup task and its firewall rule.' -ForegroundColor Green

$removeFiles = (Read-Host 'Also delete the installed worker files, Python environment, and job history? The selected model is not in these folders. Type yes to delete').Trim()
if ($removeFiles -ieq 'yes') {
    foreach ($target in @($InstallRoot, $DataRoot)) {
        if (Test-Path -LiteralPath $target) {
            $resolved = (Resolve-Path -LiteralPath $target).Path
            if (-not $resolved.StartsWith($env:ProgramFiles, [StringComparison]::OrdinalIgnoreCase) -and -not $resolved.StartsWith($env:ProgramData, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to delete a path outside the install locations: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    Write-Host 'Removed the installed worker files and virtual environment. The model folder was left untouched.' -ForegroundColor Green
} else {
    Write-Host "Worker files and logs remain in $InstallRoot and $DataRoot. The selected model was left untouched."
}
