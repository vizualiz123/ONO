using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using pxr;

namespace AvatarDesktop.Rendering;

internal static class UsdWpfMeshLoader
{
    private const string UsdNuGetPackageName = "universalscenedescription";
    private static readonly object Sync = new();
    private static bool _runtimeInitialized;

    public static bool TryCreateModel(string usdPath, Material fallbackMaterial, out Model3D model)
    {
        model = new Model3DGroup();

        if (string.IsNullOrWhiteSpace(usdPath) || !File.Exists(usdPath))
        {
            return false;
        }

        try
        {
            if (!EnsureUsdRuntimeInitialized())
            {
                return false;
            }

            var stage = UsdStage.Open(usdPath);
            if (stage is null)
            {
                return false;
            }

            var axisMode = GetAxisConversionMode(stage);
            var assetsDirectory = ResolveAssetsDirectoryFromPath(usdPath);

            using var xformCache = new UsdGeomXformCache();
            var rootGroup = new Model3DGroup();
            var bounds = new BoundsAccumulator();

            var prims = stage.GetAllPrims();
            var meshCount = 0;
            for (var i = 0; i < prims.Count; i++)
            {
                var prim = prims[i];
                if (!string.Equals(prim.GetTypeName().ToString(), "Mesh", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryCreateGeometryModel(
                        prim,
                        usdPath,
                        assetsDirectory,
                        fallbackMaterial,
                        xformCache,
                        axisMode,
                        out var meshModel,
                        out var meshBounds))
                {
                    continue;
                }

                rootGroup.Children.Add(meshModel);
                bounds.Include(meshBounds);
                meshCount++;
            }

            if (meshCount == 0 || !bounds.HasValue)
            {
                return false;
            }

            rootGroup.Transform = BuildAvatarNormalizationTransform(bounds);
            model = rootGroup;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool EnsureRuntimeReady()
    {
        return EnsureUsdRuntimeInitialized();
    }

    internal static Material? TryCreateTexturedMaterialForExternalMesh(string usdPath, UsdPrim prim, bool hasUvCoordinates)
    {
        var assetsDirectory = ResolveAssetsDirectoryFromPath(usdPath);
        return TryCreateTexturedMaterialForMesh(usdPath, assetsDirectory, prim, hasUvCoordinates);
    }

    private static bool TryCreateGeometryModel(
        UsdPrim prim,
        string usdPath,
        string? assetsDirectory,
        Material fallbackMaterial,
        UsdGeomXformCache xformCache,
        AxisConversionMode axisMode,
        out GeometryModel3D geometryModel,
        out BoundsAccumulator bounds)
    {
        geometryModel = new GeometryModel3D();
        bounds = new BoundsAccumulator();

        try
        {
            var usdMesh = new UsdGeomMesh(prim);
            var pointBased = new UsdGeomPointBased(prim);

            var points = (VtVec3fArray)pointBased.GetPointsAttr().Get();
            var faceCounts = (VtIntArray)usdMesh.GetFaceVertexCountsAttr().Get();
            var faceIndices = (VtIntArray)usdMesh.GetFaceVertexIndicesAttr().Get();

            if (points is null || faceCounts is null || faceIndices is null)
            {
                return false;
            }

            var pointCount = (int)points.size();
            if (pointCount <= 0)
            {
                return false;
            }

            var uvData = TryReadUvData(prim);
            var worldMatrix = xformCache.GetLocalToWorldTransform(prim);
            var transformedPoints = new Point3D[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                var p = worldMatrix.TransformAffine(points[i]);
                transformedPoints[i] = ConvertUsdPointToWpf(p, axisMode);
            }

            var positions = new List<Point3D>(Math.Min(pointCount * 2, 64_000));
            var texCoords = new List<Point>(Math.Min(pointCount * 2, 64_000));
            var sourcePointIndexByVertex = new List<int>(Math.Min(pointCount * 2, 64_000));
            var triangleIndices = new List<int>(Math.Min((int)faceIndices.size(), 128_000));
            var accumNormalsBySourcePoint = new Vector3D[pointCount];
            var vertexMap = new Dictionary<VertexKey, int>(pointCount);

            var faceCursor = 0;
            for (var faceIndex = 0; faceIndex < (int)faceCounts.size(); faceIndex++)
            {
                var corners = faceCounts[faceIndex];
                if (corners < 3)
                {
                    faceCursor += Math.Max(0, corners);
                    continue;
                }

                if (faceCursor + corners > (int)faceIndices.size())
                {
                    break;
                }

                var basePointIndex = faceIndices[faceCursor];
                if (!IsValidPointIndex(basePointIndex, pointCount))
                {
                    faceCursor += corners;
                    continue;
                }

                for (var j = 1; j < corners - 1; j++)
                {
                    var i0 = faceIndices[faceCursor];
                    var i1 = faceIndices[faceCursor + j];
                    var i2 = faceIndices[faceCursor + j + 1];
                    if (!IsValidPointIndex(i0, pointCount) || !IsValidPointIndex(i1, pointCount) || !IsValidPointIndex(i2, pointCount))
                    {
                        continue;
                    }

                    var p0 = transformedPoints[i0];
                    var p1 = transformedPoints[i1];
                    var p2 = transformedPoints[i2];

                    var faceNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
                    if (faceNormal.LengthSquared < 1e-12)
                    {
                        continue;
                    }

                    faceNormal.Normalize();
                    accumNormalsBySourcePoint[i0] += faceNormal;
                    accumNormalsBySourcePoint[i1] += faceNormal;
                    accumNormalsBySourcePoint[i2] += faceNormal;

                    var v0 = GetOrCreateVertex(i0, faceIndex, faceCursor, 0);
                    var v1 = GetOrCreateVertex(i1, faceIndex, faceCursor, j);
                    var v2 = GetOrCreateVertex(i2, faceIndex, faceCursor, j + 1);

                    triangleIndices.Add(v0);
                    triangleIndices.Add(v1);
                    triangleIndices.Add(v2);
                }

                faceCursor += corners;
            }

            if (positions.Count == 0 || triangleIndices.Count == 0)
            {
                return false;
            }

            var normals = new List<Vector3D>(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                var sourcePointIndex = sourcePointIndexByVertex[i];
                var n = accumNormalsBySourcePoint[sourcePointIndex];
                if (n.LengthSquared < 1e-12)
                {
                    n = new Vector3D(0, 1, 0);
                }
                else
                {
                    n.Normalize();
                }

                normals.Add(n);
                bounds.Include(positions[i]);
            }

            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(positions),
                Normals = new Vector3DCollection(normals),
                TriangleIndices = new Int32Collection(triangleIndices),
            };

            if (uvData.HasValue && texCoords.Count == positions.Count)
            {
                mesh.TextureCoordinates = new PointCollection(texCoords);
            }

            mesh.Freeze();

            var material = TryCreateTexturedMaterialForMesh(usdPath, assetsDirectory, prim, uvData.HasValue)
                ?? fallbackMaterial;

            geometryModel = new GeometryModel3D
            {
                Geometry = mesh,
                Material = material,
                BackMaterial = material,
            };

            return true;

            int GetOrCreateVertex(int sourcePointIndex, int currentFaceIndex, int currentFaceCursor, int faceCornerOffset)
            {
                var uvKey = -1;
                var uvPoint = default(Point);
                if (uvData.HasValue &&
                    TryResolveUvForCorner(uvData.Value, sourcePointIndex, currentFaceIndex, currentFaceCursor, faceCornerOffset, out uvKey, out uvPoint))
                {
                    // Use UV-aware vertex split for seams.
                }
                else
                {
                    uvKey = -1;
                }

                var key = new VertexKey(sourcePointIndex, uvKey);
                if (vertexMap.TryGetValue(key, out var existingIndex))
                {
                    return existingIndex;
                }

                var vertexIndex = positions.Count;
                vertexMap[key] = vertexIndex;
                positions.Add(transformedPoints[sourcePointIndex]);
                texCoords.Add(uvPoint);
                sourcePointIndexByVertex.Add(sourcePointIndex);
                return vertexIndex;
            }
        }
        catch
        {
            return false;
        }
    }

    internal static UvData? TryReadUvData(UsdPrim prim)
    {
        try
        {
            var primvarsApi = new UsdGeomPrimvarsAPI(prim);

            UsdGeomPrimvar st = primvarsApi.GetPrimvar(new TfToken("st"));
            if (!st.IsDefined() || !st.HasValue())
            {
                foreach (var candidate in new[] { "UVMap", "map1", "uv", "st0" })
                {
                    st = primvarsApi.GetPrimvar(new TfToken(candidate));
                    if (st.IsDefined() && st.HasValue())
                    {
                        break;
                    }
                }
            }

            if (!st.IsDefined() || !st.HasValue())
            {
                return null;
            }

            var values = (VtVec2fArray)st.GetAttr().Get();
            if (values is null || values.size() == 0)
            {
                return null;
            }

            VtIntArray? indices = null;
            if (st.IsIndexed())
            {
                var uvIndices = new VtIntArray();
                if (st.GetIndices(uvIndices) && uvIndices.size() > 0)
                {
                    indices = uvIndices;
                }
            }

            var interpolation = st.GetInterpolation()?.ToString() ?? string.Empty;
            return new UvData(values, indices, interpolation);
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryResolveUvForCorner(
        UvData uvData,
        int sourcePointIndex,
        int faceIndex,
        int faceCursor,
        int faceCornerOffset,
        out int uvKey,
        out Point uvPoint)
    {
        uvKey = -1;
        uvPoint = default;

        var rawUvIndex = uvData.Interpolation switch
        {
            "faceVarying" or "facevarying" => faceCursor + faceCornerOffset,
            "uniform" => faceIndex,
            "constant" => 0,
            "varying" or "vertex" => sourcePointIndex,
            _ => sourcePointIndex
        };

        if (rawUvIndex < 0)
        {
            return false;
        }

        if (uvData.Indices is not null)
        {
            if (rawUvIndex >= (int)uvData.Indices.size())
            {
                return false;
            }

            rawUvIndex = uvData.Indices[rawUvIndex];
        }

        if (rawUvIndex < 0 || rawUvIndex >= (int)uvData.Values.size())
        {
            return false;
        }

        var uv = uvData.Values[rawUvIndex];
        uvPoint = new Point(uv[0], 1.0 - uv[1]);
        uvKey = rawUvIndex;
        return true;
    }

    internal static bool IsValidPointIndex(int index, int pointCount)
    {
        return index >= 0 && index < pointCount;
    }

    internal static AxisConversionMode GetAxisConversionMode(UsdStage stage)
    {
        try
        {
            var upAxis = UsdGeom.UsdGeomGetStageUpAxis(stage)?.ToString()?.Trim();
            if (string.Equals(upAxis, "Z", StringComparison.OrdinalIgnoreCase))
            {
                return AxisConversionMode.ZUpToYUp;
            }

            if (string.Equals(upAxis, "Y", StringComparison.OrdinalIgnoreCase))
            {
                return AxisConversionMode.YUp;
            }
        }
        catch
        {
            // Ignore and use default.
        }

        return AxisConversionMode.YUp;
    }

    internal static Point3D ConvertUsdPointToWpf(GfVec3f usdPoint, AxisConversionMode axisMode)
    {
        var x = usdPoint[0];
        var y = usdPoint[1];
        var z = usdPoint[2];

        return axisMode switch
        {
            AxisConversionMode.ZUpToYUp => new Point3D(x, z, -y),
            _ => new Point3D(x, y, z),
        };
    }

    internal static Transform3D BuildAvatarNormalizationTransform(BoundsAccumulator bounds)
    {
        if (!bounds.HasValue)
        {
            return Transform3D.Identity;
        }

        var sizeX = bounds.MaxX - bounds.MinX;
        var sizeY = bounds.MaxY - bounds.MinY;
        var sizeZ = bounds.MaxZ - bounds.MinZ;
        var maxSize = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
        if (maxSize <= 1e-6)
        {
            return Transform3D.Identity;
        }

        var targetHeight = 2.2;
        var scale = sizeY > 1e-6 ? targetHeight / sizeY : 2.0 / maxSize;
        var centerX = (bounds.MinX + bounds.MaxX) * 0.5;
        var centerZ = (bounds.MinZ + bounds.MaxZ) * 0.5;
        var baseY = bounds.MinY;

        var group = new Transform3DGroup();
        group.Children.Add(new ScaleTransform3D(scale, scale, scale));
        group.Children.Add(new TranslateTransform3D(-centerX * scale, (-baseY * scale) - 1.05, -centerZ * scale));
        return group;
    }

    private static Material? TryCreateTexturedMaterialForMesh(string usdPath, string? assetsDirectory, UsdPrim prim, bool hasUvCoordinates)
    {
        if (!hasUvCoordinates)
        {
            return null;
        }

        var texturePath = ResolveBaseColorTexturePath(usdPath, assetsDirectory, prim);
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(texturePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                TileMode = TileMode.None,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(brush, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(brush, 2.0);
            brush.Freeze();

            var material = new DiffuseMaterial(brush);
            material.Freeze();
            return material;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveBaseColorTexturePath(string usdPath, string? assetsDirectory, UsdPrim prim)
    {
        var texturesRoot = ResolveTexturesRootFromUsdPath(usdPath, assetsDirectory);
        if (string.IsNullOrWhiteSpace(texturesRoot) || !Directory.Exists(texturesRoot))
        {
            return null;
        }

        var meshName = prim.GetPath().GetName();
        if (string.IsNullOrWhiteSpace(meshName))
        {
            return null;
        }

        var candidateDirs = EnumerateTextureDirectoryCandidates(texturesRoot, prim, meshName).ToList();

        if (candidateDirs.Count == 0)
        {
            return null;
        }

        foreach (var dir in candidateDirs)
        {
            var folderName = Path.GetFileName(dir);
            var exact = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file).Equals($"{folderName}_BaseColor", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            var fallback = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file => Path.GetFileName(file).Contains("_BaseColor", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private static string? ResolveTexturesRootFromUsdPath(string usdPath, string? assetsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(assetsDirectory))
        {
            var texturesRoot = Path.Combine(assetsDirectory, "Materials", "Textures");
            if (Directory.Exists(texturesRoot))
            {
                return texturesRoot;
            }
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(usdPath) ?? string.Empty);
        while (current is not null)
        {
            var localTexturesRoot = Path.Combine(current.FullName, "Materials", "Textures");
            if (Directory.Exists(localTexturesRoot))
            {
                return localTexturesRoot;
            }

            current = current.Parent;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTextureDirectoryCandidates(string texturesRoot, UsdPrim prim, string meshName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Normalize(string value)
        {
            return value
                .Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .Replace(" ", "_", StringComparison.Ordinal);
        }

        IEnumerable<string> AddDirsByName(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                yield break;
            }

            var normalized = Normalize(folderName);
            if (normalized.Length == 0)
            {
                yield break;
            }

            var exact = Path.Combine(texturesRoot, normalized);
            if (Directory.Exists(exact) && seen.Add(exact))
            {
                yield return exact;
            }

            foreach (var dir in Directory.EnumerateDirectories(texturesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var dirName = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(dirName)
                    && string.Equals(Normalize(dirName), normalized, StringComparison.OrdinalIgnoreCase)
                    && seen.Add(dir))
                {
                    yield return dir;
                }
            }
        }

        // 1) Material binding name is the most accurate source for texture folder names.
        foreach (var materialFolder in TryGetBoundMaterialFolderNames(prim))
        {
            foreach (var dir in AddDirsByName(materialFolder))
            {
                yield return dir;
            }
        }

        // 2) Direct mesh-name match.
        foreach (var dir in AddDirsByName(meshName))
        {
            yield return dir;
        }

        // 3) Known mesh -> material aliases for Character Creator style names.
        foreach (var alias in GetTextureFolderAliases(meshName))
        {
            foreach (var dir in AddDirsByName(alias))
            {
                yield return dir;
            }
        }
    }

    private static IEnumerable<string> TryGetBoundMaterialFolderNames(UsdPrim prim)
    {
        try
        {
            var bindingApi = new UsdShadeMaterialBindingAPI(prim);
            if (!bindingApi.GetPrim().IsValid())
            {
                return Array.Empty<string>();
            }

            var material = bindingApi.ComputeBoundMaterial();
            var materialPrim = material.GetPrim();
            if (!materialPrim.IsValid())
            {
                return Array.Empty<string>();
            }

            var materialName = materialPrim.GetPath().GetName();
            if (!string.IsNullOrWhiteSpace(materialName))
            {
                return new[] { materialName };
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetTextureFolderAliases(string meshName)
    {
        if (string.IsNullOrWhiteSpace(meshName))
        {
            yield break;
        }

        var name = meshName.Trim();

        // Character Creator mesh names frequently differ from material/texture folders.
        if (name.Contains("CC_Base_Body", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Skin_Body";
        }

        if (name.Contains("CC_Game_Body", StringComparison.OrdinalIgnoreCase))
        {
            yield return "CC_Game_Body_Ga_Skin_Body";
            yield return "Ga_Skin_Body";
            yield return "Std_Skin_Body";
            yield return "Std_Skin_Head";
        }

        if (name.Contains("CC_Base_Head", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Skin_Head";
        }

        if (name.Contains("CC_Base_Arm", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Skin_Arm";
        }

        if (name.Contains("CC_Base_Leg", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Skin_Leg";
        }

        if (name.Contains("Tongue", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Tongue";
        }

        if (name.Contains("UpperTeeth", StringComparison.OrdinalIgnoreCase) || name.Contains("UTeeth", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Upper_Teeth";
        }

        if (name.Contains("LowerTeeth", StringComparison.OrdinalIgnoreCase) || name.Contains("LTeeth", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Lower_Teeth";
        }

        if (name.Contains("Teeth", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Upper_Teeth";
            yield return "Std_Lower_Teeth";
        }

        if (name.Contains("Nail", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Std_Nails";
        }
    }

    private static string? ResolveAssetsDirectoryFromPath(string usdPath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(usdPath) ?? string.Empty);
        while (current is not null)
        {
            if (string.Equals(current.Name, "assets", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool EnsureUsdRuntimeInitialized()
    {
        lock (Sync)
        {
            if (_runtimeInitialized)
            {
                return true;
            }

            try
            {
                var runtimeUsdRoot = PrepareAppLocalUsdRuntime();
                if (string.IsNullOrWhiteSpace(runtimeUsdRoot))
                {
                    return false;
                }

                var runtimeRoot = Directory.GetParent(runtimeUsdRoot)?.FullName;
                if (!string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    PrependProcessPath(runtimeRoot);
                    AppendProcessEnvPath("PXR_PLUGINPATH_NAME", runtimeRoot);
                }

                PrependProcessPath(runtimeUsdRoot);
                AppendProcessEnvPath("PXR_PLUGINPATH_NAME", runtimeUsdRoot);

                _runtimeInitialized = true;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string? PrepareAppLocalUsdRuntime()
    {
        var appRuntimeRoot = Path.Combine(AppContext.BaseDirectory, "usd_runtime");
        var appUsdRoot = Path.Combine(appRuntimeRoot, "USD");

        if (File.Exists(Path.Combine(appUsdRoot, "UsdCs.dll")) && File.Exists(Path.Combine(appUsdRoot, "plugInfo.json")))
        {
            EnsureRootDllCopies(appRuntimeRoot, appUsdRoot);
            return appUsdRoot;
        }

        var packageContentRoot = ResolveUsdPackageContentRoot();
        if (string.IsNullOrWhiteSpace(packageContentRoot))
        {
            return null;
        }

        var srcUsd = Path.Combine(packageContentRoot, "USD");
        var srcDlls = Path.Combine(packageContentRoot, "DLLs");
        if (!Directory.Exists(srcUsd) || !Directory.Exists(srcDlls))
        {
            return null;
        }

        CopyDirectory(srcUsd, appUsdRoot);
        Directory.CreateDirectory(appRuntimeRoot);
        Directory.CreateDirectory(appUsdRoot);

        foreach (var dllFile in Directory.EnumerateFiles(srcDlls, "*.dll", SearchOption.TopDirectoryOnly))
        {
            CopyFileIfMissing(dllFile, Path.Combine(appUsdRoot, Path.GetFileName(dllFile)));
            // Some USD plugin resolution on Windows may look in the parent runtime folder.
            CopyFileIfMissing(dllFile, Path.Combine(appRuntimeRoot, Path.GetFileName(dllFile)));
        }

        return appUsdRoot;
    }

    private static void EnsureRootDllCopies(string appRuntimeRoot, string appUsdRoot)
    {
        try
        {
            Directory.CreateDirectory(appRuntimeRoot);
            foreach (var dll in Directory.EnumerateFiles(appUsdRoot, "*.dll", SearchOption.TopDirectoryOnly))
            {
                CopyFileIfMissing(dll, Path.Combine(appRuntimeRoot, Path.GetFileName(dll)));
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string? ResolveUsdPackageContentRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return null;
        }

        var packageRoot = Path.Combine(userProfile, ".nuget", "packages", UsdNuGetPackageName);
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var versionDirs = Directory.EnumerateDirectories(packageRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var versionDir in versionDirs)
        {
            var candidate = Path.Combine(versionDir, "contentFiles", "any", "net8.0");
            if (Directory.Exists(Path.Combine(candidate, "USD")) && Directory.Exists(Path.Combine(candidate, "DLLs")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destinationDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destinationDir, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (!File.Exists(target))
            {
                File.Copy(file, target, overwrite: false);
            }
        }
    }

    private static void CopyFileIfMissing(string sourcePath, string destinationPath)
    {
        var targetDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        if (!File.Exists(destinationPath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static void PrependProcessPath(string directory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (current.Split(Path.PathSeparator).Any(p => string.Equals(p, directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var next = string.IsNullOrWhiteSpace(current)
            ? directory
            : directory + Path.PathSeparator + current;
        Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.Process);
    }

    private static void AppendProcessEnvPath(string key, string directory)
    {
        var current = Environment.GetEnvironmentVariable(key) ?? string.Empty;
        var segments = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(p => string.Equals(p, directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var next = string.IsNullOrWhiteSpace(current)
            ? directory
            : current + Path.PathSeparator + directory;
        Environment.SetEnvironmentVariable(key, next, EnvironmentVariableTarget.Process);
    }

    internal enum AxisConversionMode
    {
        YUp,
        ZUpToYUp,
    }

    internal readonly record struct VertexKey(int SourcePointIndex, int UvKey);
    internal readonly record struct UvData(VtVec2fArray Values, VtIntArray? Indices, string Interpolation);

    internal struct BoundsAccumulator
    {
        public bool HasValue;
        public double MinX;
        public double MinY;
        public double MinZ;
        public double MaxX;
        public double MaxY;
        public double MaxZ;

        public void Include(Point3D p)
        {
            if (!HasValue)
            {
                HasValue = true;
                MinX = MaxX = p.X;
                MinY = MaxY = p.Y;
                MinZ = MaxZ = p.Z;
                return;
            }

            MinX = Math.Min(MinX, p.X);
            MinY = Math.Min(MinY, p.Y);
            MinZ = Math.Min(MinZ, p.Z);
            MaxX = Math.Max(MaxX, p.X);
            MaxY = Math.Max(MaxY, p.Y);
            MaxZ = Math.Max(MaxZ, p.Z);
        }

        public void Include(BoundsAccumulator other)
        {
            if (!other.HasValue)
            {
                return;
            }

            if (!HasValue)
            {
                this = other;
                return;
            }

            MinX = Math.Min(MinX, other.MinX);
            MinY = Math.Min(MinY, other.MinY);
            MinZ = Math.Min(MinZ, other.MinZ);
            MaxX = Math.Max(MaxX, other.MaxX);
            MaxY = Math.Max(MaxY, other.MaxY);
            MaxZ = Math.Max(MaxZ, other.MaxZ);
        }
    }
}
