# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Lightweight auto-fix helpers for isolated rotation anomalies."""

from __future__ import annotations

import torch

from kimodo.geometry import matrix_to_quaternion, quaternion_to_matrix

from .continuity import canonicalize_quaternions_timeline, slerp_blend


def _nearest_unmasked(mask: torch.Tensor, frame: int, joint: int, direction: int, window: int) -> int | None:
    limit = range(1, window + 1)
    for offset in limit:
        candidate = frame + (direction * offset)
        if candidate < 0 or candidate >= int(mask.shape[0]):
            break
        if not bool(mask[candidate, joint]):
            return candidate
    return None


def smooth_anomalies(
    local_rot_mats: torch.Tensor,
    mask: torch.Tensor,
    *,
    window: int = 3,
) -> torch.Tensor:
    """Replace masked rotations by shortest-path interpolation from nearby frames."""
    if local_rot_mats.dim() == 5:
        if int(local_rot_mats.shape[0]) != 1:
            raise ValueError(f"Expected one motion clip, got batch shape {tuple(local_rot_mats.shape)}")
        fixed = smooth_anomalies(local_rot_mats[0], mask, window=window)
        return fixed.unsqueeze(0)

    if local_rot_mats.dim() != 4 or tuple(local_rot_mats.shape[-2:]) != (3, 3):
        raise ValueError(f"Expected local_rot_mats with shape (T,J,3,3), got {tuple(local_rot_mats.shape)}")
    if mask.shape != local_rot_mats.shape[:2]:
        raise ValueError(f"mask shape {tuple(mask.shape)} must match motion shape {tuple(local_rot_mats.shape[:2])}")

    q = canonicalize_quaternions_timeline(matrix_to_quaternion(local_rot_mats), time_dim=0)
    out = q.clone()
    mask = mask.to(device=q.device, dtype=torch.bool)
    frames, joints = torch.where(mask)
    for frame_t, joint_t in zip(frames.tolist(), joints.tolist()):
        left = _nearest_unmasked(mask, frame_t, joint_t, -1, int(window))
        right = _nearest_unmasked(mask, frame_t, joint_t, 1, int(window))
        if left is not None and right is not None and right != left:
            tau = (frame_t - left) / float(right - left)
            out[frame_t, joint_t] = slerp_blend(q[left, joint_t], q[right, joint_t], tau)
        elif left is not None:
            out[frame_t, joint_t] = q[left, joint_t]
        elif right is not None:
            out[frame_t, joint_t] = q[right, joint_t]

    out = canonicalize_quaternions_timeline(out, time_dim=0)
    return quaternion_to_matrix(out).to(dtype=local_rot_mats.dtype)
