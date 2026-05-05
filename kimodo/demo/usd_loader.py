# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Small USD mesh loader for the local demo UI."""

from __future__ import annotations

import os
from dataclasses import dataclass

import numpy as np


SUPPORTED_USD_EXTENSIONS = {".usd", ".usda", ".usdc", ".usdz"}


@dataclass
class LoadedUsdMesh:
    name: str
    vertices: np.ndarray
    faces: np.ndarray


def _require_usd():
    try:
        from pxr import Gf, Usd, UsdGeom
    except ImportError as error:
        raise RuntimeError(
            "USD loader requires the 'usd-core' package. Install it with: pip install usd-core"
        ) from error
    return Gf, Usd, UsdGeom


def _triangulate(face_vertex_counts, face_vertex_indices) -> np.ndarray:
    """Fan-triangulate USD polygon faces into [N, 3] triangle indices."""
    triangles: list[list[int]] = []
    cursor = 0
    for count in face_vertex_counts:
        count = int(count)
        polygon = [int(idx) for idx in face_vertex_indices[cursor : cursor + count]]
        cursor += count
        if count < 3:
            continue
        for idx in range(1, count - 1):
            triangles.append([polygon[0], polygon[idx], polygon[idx + 1]])
    return np.asarray(triangles, dtype=np.int32)


def _safe_mesh_name(prim_path: str, index: int) -> str:
    cleaned = prim_path.strip("/").replace("/", "_").replace(" ", "_")
    return cleaned or f"usd_mesh_{index}"


def load_usd_meshes(path: str, *, scale: float = 1.0, center: bool = True) -> list[LoadedUsdMesh]:
    """Load USD meshes as plain triangle geometry.

    Supports `.usd`, `.usda`, `.usdc`, and `.usdz` when the installed USD runtime can open them.
    Materials, skeletons, and animation are intentionally ignored for the first-pass viewport loader.
    """
    path = os.path.abspath(os.path.expanduser(path))
    ext = os.path.splitext(path)[1].lower()
    if ext not in SUPPORTED_USD_EXTENSIONS:
        supported = ", ".join(sorted(SUPPORTED_USD_EXTENSIONS))
        raise ValueError(f"Expected a USD file ({supported}), got: {path}")
    if not os.path.exists(path):
        raise FileNotFoundError(path)

    Gf, Usd, UsdGeom = _require_usd()
    stage = Usd.Stage.Open(path)
    if stage is None:
        raise ValueError(f"Could not open USD stage: {path}")

    time = Usd.TimeCode.Default()
    xform_cache = UsdGeom.XformCache(time)
    loaded: list[LoadedUsdMesh] = []
    all_vertices: list[np.ndarray] = []

    for prim in stage.Traverse():
        mesh = UsdGeom.Mesh(prim)
        if not mesh:
            continue

        points = mesh.GetPointsAttr().Get(time)
        counts = mesh.GetFaceVertexCountsAttr().Get(time)
        indices = mesh.GetFaceVertexIndicesAttr().Get(time)
        if not points or not counts or not indices:
            continue

        world_from_local = xform_cache.GetLocalToWorldTransform(prim)
        vertices = np.asarray(
            [
                tuple(world_from_local.Transform(Gf.Vec3d(float(point[0]), float(point[1]), float(point[2]))))
                for point in points
            ],
            dtype=np.float32,
        )
        faces = _triangulate(counts, indices)
        if vertices.size == 0 or faces.size == 0:
            continue

        vertices *= float(scale)
        loaded.append(
            LoadedUsdMesh(
                name=_safe_mesh_name(str(prim.GetPath()), len(loaded)),
                vertices=vertices,
                faces=faces,
            )
        )
        all_vertices.append(vertices)

    if not loaded:
        raise ValueError(f"No polygon meshes found in USD file: {path}")

    if center:
        combined = np.concatenate(all_vertices, axis=0)
        midpoint = (combined.min(axis=0) + combined.max(axis=0)) * 0.5
        for mesh in loaded:
            mesh.vertices = mesh.vertices - midpoint

    return loaded
