# Nein3D

Nein3D is a Windows-first AI motion studio built on top of Kimodo: prompt-driven motion generation, a viewport, a timeline, and USD asset support, packaged behind a single `Nein3D.exe` launcher.

This repository currently still contains the original Kimodo codebase underneath. The new product layer is being added on top and should gradually replace the old demo UI.

## Current State

- Main development branch: `dev`.
- Windows-only build branch: `windows-multi-gpu`.
- Local repository remote: `nein3d/dev`.
- Windows launcher: `release/windows/Nein3D.exe`.
- Backend engine port: `http://127.0.0.1:7860`.
- Text encoder server port: `http://127.0.0.1:9550`.
- Default app name: `Nein3D`.
- Default model: `kimodo-soma-seed`.

## What Works Now

- `Nein3D.exe` starts the local stack from one launcher.
- The Viser/Kimodo backend performs model loading and generation.
- The launcher auto-detects the best available GPU backend (NVIDIA CUDA, AMD/Intel via DirectML, Intel XPU, Apple MPS) with a CPU fallback.
- The backend uses aggressive GPU cleanup and keeps the text encoder on CPU where possible.
- USD loading is available in the backend UI.
- Motion export supports NPZ/BVH/CSV/AMASS plus USD; FBX is wired through Blender when available.
- Timeline length syncs to animation length.

## Supported GPUs

The launcher probes the venv at startup and exports `KIMODO_GPU_BACKEND` for the backend, which picks the matching `torch` device:

| Vendor                       | Backend     | Install                                                        |
| ---------------------------- | ----------- | -------------------------------------------------------------- |
| NVIDIA                       | `cuda`      | `pip install torch --index-url https://download.pytorch.org/whl/cu121` |
| AMD / Intel iGPU on Windows  | `directml`  | `pip install torch-directml`                                   |
| Intel Arc / discrete         | `xpu`       | Intel Extension for PyTorch build                              |
| Apple Silicon (Mac only)     | `mps`       | Stock PyTorch                                                  |
| Any                          | `cpu`       | Fallback                                                       |

Force a backend with `Nein3D.exe --gpu-backend directml` or `KIMODO_GPU_BACKEND=cuda`.

## What Is Not Connected Yet

- The current `.exe` is a launcher, not a fully portable app bundle with Python, CUDA/DirectML, PyTorch, and models inside.
- DirectML/XPU/MPS paths share the same code path as CUDA in the engine; some ops may still fall back to CPU on non-CUDA backends.

## Current Architecture

```text
Nein3D.exe
  ├─ probes venv for the best GPU backend (cuda / directml / xpu / mps / cpu)
  ├─ starts text encoder server on 9550 (CPU)
  └─ starts Kimodo/Viser backend on 7860 (selected GPU)

Browser
  └─ opens http://127.0.0.1:7860
```

## LLaMA / LLM2Vec Replacement Note

Current text encoding uses LLM2Vec/LLaMA-style embeddings. The idea is to replace or bypass the local LLaMA text encoder with an external API later.

Do not simply swap the URL. Kimodo expects embeddings with a specific size and distribution. A safe implementation needs an adapter layer:

```text
Prompt text
  -> Brain/Text Encoder API
  -> compatibility adapter or projection layer
  -> Kimodo-compatible embedding
  -> motion generation
```

Recommended staged plan:

1. Keep the current local LLM2Vec encoder as fallback.
2. Add `TEXT_ENCODER_MODE=openai` or a more generic `TEXT_ENCODER_MODE=api_v2`.
3. Add a clean adapter interface for text embeddings.
4. Compare embedding dimensions and motion quality.
5. If dimensions or distribution differ, train or fit a projection layer.
6. Only then make API mode the default.

## Next Development Steps

1. Validate end-to-end runs on AMD (DirectML) and Intel Arc (XPU) hardware.
2. Add a backend HTTP API so a future shell can drive generation programmatically.
3. Build a portable Windows release:
   - `Nein3D.exe`
   - embedded Python runtime
   - vendor-correct PyTorch wheels (CUDA / DirectML / XPU)
   - model cache strategy
   - logs and crash reports

## Run

```powershell
.\release\windows\Nein3D.exe
```

Without opening the browser:

```powershell
.\release\windows\Nein3D.exe --no-browser
```

Force a specific GPU backend:

```powershell
.\release\windows\Nein3D.exe --gpu-backend cuda
.\release\windows\Nein3D.exe --gpu-backend directml
.\release\windows\Nein3D.exe --gpu-backend cpu
```

Open the backend UI manually:

```text
http://127.0.0.1:7860
```

## Repository Transfer Note

When a new repository URL is provided, push the relevant branch there. Do not commit local model folders or cache folders. The following local/untracked folders should stay out of git unless intentionally handled with LFS or a release artifact process:

- `kimodo/assets/demo/examples/kimodo-g1-seed/`
- `kimodo/assets/demo/examples/kimodo-soma-rp-v1.1/`
- `kimodo/assets/demo/examples/kimodo-soma-rp-v1/`
- `kimodo/assets/demo/examples/kimodo-soma-seed-v1.1/`
- `kimodo/assets/demo/examples/kimodo-soma-seed-v1/`
- `kimodo/assets/demo/examples/kimodo-soma-seed/`

## Last Important Commits

- `e7f4b39` - Move prompt editor to viewport dock
- `39b372e` - Add Windows exe launcher
- `dea8dfc` - Add production readiness PDF
- `3078dfa` - Convert action buttons to icons
