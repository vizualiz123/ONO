# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""FBX motion export helpers.

Autodesk's FBX Python SDK is not distributed on PyPI for this environment. The
production path here is a Blender bridge: export a temporary BVH, import it in
Blender, then write FBX. If Blender is not installed, callers get a clear error
instead of a fake `.fbx` file.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Union

import torch

from kimodo.exports.bvh import save_motion_bvh


def _default_windows_blender_candidates() -> list[str]:
    if os.name != "nt":
        return []

    roots: list[Path] = []
    for env_name in ("BLENDER_HOME", "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)", "LOCALAPPDATA"):
        env_value = os.environ.get(env_name)
        if not env_value:
            continue
        env_path = Path(env_value)
        if env_name == "BLENDER_HOME":
            roots.extend([env_path, env_path / "blender.exe"])
        elif env_name == "LOCALAPPDATA":
            roots.append(env_path / "Programs" / "Blender Foundation")
        else:
            roots.append(env_path / "Blender Foundation")

    candidates: list[str] = []
    for root in roots:
        if root.name.lower() == "blender.exe":
            candidates.append(str(root))
            continue
        if not root.exists():
            continue
        candidates.extend(str(path) for path in sorted(root.glob("Blender */blender.exe"), reverse=True))
        candidates.extend(str(path) for path in sorted(root.glob("*/blender.exe"), reverse=True))
    return candidates


def _resolve_bvh_skeleton(local_rot_mats: torch.Tensor, skeleton):
    if getattr(skeleton, "name", "") != "somaskel30":
        return skeleton
    joint_count = int(local_rot_mats.shape[2] if local_rot_mats.dim() == 5 else local_rot_mats.shape[1])
    if joint_count == int(skeleton.somaskel77.nbjoints):
        return skeleton.somaskel77
    return skeleton


def _find_blender(blender_path: str | None = None) -> str:
    candidates = []
    if blender_path:
        candidates.append(blender_path)
    env_path = os.environ.get("BLENDER_PATH")
    if env_path:
        candidates.append(env_path)
    found = shutil.which("blender")
    if found:
        candidates.append(found)
    candidates.extend(_default_windows_blender_candidates())

    seen = set()
    for candidate in candidates:
        if not candidate:
            continue
        candidate_path = Path(candidate)
        normalized = str(candidate_path).lower()
        if normalized in seen:
            continue
        seen.add(normalized)
        if candidate_path.exists():
            return str(candidate_path)
    raise RuntimeError(
        "FBX export requires Blender in PATH or BLENDER_PATH. Install Blender, "
        "then run export again. USD export works without Blender."
    )


def save_motion_fbx(
    path: Union[str, Path],
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    *,
    skeleton,
    fps: float,
    standard_tpose: bool = False,
    blender_path: str | None = None,
) -> None:
    """Write FBX by converting the motion through Blender's BVH importer."""
    output_path = Path(path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    blender = _find_blender(blender_path)
    skeleton = _resolve_bvh_skeleton(local_rot_mats, skeleton)

    with tempfile.TemporaryDirectory(prefix="nein3d_fbx_") as tmpdir:
        tmpdir_path = Path(tmpdir)
        bvh_path = tmpdir_path / "motion.bvh"
        script_path = tmpdir_path / "export_fbx.py"
        save_motion_bvh(
            bvh_path,
            local_rot_mats,
            root_positions,
            skeleton=skeleton,
            fps=fps,
            standard_tpose=standard_tpose,
        )
        script_path.write_text(
            f"""
import bpy

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete()
bpy.ops.import_anim.bvh(filepath={str(bvh_path)!r}, global_scale=1.0)
bpy.ops.export_scene.fbx(
    filepath={str(output_path)!r},
    use_selection=False,
    object_types={{'ARMATURE'}},
    add_leaf_bones=False,
    bake_anim=True,
    bake_anim_use_all_bones=True,
    bake_anim_use_nla_strips=False,
    bake_anim_use_all_actions=False,
)
""",
            encoding="utf-8",
        )
        completed = subprocess.run(
            [blender, "--background", "--python", str(script_path)],
            capture_output=True,
            text=True,
            timeout=600,
        )
        if completed.returncode != 0:
            raise RuntimeError(
                "Blender FBX export failed.\n"
                f"stdout:\n{completed.stdout[-4000:]}\n"
                f"stderr:\n{completed.stderr[-4000:]}"
            )


def motion_to_fbx_bytes(
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    *,
    skeleton,
    fps: float,
    standard_tpose: bool = False,
    blender_path: str | None = None,
) -> bytes:
    """Return FBX bytes via the Blender bridge."""
    with tempfile.TemporaryDirectory(prefix="nein3d_fbx_bytes_") as tmpdir:
        path = Path(tmpdir) / "motion.fbx"
        save_motion_fbx(
            path,
            local_rot_mats,
            root_positions,
            skeleton=skeleton,
            fps=fps,
            standard_tpose=standard_tpose,
            blender_path=blender_path,
        )
        return path.read_bytes()
