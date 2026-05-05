# Nein3D Windows EXE

This folder builds the first production-style Windows launcher for Nein3D.

`Nein3D.exe` is intentionally a small launcher, not a full bundled CUDA/PyTorch/model binary yet. It starts the existing local `.venv`, text encoder, and studio UI with production defaults:

- app title: `Nein3D`
- dark theme enabled
- text encoder on CPU
- GPU cleanup enabled
- logs in `logs/`
- browser opened at `http://127.0.0.1:7860`

Build:

```powershell
.\packaging\windows\build_exe.ps1 -Clean
```

Output:

```text
release\windows\Nein3D.exe
```

Run:

```powershell
.\release\windows\Nein3D.exe
```

Useful flags:

```powershell
.\release\windows\Nein3D.exe --no-browser
.\release\windows\Nein3D.exe --detach
.\release\windows\Nein3D.exe --ui-port 7861
.\release\windows\Nein3D.exe --model kimodo-soma-seed
```

For a later true portable release, package the Python runtime, CUDA-compatible PyTorch wheels, USD runtime, app assets, and a model cache next to this launcher or move to an installer.
