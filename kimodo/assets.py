# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0

from pathlib import Path

PACKAGE_ROOT = Path(__file__).resolve().parent
ASSETS_ROOT = PACKAGE_ROOT / "assets"
DEMO_ASSETS_ROOT = ASSETS_ROOT / "demo"
DEMO_EXAMPLES_ROOT = DEMO_ASSETS_ROOT / "examples"
DEMO_USD_ROOT = DEMO_ASSETS_ROOT / "usd"
SKELETONS_ROOT = ASSETS_ROOT / "skeletons"
SOMA_ASSETS_ROOT = ASSETS_ROOT / "SOMA"

TEST_USD_ASSETS = {
    "Nein3D Manny (UE-style test)": DEMO_USD_ROOT / "nein3d_manny_test.usda",
    "Nein3D Quinn (UE-style test)": DEMO_USD_ROOT / "nein3d_quinn_test.usda",
    "Nein3D Greybox test set": DEMO_USD_ROOT / "nein3d_greybox_test_set.usda",
}


def skeleton_asset_path(*parts: str) -> Path:
    return SKELETONS_ROOT.joinpath(*parts)


def demo_asset_path(*parts: str) -> Path:
    return DEMO_ASSETS_ROOT.joinpath(*parts)
