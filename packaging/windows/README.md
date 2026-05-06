# Nein3D Windows EXE

This folder builds the Windows-only launcher for Nein3D.

`Nein3D.exe` starts the local text encoder and the 3D studio, then opens the
studio in its own Windows desktop window via WebView2/pywebview. It does not
open a normal browser tab by default.

- app title: `Nein3D`
- dark theme enabled
- text encoder on CPU
- aggressive GPU cleanup enabled
- logs in `logs/`
- backend engine on `http://127.0.0.1:7860`
- desktop app window: enabled by default

## Supported GPUs

The launcher auto-detects the best accelerator available in the venv:

1. **NVIDIA** — CUDA (install a CUDA build of `torch`)
2. **AMD / Intel on Windows** — DirectML (`pip install torch-directml`)
3. **Intel discrete / Arc** — XPU (`torch` Intel Extension build)
4. **Apple Silicon** — MPS (Mac only; not the primary target for this build)
5. **CPU** — fallback when no accelerator is available

Override the picker with `--gpu-backend {auto,cuda,directml,xpu,mps,cpu}` or by setting `KIMODO_GPU_BACKEND` in the environment.

Build:

```powershell
.\packaging\windows\build_exe.ps1 -Clean
```

Output:

```text
release\windows\Nein3D\Nein3D.exe
```

Run:

```powershell
.\release\windows\Nein3D\Nein3D.exe
```

Useful flags:

```powershell
.\release\windows\Nein3D\Nein3D.exe --browser
.\release\windows\Nein3D\Nein3D.exe --no-window
.\release\windows\Nein3D\Nein3D.exe --detach
.\release\windows\Nein3D\Nein3D.exe --ui-port 7861
.\release\windows\Nein3D\Nein3D.exe --model kimodo-soma-seed
.\release\windows\Nein3D\Nein3D.exe --gpu-backend directml
.\release\windows\Nein3D\Nein3D.exe --gpu-backend cuda
```

For a true portable release later: package the Python runtime, vendor-appropriate PyTorch wheels (CUDA / DirectML / XPU), USD runtime, app assets, and a model cache next to this launcher, or move to an installer.
