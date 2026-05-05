# Nein3D

Nein3D is being built as a web-based DCC motion studio: the long-term target is a browser UI in the spirit of 3ds Max, with AI motion generation, editable prompts, timeline control, USD assets, and production export tools.

This repository currently still contains the original Kimodo codebase underneath. The new product layer is being added on top and should gradually replace the old demo UI.

## Current State

- Main development branch: `dev`.
- Local repository remote: `nein3d/dev`.
- Windows launcher: `release/windows/Nein3D.exe`.
- New web UI: `web/nein3d_studio`.
- Web UI port: `http://127.0.0.1:7870`.
- Backend engine port: `http://127.0.0.1:7860`.
- Text encoder server port: `http://127.0.0.1:9550`.
- Default app name: `Nein3D`.
- Default model: `kimodo-soma-seed`.

## What Works Now

- `Nein3D.exe` starts the local stack from one launcher.
- The old Viser/Kimodo backend still performs the actual model loading and generation.
- The new web DCC interface opens separately on port `7870`.
- The prompt is editable in a bottom transparent glass dock over the viewport.
- The web UI has a 3D-editor layout: top menu, toolbar, left tool strip, viewport, right command panel, bottom timeline.
- The web UI can embed the old backend in `Live Engine` mode so the original generation/timeline/USD tools keep working.
- The web UI can save a local `.nein3d.json` project snapshot.
- The app uses a black/cyan style by default.
- The backend uses aggressive GPU cleanup and keeps the text encoder on CPU where possible.
- USD loading exists in the old backend UI.
- Timeline length sync fixes were added in the backend.

## What Is Not Connected Yet

- The new web `Generate` button does not yet call backend generation directly.
- The web prompt edits are local to the web UI until backend binding is implemented.
- The right-side model settings in the web UI are layout/state only for now.
- The web USD panel is a UI placeholder; real USD loading still lives in the backend UI.
- Undo/redo in the web UI is not backed by an action history yet.
- The current `.exe` is a launcher, not a fully portable app bundle with Python, CUDA, PyTorch, and models inside.

## Current Architecture

```text
Nein3D.exe
  ├─ starts text encoder server on 9550
  ├─ starts Kimodo/Viser backend on 7860
  └─ starts Nein3D web UI on 7870

Browser
  └─ opens http://127.0.0.1:7870/?engine=http://127.0.0.1:7860&mode=engine
```

The new web UI should become the real product interface. The old Viser UI should be treated as the engine/admin backend until its useful functions are exposed through an API.

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

This is not implemented yet. It is documented here so the plan is not lost when moving to a new repository.

## Next Development Steps

1. Add a backend HTTP API for the new web UI:
   - `/api/status`
   - `/api/generate`
   - `/api/load-usd`
   - `/api/save-project`
   - `/api/export`

2. Connect the web prompt dock to real generation.

3. Move USD loading from the old backend UI into the new web command panel.

4. Add a real project format:
   - `project.nein3d.json`
   - prompts
   - timeline
   - model settings
   - asset references
   - generated motion paths

5. Build a portable Windows release:
   - `Nein3D.exe`
   - local Python runtime or embedded venv
   - CUDA/PyTorch dependencies
   - model cache strategy
   - logs and crash reports

6. Create the real remote repository when the GitHub/Git remote is provided.

## Run

```powershell
.\release\windows\Nein3D.exe
```

Without opening the browser:

```powershell
.\release\windows\Nein3D.exe --no-browser
```

Open the new web UI manually:

```text
http://127.0.0.1:7870/?engine=http://127.0.0.1:7860
```

Open the new shell with the old working backend embedded by default:

```text
http://127.0.0.1:7870/?engine=http://127.0.0.1:7860&mode=engine
```

Open the old backend UI manually:

```text
http://127.0.0.1:7860
```

## Repository Transfer Note

When a new repository URL is provided, push the `dev` branch there first. Do not commit local model folders or cache folders. The following local/untracked folders should stay out of git unless intentionally handled with LFS or a release artifact process:

- `kimodo/assets/demo/examples/kimodo-g1-seed/`
- `kimodo/assets/demo/examples/kimodo-soma-rp-v1.1/`
- `kimodo/assets/demo/examples/kimodo-soma-rp-v1/`
- `kimodo/assets/demo/examples/kimodo-soma-seed-v1.1/`
- `kimodo/assets/demo/examples/kimodo-soma-seed-v1/`
- `kimodo/assets/demo/examples/kimodo-soma-seed/`

## Last Important Commits

- `e7f4b39` - Move prompt editor to viewport dock
- `29d98e5` - Add web DCC interface
- `39b372e` - Add Windows exe launcher
- `dea8dfc` - Add production readiness PDF
- `3078dfa` - Convert action buttons to icons
