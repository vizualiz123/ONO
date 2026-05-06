# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""USD motion export helpers.

The exporter writes an animated transform hierarchy plus small joint markers.
That keeps the output lightweight and readable by DCC tools even before a full
skinned-character pipeline exists.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Union

import torch

from kimodo.geometry import matrix_to_quaternion


def _require_usd():
    try:
        from pxr import Gf, Sdf, Usd, UsdGeom  # type: ignore

        return Gf, Sdf, Usd, UsdGeom
    except Exception as exc:  # pragma: no cover
        raise ImportError("USD export requires the 'usd-core' package. Install it with: pip install usd-core") from exc


def _coerce_time_local_root(
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
) -> tuple[torch.Tensor, torch.Tensor]:
    if local_rot_mats.dim() == 5:
        if int(local_rot_mats.shape[0]) != 1:
            raise ValueError(f"local_rot_mats batch size must be 1 for USD export; got {local_rot_mats.shape[0]}")
        local_rot_mats = local_rot_mats[0]
    if root_positions.dim() == 3:
        if int(root_positions.shape[0]) != 1:
            raise ValueError(f"root_positions batch size must be 1 for USD export; got {root_positions.shape[0]}")
        root_positions = root_positions[0]
    if local_rot_mats.dim() != 4:
        raise ValueError(f"local_rot_mats must be (T,J,3,3); got {tuple(local_rot_mats.shape)}")
    if root_positions.dim() != 2 or int(root_positions.shape[-1]) != 3:
        raise ValueError(f"root_positions must be (T,3); got {tuple(root_positions.shape)}")
    if int(local_rot_mats.shape[0]) != int(root_positions.shape[0]):
        raise ValueError("local_rot_mats and root_positions must have the same number of frames")
    return local_rot_mats, root_positions


def _safe_prim_name(name: str, index: int) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9_]", "_", str(name)).strip("_")
    if not cleaned or cleaned[0].isdigit():
        cleaned = f"joint_{cleaned}" if cleaned else "joint"
    return f"J{index:03d}_{cleaned}"


def _matrix_from_translation_quat(Gf, translation, quat_wxyz) -> object:
    tx, ty, tz = [float(v) for v in translation]
    qw, qx, qy, qz = [float(v) for v in quat_wxyz]
    matrix = Gf.Matrix4d(1.0)
    matrix.SetRotate(Gf.Quatd(qw, Gf.Vec3d(qx, qy, qz)))
    matrix.SetTranslateOnly(Gf.Vec3d(tx, ty, tz))
    return matrix


