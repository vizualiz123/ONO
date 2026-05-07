# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Quaternion continuity and shortest-path interpolation helpers."""

from __future__ import annotations

import torch

from kimodo.geometry import matrix_to_quaternion, quaternion_to_matrix


def normalize_quaternion(q: torch.Tensor, eps: float = 1e-8) -> torch.Tensor:
    """Return normalized wxyz quaternions without changing shape."""
    return q / torch.linalg.norm(q, dim=-1, keepdim=True).clamp(min=eps)


def canonicalize_quaternions_timeline(
    q: torch.Tensor,
    *,
    time_dim: int = 0,
    prefer_positive_w: bool = True,
) -> torch.Tensor:
    """Make adjacent quaternions use the same sign along a timeline.

    Quaternions q and -q represent the same rotation, but interpolation and
    spike detectors need a stable sign. This guarantees dot(q[t], q[t-1]) >= 0.
    """
    if q.shape[-1] != 4:
        raise ValueError(f"Expected quaternions with last dimension 4, got {tuple(q.shape)}")

    q_time = torch.movedim(normalize_quaternion(q), time_dim, 0).clone()
    if q_time.shape[0] == 0:
        return torch.movedim(q_time, 0, time_dim)

    if prefer_positive_w:
        q_time[0] = torch.where(q_time[0][..., 0:1] < 0, -q_time[0], q_time[0])

    for idx in range(1, int(q_time.shape[0])):
        dot = (q_time[idx - 1] * q_time[idx]).sum(dim=-1, keepdim=True)
        q_time[idx] = torch.where(dot < 0, -q_time[idx], q_time[idx])

    return torch.movedim(q_time, 0, time_dim)


def slerp_blend(q0: torch.Tensor, q1: torch.Tensor, t: torch.Tensor | float) -> torch.Tensor:
    """Shortest-path spherical interpolation between wxyz quaternions."""
    q0 = normalize_quaternion(q0)
    q1 = normalize_quaternion(q1)
    if not torch.is_tensor(t):
        t = torch.tensor(t, dtype=q0.dtype, device=q0.device)
    else:
        t = t.to(device=q0.device, dtype=q0.dtype)
    while t.dim() < q0.dim():
        t = t.unsqueeze(-1)

    dot = (q0 * q1).sum(dim=-1, keepdim=True)
    q1 = torch.where(dot < 0, -q1, q1)
    dot = dot.abs().clamp(-1.0, 1.0)

    close = dot > 0.9995
    theta_0 = torch.acos(dot)
    sin_theta_0 = torch.sin(theta_0).clamp(min=1e-8)
    s0 = torch.sin((1.0 - t) * theta_0) / sin_theta_0
    s1 = torch.sin(t * theta_0) / sin_theta_0
    spherical = s0 * q0 + s1 * q1
    linear = (1.0 - t) * q0 + t * q1
    return normalize_quaternion(torch.where(close, linear, spherical))


def angular_distance(q0: torch.Tensor, q1: torch.Tensor) -> torch.Tensor:
    """Return shortest angular distance in radians for wxyz quaternion pairs."""
    q0 = normalize_quaternion(q0)
    q1 = normalize_quaternion(q1)
    dot = (q0 * q1).sum(dim=-1).abs().clamp(0.0, 1.0)
    return 2.0 * torch.acos(dot)


def canonicalize_rotation_matrices(local_rot_mats: torch.Tensor, *, time_dim: int = 0) -> torch.Tensor:
    """Round-trip matrices through canonical quaternions along time."""
    q = canonicalize_quaternions_timeline(matrix_to_quaternion(local_rot_mats), time_dim=time_dim)
    return quaternion_to_matrix(q)
