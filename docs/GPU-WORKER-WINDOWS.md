# Windows GPU worker setup

This guide installs the GST CUDA worker as a hidden Windows startup task. It keeps running after the setup window closes and starts again after a reboot. Windows Task Scheduler restarts the worker if it exits.

## Requirements

- Windows 10 or 11, 64-bit.
- An NVIDIA GPU and a current NVIDIA driver.
- Python 3.12 (64-bit). The installer creates a separate virtual environment, so it does not change another Python environment.
- A local GST translation model directory that contains `config.json` and its tokenizer and weight files. The model is not included in the repository.
- The Jellyfin server's LAN IP address.
- Administrator permission for the firewall rule and startup task.

## Install

1. Download or clone this repository on the GPU computer and extract it.
2. Open PowerShell as Administrator in the repository folder.
3. Run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\worker\install-gpu-worker.ps1
   ```

4. Enter the Jellyfin server IP, local model folder, and listen port when prompted. Port `8765` is the default.
5. Wait for the installer to create the Python environment and verify that PyTorch detects the NVIDIA GPU. The CUDA-enabled PyTorch wheel is installed from the official PyTorch wheel index; the remaining Python dependencies are installed from `local-gst/requirements.txt`.
6. The installer creates a Windows Firewall rule for the selected Jellyfin IP only and starts the worker automatically.

The model folder must be readable by the Windows `SYSTEM` account because the background task runs at startup as `SYSTEM`. Use a local disk path rather than a mapped drive. The installer never copies or deletes the model.

## Check that it is running

- Run `worker\get-gpu-worker-status.ps1` in an elevated PowerShell window for the task state and recent log lines.
- Open `http://127.0.0.1:8765/` on the GPU computer to see the local worker dashboard.
- The Jellyfin plugin should use the GPU computer's LAN IP and the same port.

## Stop, restart, or remove

In elevated PowerShell:

```powershell
Stop-ScheduledTask -TaskName 'GST GPU Worker'
Start-ScheduledTask -TaskName 'GST GPU Worker'
```

Run `worker\uninstall-gpu-worker.ps1` to remove the startup task and firewall rule. The script asks whether to remove the installed Python environment and worker files. The selected model directory is always left untouched.

## Network and privacy

- The worker listens on the configured port and accepts translation jobs only from the Jellyfin server IP entered during setup.
- The firewall rule also allows that IP only.
- Subtitle translation is offline after setup. The worker does not send subtitle contents to an external AI or translation API.
- Installing packages requires downloading PyTorch and Python packages once. This setup traffic is separate from subtitle translation.
- The worker dashboard is available on the GPU computer itself at `http://127.0.0.1:8765/`.

## Troubleshooting

- If setup reports that CUDA is unavailable, update the NVIDIA driver and rerun the installer. It does not register the startup task unless the CUDA check succeeds.
- If the worker starts but the model is unavailable, verify that the model directory contains `config.json`, tokenizer files, and model weights, and that `SYSTEM` can read it.
- The worker log and job history are stored under `%ProgramData%\Global Subtitle Translator\GPU Worker\`.
- Run `worker\get-gpu-worker-status.ps1` and inspect `logs\worker.log` for startup errors.
