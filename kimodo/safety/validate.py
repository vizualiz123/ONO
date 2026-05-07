# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Motion validation reports used by UI and export gates."""

from __future__ import annotations

from dataclasses import dataclass, field

import torch

from kimodo.geometry import matrix_to_quaternion

from .continuity import angular_distance, canonicalize_quaternions_timeline
from .detect import detect_nonfinite_frames, detect_rotation_spikes
from .smooth import smooth_anomalies


@dataclass
class MotionValidationReport:
    n_frames: int
    n_joints: int
    fps: float
    nan_frames: list[int] = field(default_factory=list)
    rotation_spike_frames: list[int] = field(default_factory=list)
    root_jump_frames: list[int] = field(default_factory=list)
    max_rotation_step_deg: float = 0.0
    max_root_speed_mps: float = 0.0
    rotation_spike_mask: torch.Tensor | None = field(default=None, repr=False)

    @property
    def has_blocking_errors(self) -> bool:
        return bool(self.nan_frames)

    @property
    def has_warnings(self) -> bool:
        return bool(self.rotation_spike_frames or self.root_jump_frames)

    @property
    def is_valid(self) -> bool:
        return not self.has_blocking_errors and not self.has_warnings


def _coerce_motion_tensors(
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
) -> tuple[torch.Tensor, torch.Tensor]:
    if not torch.is_tensor(local_rot_mats):
        local_rot_mats = torch.as_tensor(local_rot_mats)
    if not torch.is_tensor(root_positions):
        root_positions = torch.as_tensor(root_positions)
    if local_rot_mats.dim() == 5:
        if int(local_rot_mats.shape[0]) != 1:
            raise ValueError(f"Expected a single clip; got local_rot_mats shape {tuple(local_rot_mats.shape)}")
        local_rot_mats = local_rot_mats[0]
    if root_positions.dim() == 3:
        if int(root_positions.shape[0]) != 1:
            raise ValueError(f"Expected a single clip; got root_positions shape {tuple(root_positions.shape)}")
        root_positions = root_positions[0]
    if local_rot_mats.dim() != 4 or tuple(local_rot_mats.shape[-2:]) != (3, 3):
        raise ValueError(f"Expected local_rot_mats shape (T,J,3,3), got {tuple(local_rot_mats.shape)}")
    if root_positions.dim() != 2 or int(root_positions.shape[-1]) != 3:
        raise ValueError(f"Expected root_positions shape (T,3), got {tuple(root_positions.shape)}")
    if int(local_rot_mats.shape[0]) != int(root_positions.shape[0]):
        raise ValueError("local_rot_mats and root_positions must have the same frame count")
    return local_rot_mats, root_positions.to(device=local_rot_mats.device, dtype=local_rot_mats.dtype)


def validate_motion(
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    skeleton,
    fps: float,
    *,
    spike_threshold_deg: float = 120.0,
    root_speed_threshold_mps: float = 12.0,
) -> MotionValidationReport:
    """Validate one motion clip for NaN/Inf, rotation jumps, and root teleports."""
    del skeleton  # reserved for anatomical limits in the next safety phase
    local_rot_mats, root_positions = _coerce_motion_tensors(local_rot_mats, root_positions)
    n_frames = int(local_rot_mats.shape[0])
    n_joints = int(local_rot_mats.shape[1])
    fps = float(fps)

    nan_frames = detect_nonfinite_frames(local_rot_mats, root_positions)
    spike_mask = detect_rotation_spikes(local_rot_mats, threshold_deg=spike_threshold_deg)
    spike_frames = torch.where(spike_mask.any(dim=1))[0].detach().cpu().tolist()

    max_rotation_step_deg = 0.0
    if n_frames >= 2:
        q = canonicalize_quaternions_timeline(matrix_to_quaternion(local_rot_mats), time_dim=0)
        step = angular_distance(q[:-1], q[1:])
        if step.numel() > 0 and torch.isfinite(step).any():
            max_rotation_step_deg = float(torch.rad2deg(step[torch.isfinite(step)].max()).detach().cpu())

    root_jump_frames: list[int] = []
    max_root_speed_mps = 0.0
    if n_frames >= 2 and fps > 0:
        root_speed = torch.linalg.norm(root_positions[1:] - root_positions[:-1], dim=-1) * fps
        finite_speed = root_speed[torch.isfinite(root_speed)]
        if finite_speed.numel() > 0:
            max_root_speed_mps = float(finite_speed.max().detach().cpu())
        root_jump_frames = (torch.where(root_speed > float(root_speed_threshold_mps))[0] + 1).detach().cpu().tolist()

    return MotionValidationReport(
        n_frames=n_frames,
        n_joints=n_joints,
        fps=fps,
        nan_frames=nan_frames,
        rotation_spike_frames=spike_frames,
        root_jump_frames=root_jump_frames,
        max_rotation_step_deg=max_rotation_step_deg,
        max_root_speed_mps=max_root_speed_mps,
        rotation_spike_mask=spike_mask,
    )


def autofix_motion(
    local_rot_mats: torch.Tensor,
    report: MotionValidationReport,
    *,
    window: int = 3,
) -> torch.Tensor:
    """Return local rotations with isolated rotation spikes smoothed."""
    if report.rotation_spike_mask is None or not bool(report.rotation_spike_mask.any()):
        return local_rot_mats
    return smooth_anomalies(local_rot_mats, report.rotation_spike_mask, window=window)


def format_validation_report(report: MotionValidationReport, *, max_frames: int = 10) -> str:
    """Human-readable compact report for notifications and logs."""
    lines = [
        f"frames={report.n_frames}, joints={report.n_joints}, fps={report.fps:.3g}",
        f"max rotation step={report.max_rotation_step_deg:.1f} deg",
        f"max root speed={report.max_root_speed_mps:.2f} m/s",
    ]
    if report.nan_frames:
        lines.append(f"NaN/Inf frames: {report.nan_frames[:max_frames]}")
    if report.rotation_spike_frames:
        lines.append(f"Rotation spike frames: {report.rotation_spike_frames[:max_frames]}")
    if report.root_jump_frames:
        lines.append(f"Root jump frames: {report.root_jump_frames[:max_frames]}")
    if report.is_valid:
        lines.append("Motion safety: OK")
    elif report.has_blocking_errors:
        lines.append("Motion safety: blocked until fixed")
    else:
        lines.append("Motion safety: warnings")
    return "\n".join(lines)
