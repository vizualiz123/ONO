from __future__ import annotations

import math
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
USD_DIR = ROOT / "kimodo" / "assets" / "demo" / "usd"


def _require_usd():
    try:
        from pxr import Gf, Sdf, Usd, UsdGeom
    except ImportError as exc:
        raise SystemExit("usd-core is required: pip install usd-core") from exc
    return Gf, Sdf, Usd, UsdGeom


def _sphere_mesh(center, radius, *, segments=28, rings=14):
    cx, cy, cz = center
    rx, ry, rz = radius
    vertices = []
    for ring in range(rings + 1):
        phi = math.pi * ring / rings
        y = math.cos(phi)
        r = math.sin(phi)
        for seg in range(segments):
            theta = 2.0 * math.pi * seg / segments
            vertices.append(
                (
                    cx + rx * r * math.cos(theta),
                    cy + ry * y,
                    cz + rz * r * math.sin(theta),
                )
            )

    counts = []
    indices = []
    for ring in range(rings):
        for seg in range(segments):
            a = ring * segments + seg
            b = ring * segments + (seg + 1) % segments
            c = (ring + 1) * segments + (seg + 1) % segments
            d = (ring + 1) * segments + seg
            counts.append(4)
            indices.extend([a, b, c, d])
    return vertices, counts, indices


def _box_mesh(center, size):
    cx, cy, cz = center
    sx, sy, sz = (value * 0.5 for value in size)
    vertices = [
        (cx - sx, cy - sy, cz - sz),
        (cx + sx, cy - sy, cz - sz),
        (cx + sx, cy + sy, cz - sz),
        (cx - sx, cy + sy, cz - sz),
        (cx - sx, cy - sy, cz + sz),
        (cx + sx, cy - sy, cz + sz),
        (cx + sx, cy + sy, cz + sz),
        (cx - sx, cy + sy, cz + sz),
    ]
    indices = [
        0,
        1,
        2,
        3,
        1,
        5,
        6,
        2,
        5,
        4,
        7,
        6,
        4,
        0,
        3,
        7,
        3,
        2,
        6,
        7,
        4,
        5,
        1,
        0,
    ]
    return vertices, [4] * 6, indices


def _add_mesh(stage, path, vertices, counts, indices, color):
    _Gf, _Sdf, _Usd, UsdGeom = _require_usd()
    mesh = UsdGeom.Mesh.Define(stage, path)
    mesh.CreatePointsAttr(vertices)
    mesh.CreateFaceVertexCountsAttr(counts)
    mesh.CreateFaceVertexIndicesAttr(indices)
    mesh.CreateDisplayColorAttr([color])
    return mesh


def _add_part(stage, root_path, name, *, center, radius=None, size=None, color):
    safe_name = name.replace(" ", "_").replace("-", "_")
    if radius is not None:
        vertices, counts, indices = _sphere_mesh(center, radius)
    elif size is not None:
        vertices, counts, indices = _box_mesh(center, size)
    else:
        raise ValueError(name)
    _add_mesh(stage, f"{root_path}/{safe_name}", vertices, counts, indices, color)


def _make_stage(path: Path, title: str, *, description: str):
    _Gf, Sdf, Usd, UsdGeom = _require_usd()
    stage = Usd.Stage.CreateNew(str(path))
    stage.SetMetadata("comment", description)
    UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.y)
    UsdGeom.SetStageMetersPerUnit(stage, 1.0)
    world = UsdGeom.Xform.Define(stage, "/World")
    stage.SetDefaultPrim(world.GetPrim())
    root = UsdGeom.Xform.Define(stage, f"/World/{title}")
    root.GetPrim().CreateAttribute("nein3d:assetKind", Sdf.ValueTypeNames.String).Set("test_mannequin")
    return stage, f"/World/{title}"


