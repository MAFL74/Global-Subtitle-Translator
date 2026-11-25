param()

$ErrorActionPreference = 'Stop'
$TaskName = 'GST GPU Worker'
$FirewallRuleName = 'GST GPU Worker (Jellyfin only)'
$InstallRoot = Join-Path $env:ProgramFiles 'Global Subtitle Translator\GPU Worker'
$DataRoot = Join-Path $env:ProgramData 'Global Subtitle Translator\GPU Worker'
$Port = 8765

function Write-Step([string] $Message) {
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Invoke-Checked([string] $Program, [string[]] $Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE: $Program $($Arguments -join ' ')"
    }
}

function Read-Required([string] $Prompt) {
    while ($true) {
        $value = (Read-Host $Prompt).Trim().Trim('"')
        if ($value) { return $value }
        Write-Host 'This value is required.' -ForegroundColor Yellow
    }
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell with Run as administrator, then run this installer again.'
}

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$WorkerSource = Join-Path $RepoRoot 'worker\gst_gpu_worker.py'
$EngineSource = Join-Path $RepoRoot 'local-gst\local_gst.py'
$RequirementsSource = Join-Path $RepoRoot 'local-gst\requirements.txt'
foreach ($requiredFile in @($WorkerSource, $EngineSource, $RequirementsSource)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required GST source file not found: $requiredFile. Run this script from a complete GST source package."
    }
}

Write-Host 'GST GPU worker setup. Subtitle translation stays local; no subtitle text is sent to a hosted AI service.'
$jellyfinIpText = Read-Required 'Jellyfin server IPv4 address'
$parsedIp = $null
if (-not [Net.IPAddress]::TryParse($jellyfinIpText, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Enter a valid IPv4 address for the Jellyfin server.'
}
$jellyfinIp = $parsedIp.ToString()

$modelInput = Read-Required 'Full path to the existing local translation model folder'
$ModelPath = [IO.Path]::GetFullPath($modelInput)
if (-not (Test-Path -LiteralPath $ModelPath -PathType Container)) {
    throw "Model directory does not exist: $ModelPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $ModelPath 'config.json') -PathType Leaf)) {
    throw 'The selected model folder must contain config.json. Select the model folder itself, not its parent.'
}

$portInput = Read-Host 'Worker port [8765]'
if ($portInput) {
    $parsedPort = 0
    if (-not [int]::TryParse($portInput, [ref]$parsedPort) -or $parsedPort -lt 1 -or $parsedPort -gt 65535) {
        throw 'Port must be a number between 1 and 65535.'
    }
    $Port = $parsedPort
}

$pythonLauncher = Get-Command py.exe -ErrorAction SilentlyContinue
if ($pythonLauncher) {
    $bootstrapPython = $pythonLauncher.Source
    $bootstrapPrefix = @('-3.12')
} else {
    $pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if (-not $pythonCommand) { throw 'Install 64-bit Python 3.12, then rerun this installer.' }
    $bootstrapPython = $pythonCommand.Source
    $bootstrapPrefix = @()
}

Write-Step 'Prepare the worker files'
New-Item -ItemType Directory -Path $InstallRoot, $DataRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'logs') -Force | Out-Null
Copy-Item -LiteralPath $WorkerSource -Destination (Join-Path $InstallRoot 'gst_gpu_worker.py') -Force
Copy-Item -LiteralPath $EngineSource -Destination (Join-Path $InstallRoot 'local_gst.py') -Force
Copy-Item -LiteralPath $RequirementsSource -Destination (Join-Path $InstallRoot 'requirements.txt') -Force

$venvPath = Join-Path $DataRoot 'venv'
$venvPython = Join-Path $venvPath 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    Write-Step 'Create an isolated Python environment'
    Invoke-Checked $bootstrapPython ($bootstrapPrefix + @('-m', 'venv', $venvPath))
}

Write-Step 'Install CUDA-enabled PyTorch and GST dependencies'
Write-Host 'PyTorch downloads several gigabytes. This is a one-time dependency install; translation data remains local.'
Invoke-Checked $venvPython @('-m', 'pip', 'install', '--upgrade', 'pip')
Invoke-Checked $venvPython @('-m', 'pip', 'install', 'torch', '--index-url', 'https://download.pytorch.org/whl/cu130')
Invoke-Checked $venvPython @('-m', 'pip', 'install', '-r', (Join-Path $InstallRoot 'requirements.txt'))

Write-Step 'Verify CUDA and the local model'
$gpuCheck = & $venvPython -c "import json,torch; ok=torch.cuda.is_available(); print(json.dumps({'cuda_available':ok,'gpu_name':torch.cuda.get_device_name(0) if ok else ''}))"
if ($LASTEXITCODE -ne 0) { throw 'Could not check the CUDA-enabled PyTorch environment.' }
$gpu = $gpuCheck | ConvertFrom-Json
if (-not $gpu.cuda_available) { throw 'PyTorch cannot access an NVIDIA GPU. Update the NVIDIA driver and rerun setup.' }
Write-Host "CUDA detected: $($gpu.gpu_name)" -ForegroundColor Green
Write-Host 'Model folder found. Model weights are loaded locally when the first translation starts.'

$workerScript = Join-Path $InstallRoot 'gst_gpu_worker.py'
$engine = Join-Path $InstallRoot 'local_gst.py'
$logFile = Join-Path $DataRoot 'logs\worker.log'
$historyFile = Join-Path $DataRoot 'jobs.json'
$arguments = @(
    "`"$workerScript`"",
    '--allowed-client', $jellyfinIp,
    '--port', [string]$Port,
    '--python', "`"$venvPython`"",
    '--engine', "`"$engine`"",
    '--model', "`"$ModelPath`"",
    '--log-file', "`"$logFile`"",
    '--history-file', "`"$historyFile`""
) -join ' '
$action = New-ScheduledTaskAction -Execute $venvPython -Argument $arguments -WorkingDirectory $InstallRoot
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

Write-Step 'Register a hidden startup task and restricted firewall rule'
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Runs the offline GST CUDA subtitle worker at Windows startup.' -Force | Out-Null
Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $FirewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress $jellyfinIp -Profile Any | Out-Null
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3

$task = Get-ScheduledTask -TaskName $TaskName
Write-Host "`nGST GPU worker task state: $($task.State)" -ForegroundColor Green
Write-Host "GPU: $($gpu.gpu_name)"
Write-Host "Jellyfin client allowed: $jellyfinIp"
Write-Host "Worker URL: http://127.0.0.1:$Port/"
Write-Host "Log: $logFile"
Write-Host 'The worker will start automatically after reboot and restart after a crash.'
