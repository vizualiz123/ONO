# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Motion safety helpers for continuity checks, validation, and lightweight repair."""

from .continuity import (
    angular_distance,
    canonicalize_quaternions_timeline,
    canonicalize_rotation_matrices,
    normalize_quaternion,
    slerp_blend,
)
from .detect import detect_nonfinite_frames, detect_rotation_spikes
from .smooth import smooth_anomalies
from .validate import MotionValidationReport, format_validation_report, validate_motion

__all__ = [
    "MotionValidationReport",
    "angular_distance",
    "canonicalize_quaternions_timeline",
    "canonicalize_rotation_matrices",
    "detect_nonfinite_frames",
    "detect_rotation_spikes",
    "format_validation_report",
    "normalize_quaternion",
    "slerp_blend",
    "smooth_anomalies",
    "validate_motion",
]
