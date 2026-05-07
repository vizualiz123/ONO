import torch

from kimodo.geometry import axis_angle_to_matrix, matrix_to_quaternion
from kimodo.safety.continuity import canonicalize_quaternions_timeline, slerp_blend
from kimodo.safety.detect import detect_rotation_spikes
from kimodo.safety.smooth import smooth_anomalies


def test_canonicalize_quaternions_timeline_flips_signs_to_short_path():
    q = torch.tensor(
        [
            [[1.0, 0.0, 0.0, 0.0]],
            [[-1.0, 0.0, 0.0, 0.0]],
            [[1.0, 0.0, 0.0, 0.0]],
        ]
    )
    fixed = canonicalize_quaternions_timeline(q)
    dots = (fixed[:-1] * fixed[1:]).sum(dim=-1)
    assert torch.all(dots >= 0)


def test_slerp_blend_uses_shortest_path_for_opposite_signs():
    q0 = torch.tensor([1.0, 0.0, 0.0, 0.0])
    q1 = -q0
    out = slerp_blend(q0, q1, 0.5)
    assert torch.allclose(out, q0, atol=1e-6)


def test_detect_and_smooth_rotation_spike():
    axis_angle = torch.zeros((5, 1, 3))
    axis_angle[2, 0, 1] = torch.pi
    mats = axis_angle_to_matrix(axis_angle)
    mask = detect_rotation_spikes(mats, threshold_deg=90.0)
    assert bool(mask[2, 0])

    fixed = smooth_anomalies(mats, mask, window=2)
    q = canonicalize_quaternions_timeline(matrix_to_quaternion(fixed))
    dots = (q[:-1] * q[1:]).sum(dim=-1)
    assert torch.all(dots > 0.0)
