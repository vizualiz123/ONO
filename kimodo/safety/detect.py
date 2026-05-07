# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Detection helpers for invalid frames and temporal rotation spikes."""

from __future__ import annotations

import torch

from kimodo.geometry import matrix_to_quaternion

from .continuity import angular_distance, canonicalize_quaternions_timeline


def _frame_reduce(mask: torch.Tensor) -> list[int]:
    if mask.numel() == 0:
        return []
    flat = mask.reshape(mask.shape[0], -1)
    return torch.where(flat.any(dim=1))[0].detach().cpu().tolist()


def detect_nonfinite_frames(*tensors: torch.Tensor | None) -> list[int]:
    """Return frame indices where any provided tensor has NaN or Inf values."""
    frame_masks: list[torch.Tensor] = []
    for tensor in tensors:
        if tensor is None:
            continue
        if not torch.is_tensor(tensor):
            tensor = torch.as_tensor(tensor)
        if tensor.numel() == 0:
            continue
        frame_masks.append(~torch.isfinite(tensor).reshape(tensor.shape[0], -1).all(dim=1))
    if not frame_masks:
        return []
    mask = frame_masks[0]
    for item in frame_masks[1:]:
        mask = mask | item.to(device=mask.device)
    return torch.where(mask)[0].detach().cpu().tolist()


def detect_rotation_spikes(
    local_rot_mats: torch.Tensor,
    *,
    threshold_deg: float = 120.0,
) -> torch.Tensor:
    """Return a (T, J) mask where frame t jumps too far from t-1."""
    if local_rot_mats.dim() == 5:
        if int(local_rot_mats.shape[0]) != 1:
            raise ValueError(f"Expected one motion clip, got batch shape {tuple(local_rot_mats.shape)}")
        local_rot_mats = local_rot_mats[0]
    if local_rot_mats.dim() != 4 or tuple(local_rot_mats.shape[-2:]) != (3, 3):
        raise ValueError(f"Expected local_rot_mats with shape (T,J,3,3), got {tuple(local_rot_mats.shape)}")

    t_frames, joints = int(local_rot_mats.shape[0]), int(local_rot_mats.shape[1])
    mask = torch.zeros((t_frames, joints), dtype=torch.bool, device=local_rot_mats.device)
    if t_frames < 2:
        return mask

    q = canonicalize_quaternions_timeline(matrix_to_quaternion(local_rot_mats), time_dim=0)
    delta = angular_distance(q[:-1], q[1:])
    mask[1:] = delta > torch.deg2rad(torch.tensor(float(threshold_deg), device=delta.device, dtype=delta.dtype))
    return mask