def _build_usd_stage(
    stage,
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    *,
    skeleton,
    fps: float,
    include_markers: bool = True,
    marker_radius: float = 0.025,
):
    Gf, Sdf, Usd, UsdGeom = _require_usd()

    local_rot_mats = local_rot_mats.detach().cpu()
    root_positions = root_positions.detach().cpu()
    local_rot_mats, root_positions = _coerce_time_local_root(local_rot_mats, root_positions)

    if getattr(skeleton, "name", "") == "somaskel30":
        if int(local_rot_mats.shape[1]) == int(skeleton.nbjoints):
            local_rot_mats = skeleton.to_SOMASkeleton77(local_rot_mats)
        elif int(local_rot_mats.shape[1]) != int(skeleton.somaskel77.nbjoints):
            raise ValueError(
                "SOMA30 skeleton can export either 30-joint local rotations "
                f"or already-expanded 77-joint rotations; got {local_rot_mats.shape[1]} joints."
            )
        skeleton = skeleton.somaskel77

    T, J = local_rot_mats.shape[:2]

    if int(getattr(skeleton, "nbjoints", J)) != int(J):
        raise ValueError(f"Motion has {J} joints but skeleton has {getattr(skeleton, 'nbjoints', '?')}.")

    fps = float(fps)
    if fps <= 0:
        raise ValueError(f"fps must be positive; got {fps}")

    stage.SetStartTimeCode(0)
    stage.SetEndTimeCode(max(0, T - 1))
    stage.SetTimeCodesPerSecond(fps)
    stage.SetFramesPerSecond(fps)
    UsdGeom.SetStageMetersPerUnit(stage, 1.0)
    UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.y)

    world = UsdGeom.Xform.Define(stage, "/World")
    stage.SetDefaultPrim(world.GetPrim())

    root_container = UsdGeom.Xform.Define(stage, "/World/Nein3D_Motion")
    root_container.GetPrim().CreateAttribute("nein3d:fps", Sdf.ValueTypeNames.Double).Set(fps)

    parents = skeleton.joint_parents.detach().cpu().numpy().astype(int).tolist()
    neutral = skeleton.neutral_joints.detach().cpu()
    joint_names = list(skeleton.bone_order_names)
    root_idx = int(skeleton.root_idx)
    q_wxyz = matrix_to_quaternion(local_rot_mats).detach().cpu()

    paths: list[str] = [""] * J
    ops = []
    for idx, name in enumerate(joint_names):
        parent = int(parents[idx])
        safe_name = _safe_prim_name(name, idx)
        parent_path = "/World/Nein3D_Motion" if parent < 0 else paths[parent]
        path = f"{parent_path}/{safe_name}"
        paths[idx] = path

        xform = UsdGeom.Xform.Define(stage, path)
        xform.GetPrim().CreateAttribute("nein3d:jointName", Sdf.ValueTypeNames.String).Set(str(name))
        xform.GetPrim().CreateAttribute("nein3d:jointIndex", Sdf.ValueTypeNames.Int).Set(int(idx))
        ops.append(xform.AddTransformOp(opSuffix="motion"))

        if include_markers:
            sphere = UsdGeom.Sphere.Define(stage, f"{path}/marker")
            sphere.GetRadiusAttr().Set(float(marker_radius))
            sphere.GetDisplayColorAttr().Set([Gf.Vec3f(0.12, 0.72, 1.0)])

    for frame in range(T):
        for idx, op in enumerate(ops):
            parent = int(parents[idx])
            if idx == root_idx or parent < 0:
                translation = root_positions[frame]
            else:
                translation = neutral[idx] - neutral[parent]
            matrix = _matrix_from_translation_quat(Gf, translation, q_wxyz[frame, idx])
            op.Set(matrix, Usd.TimeCode(frame))

    return stage


def _create_stage(path: Union[str, Path] | None = None):
    _Gf, _Sdf, Usd, _UsdGeom = _require_usd()
    if path is None:
        return Usd.Stage.CreateInMemory("nein3d_motion.usda")
    return Usd.Stage.CreateNew(str(path))


def motion_to_usd_bytes(
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    *,
    skeleton,
    fps: float,
    include_markers: bool = True,
    marker_radius: float = 0.025,
) -> bytes:
    """Return an ASCII USDA payload for the motion."""
    stage = _create_stage()
    _build_usd_stage(
        stage,
        local_rot_mats,
        root_positions,
        skeleton=skeleton,
        fps=fps,
        include_markers=include_markers,
        marker_radius=marker_radius,
    )
    return stage.GetRootLayer().ExportToString().encode("utf-8")


def save_motion_usd(
    path: Union[str, Path],
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    *,
    skeleton,
    fps: float,
    include_markers: bool = True,
    marker_radius: float = 0.025,
) -> None:
    """Write animated motion to a USD file.

    `.usd`, `.usda`, and `.usdc` are accepted by the USD runtime.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    if path.suffix.lower() == ".usdz":
        raise ValueError("USDZ export is not enabled yet; use .usd, .usda, or .usdc.")

    stage = _create_stage(path)
    _build_usd_stage(
        stage,
        local_rot_mats,
        root_positions,
        skeleton=skeleton,
        fps=fps,
        include_markers=include_markers,
        marker_radius=marker_radius,
    )
    stage.GetRootLayer().Save()
