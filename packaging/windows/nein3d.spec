# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the portable Nein3D Windows exe.

Bundles the kimodo package (UI, demo, model, scripts, assets) and its core
runtime deps (viser, gradio, transformers, llm2vec, peft, omegaconf, etc.) so
Nein3D.exe is self-contained except for the HuggingFace model cache, which the
text encoder downloads on first run.
"""
from __future__ import annotations

from pathlib import Path

from PyInstaller.utils.hooks import collect_all, collect_submodules

block_cipher = None

PROJECT_ROOT = Path(SPECPATH).resolve().parents[1] if "SPECPATH" in globals() else Path.cwd()
LAUNCHER = str(PROJECT_ROOT / "packaging" / "windows" / "nein3d_launcher.py")

datas: list[tuple[str, str]] = []
binaries: list[tuple[str, str]] = []
hiddenimports: list[str] = []


def _bundle(pkg: str, *, optional: bool = False) -> None:
    try:
        pkg_datas, pkg_binaries, pkg_hidden = collect_all(pkg)
    except Exception as exc:
        if optional:
            print(f"[spec] optional package missing, skipping: {pkg} ({exc})")
            return
        raise
    datas.extend(pkg_datas)
    binaries.extend(pkg_binaries)
    hiddenimports.extend(pkg_hidden)


# Core kimodo package (UI, demo, model, scripts, assets, vendored llm2vec).
_bundle("kimodo")

# Studio UI runtime.
_bundle("viser")
_bundle("imageio")
_bundle("trimesh", optional=True)
_bundle("plyfile", optional=True)

# Desktop shell runtime: opens the studio in a native Windows WebView window.
_bundle("webview")
_bundle("pythonnet")
_bundle("clr_loader")
_bundle("cffi")
_bundle("bottle")
_bundle("proxy_tools")

# Text encoder runtime (gradio + transformers + accelerate stack).
_bundle("gradio")
_bundle("gradio_client")
_bundle("groovy")
_bundle("safehttpx")
_bundle("transformers")
_bundle("tokenizers")
_bundle("safetensors")
_bundle("accelerate")
_bundle("peft")
_bundle("huggingface_hub")
_bundle("sentence_transformers", optional=True)

# Numerical / IO stack.
_bundle("torch")
_bundle("numpy")
_bundle("scipy")
_bundle("omegaconf")
_bundle("hydra", optional=True)
_bundle("einops")

# USD support (optional, referenced from demo).
_bundle("pxr", optional=True)

# Pull in any submodules PyInstaller's static analysis would otherwise miss.
hiddenimports.extend(
    [
        "kimodo",
        "kimodo.demo",
        "kimodo.demo.app",
        "kimodo.demo.ui",
        "kimodo.demo.generation",
        "kimodo.scripts",
        "kimodo.scripts.run_text_encoder_server",
        "kimodo.scripts.gradio_theme",
        "kimodo.model",
        "kimodo.model.kimodo_model",
        "kimodo.model.tmr",
        "kimodo.model.llm2vec",
        "kimodo.model.text_encoder_api",
        "kimodo.skeleton",
        "kimodo.viz",
        "kimodo.constraints",
        "kimodo.exports",
        "kimodo.exports.mujoco",
        "kimodo.metrics",
        "kimodo.tools",
        "webview.platforms.edgechromium",
        "webview.platforms.winforms",
    ]
)
hiddenimports.extend(collect_submodules("kimodo"))


a = Analysis(
    [LAUNCHER],
    pathex=[str(PROJECT_ROOT)],
    binaries=binaries,
    datas=datas,
    hiddenimports=list(set(hiddenimports)),
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "PyQt5",
        "PyQt6",
        "tkinter",
        "matplotlib.tests",
        "scipy.tests",
        "numpy.tests",
        "torch.test",
        "torch.testing",
    ],
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="Nein3D",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="Nein3D",
)