def _mannequin_parts(*, gender: str):
    if gender == "quinn":
        body = (0.72, 0.77, 0.82)
        accent = (0.24, 0.47, 0.82)
        slim = 0.86
        height = 1.72
    else:
        body = (0.68, 0.71, 0.76)
        accent = (0.11, 0.32, 0.62)
        slim = 1.0
        height = 1.84

    s = slim
    h = height / 1.84
    return [
        ("pelvis", (0.0, 0.88 * h, 0.0), (0.26 * s, 0.15 * h, 0.18 * s), None, body),
        ("torso", (0.0, 1.20 * h, 0.0), (0.30 * s, 0.34 * h, 0.18 * s), None, body),
        ("chest", (0.0, 1.42 * h, 0.0), (0.34 * s, 0.17 * h, 0.19 * s), None, body),
        ("neck", (0.0, 1.58 * h, 0.0), (0.09 * s, 0.08 * h, 0.09 * s), None, accent),
        ("head", (0.0, 1.72 * h, 0.0), (0.14 * s, 0.17 * h, 0.13 * s), None, body),
        ("left_shoulder", (-0.34 * s, 1.45 * h, 0.0), (0.09 * s, 0.09 * h, 0.10 * s), None, accent),
        ("right_shoulder", (0.34 * s, 1.45 * h, 0.0), (0.09 * s, 0.09 * h, 0.10 * s), None, accent),
        ("left_upper_arm", (-0.53 * s, 1.42 * h, 0.0), (0.20 * s, 0.07 * h, 0.075 * s), None, body),
        ("right_upper_arm", (0.53 * s, 1.42 * h, 0.0), (0.20 * s, 0.07 * h, 0.075 * s), None, body),
        ("left_forearm", (-0.81 * s, 1.42 * h, 0.0), (0.20 * s, 0.065 * h, 0.07 * s), None, body),
        ("right_forearm", (0.81 * s, 1.42 * h, 0.0), (0.20 * s, 0.065 * h, 0.07 * s), None, body),
        ("left_hand", (-1.05 * s, 1.42 * h, 0.0), (0.075 * s, 0.055 * h, 0.085 * s), None, accent),
        ("right_hand", (1.05 * s, 1.42 * h, 0.0), (0.075 * s, 0.055 * h, 0.085 * s), None, accent),
        ("left_thigh", (-0.14 * s, 0.58 * h, 0.0), (0.105 * s, 0.30 * h, 0.115 * s), None, body),
        ("right_thigh", (0.14 * s, 0.58 * h, 0.0), (0.105 * s, 0.30 * h, 0.115 * s), None, body),
        ("left_shin", (-0.14 * s, 0.24 * h, 0.0), (0.085 * s, 0.27 * h, 0.095 * s), None, body),
        ("right_shin", (0.14 * s, 0.24 * h, 0.0), (0.085 * s, 0.27 * h, 0.095 * s), None, body),
        ("left_foot", (-0.14 * s, 0.035, 0.09 * s), None, (0.22 * s, 0.07, 0.34 * s), accent),
        ("right_foot", (0.14 * s, 0.035, 0.09 * s), None, (0.22 * s, 0.07, 0.34 * s), accent),
    ]


def create_mannequin(path: Path, *, title: str, gender: str) -> None:
    stage, root_path = _make_stage(
        path,
        title,
        description="Procedural Nein3D test mannequin, inspired by game-engine greybox mannequins. No Epic assets.",
    )
    for name, center, radius, size, color in _mannequin_parts(gender=gender):
        if radius is None:
            _add_part(stage, root_path, name, center=center, size=size, color=color)
        else:
            _add_part(stage, root_path, name, center=center, radius=radius, color=color)
    _add_part(stage, root_path, "one_meter_reference", center=(1.35, 0.5, 0.0), size=(0.08, 1.0, 0.08), color=(0.08, 0.85, 0.72))
    stage.GetRootLayer().Save()


def create_greybox_set(path: Path) -> None:
    stage, root_path = _make_stage(
        path,
        "Nein3D_Greybox_Test_Set",
        description="Simple metric USD props for testing scale, occlusion, center, opacity, and wireframe.",
    )
    _add_part(stage, root_path, "meter_cube", center=(0.0, 0.5, 0.0), size=(1.0, 1.0, 1.0), color=(0.35, 0.39, 0.44))
    _add_part(stage, root_path, "platform", center=(0.0, -0.025, 0.0), size=(2.4, 0.05, 2.4), color=(0.18, 0.20, 0.23))
    _add_part(stage, root_path, "door_frame_left", center=(-0.55, 1.05, -0.8), size=(0.10, 2.1, 0.12), color=(0.22, 0.43, 0.70))
    _add_part(stage, root_path, "door_frame_right", center=(0.55, 1.05, -0.8), size=(0.10, 2.1, 0.12), color=(0.22, 0.43, 0.70))
    _add_part(stage, root_path, "door_frame_top", center=(0.0, 2.05, -0.8), size=(1.2, 0.10, 0.12), color=(0.22, 0.43, 0.70))
    stage.GetRootLayer().Save()


def main() -> None:
    USD_DIR.mkdir(parents=True, exist_ok=True)
    create_mannequin(USD_DIR / "nein3d_manny_test.usda", title="Nein3D_Manny_Test", gender="manny")
    create_mannequin(USD_DIR / "nein3d_quinn_test.usda", title="Nein3D_Quinn_Test", gender="quinn")
    create_greybox_set(USD_DIR / "nein3d_greybox_test_set.usda")
    print(f"Wrote test USD assets to {USD_DIR}")


if __name__ == "__main__":
    main()
