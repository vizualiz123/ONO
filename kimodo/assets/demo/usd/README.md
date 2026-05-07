# Nein3D demo USD assets

These files are procedural test assets for the local Nein3D/Kimodo demo.

They are not Epic Games assets and do not contain Unreal Engine mannequin
geometry, skeletons, materials, or textures. The `Manny` and `Quinn` names here
describe the intended test role: neutral game-engine-style humanoid mannequins
for checking scale, centering, opacity, wireframe, and USD import behavior.

Assets:

- `nein3d_manny_test.usda` - tall neutral humanoid test mannequin.
- `nein3d_quinn_test.usda` - slimmer neutral humanoid test mannequin.
- `nein3d_greybox_test_set.usda` - metric cube, platform, and door-frame props.

Regenerate with:

```powershell
.\.venv\Scripts\python.exe scripts\create_nein3d_test_usd_assets.py
```
