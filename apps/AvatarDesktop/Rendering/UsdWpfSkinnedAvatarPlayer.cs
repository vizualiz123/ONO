using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using pxr;

namespace AvatarDesktop.Rendering;

internal sealed class UsdWpfSkinnedAvatarPlayer : IDisposable
{
    private readonly UsdStage _stage;
    private readonly UsdGeomXformCache _xformCache;
    private readonly UsdSkelCache _skelCache;
    private readonly UsdSkelSkeletonQuery _skeletonQuery;
    private readonly UsdSkelTopology _topology;
    private readonly VtMatrix4dArray _jointLocal;
    private readonly VtMatrix4dArray _jointWorld;
    private readonly VtMatrix4dArray _jointBindWorld;
    private readonly VtMatrix4dArray _jointSkinXforms;
    private readonly int _jawJointIndex;
    private readonly int _headJointIndex;
    private readonly int _neckJointIndex;
    private readonly List<MeshRuntime> _meshes;
    private readonly UsdWpfMeshLoader.AxisConversionMode _axisMode;
    private readonly Model3DGroup _rootGroup;
    private readonly double _timeStart;
    private readonly double _timeEnd;
    private readonly double _timeCodesPerSecond;
    private readonly bool _isAnimated;
    private double _proceduralMouthOpen;
    private double _proceduralHeadCompensation;
    private double _currentTime;
    private bool _disposed;

    private UsdWpfSkinnedAvatarPlayer(
        UsdStage stage,
        UsdGeomXformCache xformCache,
        UsdSkelCache skelCache,
        UsdSkelSkeletonQuery skeletonQuery,
        UsdSkelTopology topology,
        VtMatrix4dArray jointLocal,
        VtMatrix4dArray jointWorld,
        VtMatrix4dArray jointBindWorld,
        VtMatrix4dArray jointSkinXforms,
        int jawJointIndex,
        int headJointIndex,
        int neckJointIndex,
        List<MeshRuntime> meshes,
        UsdWpfMeshLoader.AxisConversionMode axisMode,
        Model3DGroup rootGroup,
        double timeStart,
        double timeEnd,
        double timeCodesPerSecond,
        bool isAnimated)
    {
        _stage = stage;
        _xformCache = xformCache;
        _skelCache = skelCache;
        _skeletonQuery = skeletonQuery;
        _topology = topology;
        _jointLocal = jointLocal;
        _jointWorld = jointWorld;
        _jointBindWorld = jointBindWorld;
        _jointSkinXforms = jointSkinXforms;
        _jawJointIndex = jawJointIndex;
        _headJointIndex = headJointIndex;
        _neckJointIndex = neckJointIndex;
        _meshes = meshes;
        _axisMode = axisMode;
        _rootGroup = rootGroup;
        _timeStart = timeStart;
        _timeEnd = timeEnd;
        _timeCodesPerSecond = timeCodesPerSecond;
        _isAnimated = isAnimated;
        _proceduralMouthOpen = 0;
        _proceduralHeadCompensation = 0;
        _currentTime = timeStart;
    }

    public Model3D Model => _rootGroup;
    public bool IsAnimated => _isAnimated;
    public bool HasProceduralMouthRig => _jawJointIndex >= 0;

    public void SetProceduralMouthRig(double mouthOpen, double headCompensation)
    {
        if (_disposed)
        {
            return;
        }

        _proceduralMouthOpen = Math.Clamp(mouthOpen, 0, 1);
        _proceduralHeadCompensation = Math.Clamp(headCompensation, 0, 1);
    }

    public static bool TryCreate(string usdPath, Material fallbackMaterial, out UsdWpfSkinnedAvatarPlayer? player)
    {
        player = null;
        if (string.IsNullOrWhiteSpace(usdPath) || !File.Exists(usdPath))
        {
            return false;
        }

        if (!UsdWpfMeshLoader.EnsureRuntimeReady())
        {
            return false;
        }

        try
        {
            var stage = UsdStage.Open(usdPath);
            if (stage is null)
            {
                return false;
            }

            var axisMode = UsdWpfMeshLoader.GetAxisConversionMode(stage);
            if (!TryCreateSkelContext(stage, out var skelCache, out var skeletonQuery, out var topology))
            {
                return false;
            }

            var skeletonJointIndexByName = BuildSkeletonJointIndexMap(skeletonQuery);
            var (jawJointIndex, headJointIndex, neckJointIndex) = ResolveProceduralFaceJointIndices(skeletonJointIndexByName);

            var xformCache = new UsdGeomXformCache();
            var rootGroup = new Model3DGroup();
            var bounds = new UsdWpfMeshLoader.BoundsAccumulator();
            var meshes = new List<MeshRuntime>();

            var prims = stage.GetAllPrims();
            for (var i = 0; i < prims.Count; i++)
            {
                var prim = prims[i];
                if (!string.Equals(prim.GetTypeName().ToString(), "Mesh", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryCreateMeshRuntime(
                        usdPath,
                        prim,
                        fallbackMaterial,
                        xformCache,
                        skelCache,
                        skeletonJointIndexByName,
                        axisMode,
                        out var meshRuntime,
                        out var meshBounds))
                {
                    continue;
                }

                meshes.Add(meshRuntime);
                rootGroup.Children.Add(meshRuntime.Model);
                bounds.Include(meshBounds);
            }

            if (meshes.Count == 0 || !bounds.HasValue)
            {
                return false;
            }

            rootGroup.Transform = UsdWpfMeshLoader.BuildAvatarNormalizationTransform(bounds);

            var jointCount = (int)topology.size();
            if (jointCount <= 0)
            {
                return false;
            }

            var jointBindWorld = new VtMatrix4dArray();
            if (!skeletonQuery.GetJointWorldBindTransforms(jointBindWorld) || jointBindWorld.size() == 0)
            {
                jointBindWorld.Dispose();
                return false;
            }

            var jointLocal = new VtMatrix4dArray((uint)jointCount);
            var jointWorld = new VtMatrix4dArray((uint)jointCount);
            var jointSkinXforms = new VtMatrix4dArray((uint)jointCount);
            var id = new GfMatrix4d(1.0);
            for (var i = 0; i < jointCount; i++)
            {
                jointLocal[i] = id;
                jointWorld[i] = id;
                jointSkinXforms[i] = id;
                if (i >= (int)jointBindWorld.size())
                {
                    jointBindWorld.push_back(id);
                }
            }

            var timing = ResolveTiming(stage, skeletonQuery);
            player = new UsdWpfSkinnedAvatarPlayer(
                stage,
                xformCache,
                skelCache,
                skeletonQuery,
                topology,
                jointLocal,
                jointWorld,
                jointBindWorld,
                jointSkinXforms,
                jawJointIndex,
                headJointIndex,
                neckJointIndex,
                meshes,
                axisMode,
                rootGroup,
                timing.start,
                timing.end,
                timing.tps,
                timing.animated);

            player.ApplyPose(new UsdTimeCode(timing.start));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Update(TimeSpan dt)
    {
        if (_disposed)
        {
            return;
        }

        var hasTimeline = _timeEnd > _timeStart + 1e-6;
        if (!_isAnimated && !hasTimeline)
        {
            return;
        }

        var seconds = Math.Max(0.0, dt.TotalSeconds);
        if (seconds <= 0)
        {
            return;
        }

        _currentTime += seconds * _timeCodesPerSecond;
        var duration = _timeEnd - _timeStart;
        if (duration > 1e-6)
        {
            while (_currentTime > _timeEnd)
            {
                _currentTime = _timeStart + ((_currentTime - _timeStart) % duration);
            }
        }
        else
        {
            _currentTime = _timeStart;
        }

        ApplyPose(new UsdTimeCode(_currentTime));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var mesh in _meshes)
        {
            mesh.Dispose();
        }

        _jointLocal.Dispose();
        _jointWorld.Dispose();
        _jointBindWorld.Dispose();
        _jointSkinXforms.Dispose();
        _topology.Dispose();
        _skeletonQuery.Dispose();
        _skelCache.Dispose();
        _xformCache.Dispose();
    }

    private void ApplyPose(UsdTimeCode time)
    {
        try
        {
            _xformCache.SetTime(time);
            if (!_skeletonQuery.ComputeJointLocalTransforms(_jointLocal, time, false)
                && !_skeletonQuery.ComputeJointLocalTransforms(_jointLocal, time))
            {
                return;
            }

            ApplyProceduralMouthRigToJoints();

            var concatOk = false;
            var skelPrim = _skeletonQuery.GetPrim();
            if (skelPrim.IsValid())
            {
                var skeletonRootWorld = _xformCache.GetLocalToWorldTransform(skelPrim);
                concatOk = UsdSkel.UsdSkelConcatJointTransforms(_topology, _jointLocal, _jointWorld, skeletonRootWorld);
            }

            if (!concatOk)
            {
                concatOk = UsdSkel.UsdSkelConcatJointTransforms(_topology, _jointLocal, _jointWorld);
            }

            if (!concatOk)
            {
                return;
            }

            var jointCount = Math.Min((int)_jointWorld.size(), (int)_jointBindWorld.size());
            for (var i = 0; i < jointCount; i++)
            {
                var invBind = _jointBindWorld[i].GetInverse();
                // USD Gf uses row-vector transform conventions; skinning transform composition
                // must be invBind * current, not current * invBind.
                _jointSkinXforms[i] = Multiply(invBind, _jointWorld[i]);
            }

            foreach (var mesh in _meshes)
            {
                mesh.Update(_stage, _xformCache, _jointSkinXforms, _axisMode);
            }
        }
        catch
        {
            // Keep previous pose.
        }
    }

    private void ApplyProceduralMouthRigToJoints()
    {
        var mouthOpen = _proceduralMouthOpen;
        var headComp = _proceduralHeadCompensation;
        if (mouthOpen <= 1e-4 && headComp <= 1e-4)
        {
            return;
        }

        // Very small procedural offsets layered on top of authored animation.
        // This is a fallback "speaking mouth" effect for rigs without blendshape playback.
        if (_jawJointIndex >= 0)
        {
            // Reallusion-style rigs commonly expose a jaw/jawRoot bone.
            var jawPitchDeg = -(1.0 + (8.5 * mouthOpen));
            var jawForward = 0.006 * mouthOpen;
            var jawDown = -0.008 * mouthOpen;
            ApplyLocalDelta(_jawJointIndex, jawPitchDeg, jawDown, jawForward);
        }

        if (_neckJointIndex >= 0)
        {
            var neckPitchDeg = 0.9 * headComp;
            ApplyLocalDelta(_neckJointIndex, neckPitchDeg, 0, 0);
        }

        if (_headJointIndex >= 0)
        {
            var headPitchDeg = 1.6 * headComp;
            ApplyLocalDelta(_headJointIndex, headPitchDeg, 0, 0);
        }
    }

    private void ApplyLocalDelta(int jointIndex, double pitchXDegrees, double translateY, double translateZ)
    {
        if (jointIndex < 0 || jointIndex >= (int)_jointLocal.size())
        {
            return;
        }

        GfMatrix4d delta = new GfMatrix4d(1.0);

        if (Math.Abs(pitchXDegrees) > 1e-6)
        {
            delta = Multiply(delta, CreateRowVectorRotationX(pitchXDegrees));
        }

        if (Math.Abs(translateY) > 1e-6 || Math.Abs(translateZ) > 1e-6)
        {
            delta = Multiply(delta, CreateRowVectorTranslation(0, translateY, translateZ));
        }

        // Row-vector convention: pre-multiply to apply offset in joint local space before authored local->parent transform.
        _jointLocal[jointIndex] = Multiply(delta, _jointLocal[jointIndex]);
    }

    private static bool TryCreateSkelContext(
        UsdStage stage,
        out UsdSkelCache skelCache,
        out UsdSkelSkeletonQuery skeletonQuery,
        out UsdSkelTopology topology)
    {
        skelCache = new UsdSkelCache();
        skeletonQuery = new UsdSkelSkeletonQuery();
        topology = new UsdSkelTopology();

        if (!TryFindSkelRoot(stage, out var skelRoot))
        {
            return false;
        }

        using var predicate = Usd_PrimFlagsPredicate.Tautology();
        if (!skelCache.Populate(skelRoot, predicate))
        {
            return false;
        }

        using var bindings = new UsdSkelBindingVector();
        if (!skelCache.ComputeSkelBindings(skelRoot, bindings, predicate) || bindings.Count == 0)
        {
            return false;
        }

        UsdSkelBinding? selectedBinding = null;
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var query = skelCache.GetSkelQuery(binding.GetSkeleton());
            if (!query.IsValid())
            {
                query.Dispose();
                continue;
            }

            using var targets = binding.GetSkinningTargetsAsVector();
            if (targets.Count == 0)
            {
                query.Dispose();
                continue;
            }

            selectedBinding = binding;
            skeletonQuery.Dispose();
            skeletonQuery = query;
            break;
        }

        if (selectedBinding is null || !skeletonQuery.IsValid())
        {
            return false;
        }

        using var selectedTargets = selectedBinding.GetSkinningTargetsAsVector();
        if (selectedTargets.Count == 0)
        {
            return false;
        }

        topology.Dispose();
        topology = skeletonQuery.GetTopology();
        return topology.size() > 0;
    }

    private static bool TryFindSkelRoot(UsdStage stage, out UsdSkelRoot skelRoot)
    {
        skelRoot = new UsdSkelRoot();
        try
        {
            var root = UsdSkelRoot.Find(stage.GetPseudoRoot());
            if (root.GetPrim().IsValid())
            {
                skelRoot = root;
                return true;
            }

            var prims = stage.GetAllPrims();
            for (var i = 0; i < prims.Count; i++)
            {
                root = UsdSkelRoot.Find(prims[i]);
                if (root.GetPrim().IsValid())
                {
                    skelRoot = root;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryCreateMeshRuntime(
        string usdPath,
        UsdPrim prim,
        Material fallbackMaterial,
        UsdGeomXformCache xformCache,
        UsdSkelCache skelCache,
        Dictionary<string, int> skeletonJointIndexByName,
        UsdWpfMeshLoader.AxisConversionMode axisMode,
        out MeshRuntime meshRuntime,
        out UsdWpfMeshLoader.BoundsAccumulator bounds)
    {
        meshRuntime = null!;
        bounds = new UsdWpfMeshLoader.BoundsAccumulator();

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

            var uvData = UsdWpfMeshLoader.TryReadUvData(prim);
            var worldMatrix = xformCache.GetLocalToWorldTransform(prim);
            var transformed = new Point3D[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                transformed[i] = UsdWpfMeshLoader.ConvertUsdPointToWpf(worldMatrix.TransformAffine(points[i]), axisMode);
            }

            var positions = new List<Point3D>();
            var texCoords = new List<Point>();
            var sourcePointIndexByVertex = new List<int>();
            var triangleIndices = new List<int>();
            var accumNormals = new Vector3D[pointCount];
            var vertexMap = new Dictionary<UsdWpfMeshLoader.VertexKey, int>(pointCount);

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

                for (var j = 1; j < corners - 1; j++)
                {
                    var i0 = faceIndices[faceCursor];
                    var i1 = faceIndices[faceCursor + j];
                    var i2 = faceIndices[faceCursor + j + 1];
                    if (!UsdWpfMeshLoader.IsValidPointIndex(i0, pointCount) || !UsdWpfMeshLoader.IsValidPointIndex(i1, pointCount) || !UsdWpfMeshLoader.IsValidPointIndex(i2, pointCount))
                    {
                        continue;
                    }

                    var p0 = transformed[i0];
                    var p1 = transformed[i1];
                    var p2 = transformed[i2];
                    var faceNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
                    if (faceNormal.LengthSquared < 1e-12)
                    {
                        continue;
                    }

                    faceNormal.Normalize();
                    accumNormals[i0] += faceNormal;
                    accumNormals[i1] += faceNormal;
                    accumNormals[i2] += faceNormal;

                    var v0 = GetOrCreateVertex(i0, faceIndex, faceCursor, 0);
                    var v1 = GetOrCreateVertex(i1, faceIndex, faceCursor, j);
                    var v2 = GetOrCreateVertex(i2, faceIndex, faceCursor, j + 1);
                    triangleIndices.Add(v0); triangleIndices.Add(v1); triangleIndices.Add(v2);
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
                var n = accumNormals[sourcePointIndexByVertex[i]];
                if (n.LengthSquared < 1e-12) n = new Vector3D(0, 1, 0); else n.Normalize();
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

            var material = UsdWpfMeshLoader.TryCreateTexturedMaterialForExternalMesh(usdPath, prim, uvData.HasValue) ?? fallbackMaterial;
            var model = new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material };

            using var skinQuery = skelCache.GetSkinningQuery(prim);
            meshRuntime = MeshRuntime.Create(
                prim,
                mesh,
                model,
                sourcePointIndexByVertex.ToArray(),
                points,
                skinQuery,
                skeletonJointIndexByName);
            return true;

            int GetOrCreateVertex(int sourcePointIndex, int currentFaceIndex, int currentFaceCursor, int faceCornerOffset)
            {
                var uvKey = -1;
                var uvPoint = default(Point);
                if (uvData.HasValue && UsdWpfMeshLoader.TryResolveUvForCorner(uvData.Value, sourcePointIndex, currentFaceIndex, currentFaceCursor, faceCornerOffset, out uvKey, out uvPoint))
                {
                }
                else
                {
                    uvKey = -1;
                }

                var key = new UsdWpfMeshLoader.VertexKey(sourcePointIndex, uvKey);
                if (vertexMap.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var vertexIndex = positions.Count;
                vertexMap[key] = vertexIndex;
                positions.Add(transformed[sourcePointIndex]);
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

    private static (double start, double end, double tps, bool animated) ResolveTiming(UsdStage stage, UsdSkelSkeletonQuery skeletonQuery)
    {
        var tps = stage.GetTimeCodesPerSecond();
        if (tps <= 0) tps = stage.GetFramesPerSecond();
        if (tps <= 0) tps = 24;

        var start = stage.GetStartTimeCode();
        var end = stage.GetEndTimeCode();
        var animated = end > start + 1e-6;
        try
        {
            using var animQuery = skeletonQuery.GetAnimQuery();
            if (animQuery.IsValid())
            {
                using var samples = new StdDoubleVector();
                if (animQuery.GetJointTransformTimeSamples(samples) && samples.Count > 0)
                {
                    start = samples[0];
                    end = samples[0];
                    for (var i = 1; i < samples.Count; i++)
                    {
                        start = Math.Min(start, samples[i]);
                        end = Math.Max(end, samples[i]);
                    }
                }

                // Some composed clip files report no explicit time-varying joint attrs here,
                // while stage start/end time still defines a valid animation range.
                animated = animQuery.JointTransformsMightBeTimeVarying() || (end > start + 1e-6);
            }
        }
        catch
        {
        }

        if (end < start)
        {
            (start, end) = (end, start);
        }

        // Some USD clip-composed assets do not expose a reliable authored start/end range
        // through the queried metadata, but still animate when sampled over time.
        if (end <= start + 1e-6)
        {
            end = start + Math.Max(1.0, tps * 3.0);
        }

        animated = animated || ProbeJointMotion(skeletonQuery, start, Math.Min(end, start + 1.0));

        return (start, end, tps, animated);
    }

    private static Dictionary<string, int> BuildSkeletonJointIndexMap(UsdSkelSkeletonQuery skeletonQuery)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var jointOrder = skeletonQuery.GetJointOrder();
            for (var i = 0; i < (int)jointOrder.size(); i++)
            {
                var name = jointOrder[i].ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!map.ContainsKey(name))
                {
                    map.Add(name, i);
                }
            }
        }
        catch
        {
            // Best effort: empty map falls back to skeleton order as-is.
        }

        return map;
    }

    private static bool ProbeJointMotion(UsdSkelSkeletonQuery skeletonQuery, double t0, double t1)
    {
        if (Math.Abs(t1 - t0) <= 1e-6)
        {
            return false;
        }

        try
        {
            using var a = new VtMatrix4dArray();
            using var b = new VtMatrix4dArray();

            var okA = skeletonQuery.ComputeJointLocalTransforms(a, new UsdTimeCode(t0), false)
                || skeletonQuery.ComputeJointLocalTransforms(a, new UsdTimeCode(t0));
            var okB = skeletonQuery.ComputeJointLocalTransforms(b, new UsdTimeCode(t1), false)
                || skeletonQuery.ComputeJointLocalTransforms(b, new UsdTimeCode(t1));
            if (!okA || !okB || a.size() == 0 || b.size() == 0 || a.size() != b.size())
            {
                return false;
            }

            var count = (int)Math.Min(a.size(), 4);
            var tmpA = new double[16];
            var tmpB = new double[16];
            for (var i = 0; i < count; i++)
            {
                a[i].CopyToArray(tmpA);
                b[i].CopyToArray(tmpB);
                for (var k = 0; k < 16; k++)
                {
                    if (Math.Abs(tmpA[k] - tmpB[k]) > 1e-6)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static (int jaw, int head, int neck) ResolveProceduralFaceJointIndices(Dictionary<string, int> skeletonJointIndexByName)
    {
        if (skeletonJointIndexByName.Count == 0)
        {
            return (-1, -1, -1);
        }

        var jaw = FindBestJointIndex(
            skeletonJointIndexByName,
            requiredTokens: new[] { "jaw" },
            preferredTokens: new[] { "jawroot", "jaw_root", "cc_base_jaw" },
            excludedTokens: new[] { "teeth", "tongue" });

        var head = FindBestJointIndex(
            skeletonJointIndexByName,
            requiredTokens: new[] { "head" },
            preferredTokens: new[] { "cc_base_head", "/head" },
            excludedTokens: new[] { "headend", "head_end", "lookat", "look_at" });

        var neck = FindBestJointIndex(
            skeletonJointIndexByName,
            requiredTokens: new[] { "neck" },
            preferredTokens: new[] { "cc_base_neck", "neck01", "neck_01" },
            excludedTokens: new[] { "twist02", "twist2" });

        if (neck == head)
        {
            neck = -1;
        }

        return (jaw, head, neck);
    }

    private static int FindBestJointIndex(
        Dictionary<string, int> skeletonJointIndexByName,
        IEnumerable<string> requiredTokens,
        IEnumerable<string>? preferredTokens = null,
        IEnumerable<string>? excludedTokens = null)
    {
        var required = requiredTokens.Select(t => t.ToLowerInvariant()).ToArray();
        var preferred = (preferredTokens ?? Array.Empty<string>()).Select(t => t.ToLowerInvariant()).ToArray();
        var excluded = (excludedTokens ?? Array.Empty<string>()).Select(t => t.ToLowerInvariant()).ToArray();

        var bestIndex = -1;
        var bestScore = int.MinValue;
        foreach (var kvp in skeletonJointIndexByName)
        {
            var normalized = kvp.Key.Replace('\\', '/').ToLowerInvariant();
            if (required.Any() && required.Any(t => !normalized.Contains(t, StringComparison.Ordinal)))
            {
                continue;
            }

            if (excluded.Any(t => normalized.Contains(t, StringComparison.Ordinal)))
            {
                continue;
            }

            var nameOnly = normalized.Split('/').LastOrDefault() ?? normalized;
            var score = 0;
            score += 200 * preferred.Count(t => normalized.Contains(t, StringComparison.Ordinal));
            score += 60 * required.Count(t => normalized.Contains(t, StringComparison.Ordinal));

            if (preferred.Any(t => nameOnly.Equals(t, StringComparison.Ordinal) || nameOnly.EndsWith(t, StringComparison.Ordinal)))
            {
                score += 220;
            }

            if (nameOnly.Contains("twist", StringComparison.Ordinal))
            {
                score -= 80;
            }

            score -= nameOnly.Length;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = kvp.Value;
            }
        }

        return bestIndex;
    }

    private static GfMatrix4d CreateRowVectorRotationX(double degrees)
    {
        var radians = degrees * (Math.PI / 180.0);
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);

        // Row-vector form (transpose of the common column-vector Rx).
        return new GfMatrix4d().Set(
            1, 0, 0, 0,
            0, c, s, 0,
            0, -s, c, 0,
            0, 0, 0, 1);
    }

    private static GfMatrix4d CreateRowVectorTranslation(double x, double y, double z)
    {
        // Row-vector homogeneous translation keeps offsets in the last row.
        return new GfMatrix4d().Set(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            x, y, z, 1);
    }

    private static GfMatrix4d Multiply(GfMatrix4d a, GfMatrix4d b)
    {
        var am = new double[16];
        var bm = new double[16];
        a.CopyToArray(am);
        b.CopyToArray(bm);

        var rm = new double[16];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++)
                {
                    sum += am[(r * 4) + k] * bm[(k * 4) + c];
                }
                rm[(r * 4) + c] = sum;
            }
        }

        return new GfMatrix4d().Set(
            rm[0], rm[1], rm[2], rm[3],
            rm[4], rm[5], rm[6], rm[7],
            rm[8], rm[9], rm[10], rm[11],
            rm[12], rm[13], rm[14], rm[15]);
    }

    private sealed class MeshRuntime : IDisposable
    {
        private readonly string _primPath;
        private readonly int[] _sourcePointIndexByVertex;
        private readonly VtVec3fArray _basePoints;
        private readonly VtVec3fArray _workingPoints;
        private readonly VtIntArray? _jointIndices;
        private readonly VtFloatArray? _jointWeights;
        private readonly int _numInfluencesPerComponent;
        private readonly GfMatrix4d _geomBindTransform;
        private readonly TfToken _skinningMethod;
        private readonly int[]? _jointXformRemap;
        private readonly VtMatrix4dArray? _remappedJointSkinXforms;
        private Vector3D[]? _normalScratch;

        private MeshRuntime(
            string primPath,
            MeshGeometry3D meshGeometry,
            GeometryModel3D model,
            int[] sourcePointIndexByVertex,
            VtVec3fArray basePoints,
            VtVec3fArray workingPoints,
            VtIntArray? jointIndices,
            VtFloatArray? jointWeights,
            int numInfluencesPerComponent,
            GfMatrix4d geomBindTransform,
            TfToken skinningMethod,
            int[]? jointXformRemap,
            VtMatrix4dArray? remappedJointSkinXforms)
        {
            _primPath = primPath;
            MeshGeometry = meshGeometry;
            Model = model;
            _sourcePointIndexByVertex = sourcePointIndexByVertex;
            _basePoints = basePoints;
            _workingPoints = workingPoints;
            _jointIndices = jointIndices;
            _jointWeights = jointWeights;
            _numInfluencesPerComponent = numInfluencesPerComponent;
            _geomBindTransform = geomBindTransform;
            _skinningMethod = skinningMethod;
            _jointXformRemap = jointXformRemap;
            _remappedJointSkinXforms = remappedJointSkinXforms;
        }

        public MeshGeometry3D MeshGeometry { get; }
        public GeometryModel3D Model { get; }

        public static MeshRuntime Create(
            UsdPrim prim,
            MeshGeometry3D meshGeometry,
            GeometryModel3D model,
            int[] sourcePointIndexByVertex,
            VtVec3fArray basePoints,
            UsdSkelSkinningQuery? skinQuery,
            Dictionary<string, int> skeletonJointIndexByName)
        {
            var workingPoints = new VtVec3fArray(basePoints);
            VtIntArray? jointIndices = null;
            VtFloatArray? jointWeights = null;
            var numInfluences = 0;
            var geomBind = new GfMatrix4d(1.0);
            var skinningMethod = new TfToken("classicLinear");
            int[]? jointXformRemap = null;
            VtMatrix4dArray? remappedJointSkinXforms = null;

            if (skinQuery is not null && skinQuery.IsValid())
            {
                try
                {
                    var indices = new VtIntArray();
                    var weights = new VtFloatArray();
                    var pointCount = (uint)Math.Max(0, (int)basePoints.size());
                    if (pointCount > 0
                        && skinQuery.ComputeVaryingJointInfluences(pointCount, indices, weights)
                        && indices.size() > 0
                        && weights.size() > 0)
                    {
                        jointIndices = indices;
                        jointWeights = weights;
                        numInfluences = Math.Max(1, skinQuery.GetNumInfluencesPerComponent());
                        geomBind = skinQuery.GetGeomBindTransform();
                        var methodToken = skinQuery.GetSkinningMethod();
                        if (methodToken is not null && !string.IsNullOrWhiteSpace(methodToken.ToString()))
                        {
                            skinningMethod = methodToken;
                        }
                        var expectedCount = pointCount * (uint)numInfluences;
                        if ((uint)jointIndices.size() < expectedCount || (uint)jointWeights.size() < expectedCount)
                        {
                            jointIndices.Dispose();
                            jointWeights.Dispose();
                            jointIndices = null;
                            jointWeights = null;
                            numInfluences = 0;
                        }
                        else
                        {
                            TryBuildJointRemap(
                                skinQuery,
                                skeletonJointIndexByName,
                                out jointXformRemap,
                                out remappedJointSkinXforms);
                        }
                    }
                    else
                    {
                        indices.Dispose();
                        weights.Dispose();
                    }
                }
                catch
                {
                }
            }

            return new MeshRuntime(
                prim.GetPath().GetString(),
                meshGeometry,
                model,
                sourcePointIndexByVertex,
                basePoints,
                workingPoints,
                jointIndices,
                jointWeights,
                numInfluences,
                geomBind,
                skinningMethod,
                jointXformRemap,
                remappedJointSkinXforms);
        }

        public void Update(UsdStage stage, UsdGeomXformCache xformCache, VtMatrix4dArray jointSkinXforms, UsdWpfMeshLoader.AxisConversionMode axisMode)
        {
            var pointCount = (int)_basePoints.size();
            for (var i = 0; i < pointCount; i++)
            {
                _workingPoints[i] = _basePoints[i];
            }

            if (_jointIndices is not null && _jointWeights is not null && _numInfluencesPerComponent > 0)
            {
                var xformsForMesh = jointSkinXforms;
                if (_jointXformRemap is not null && _remappedJointSkinXforms is not null)
                {
                    var srcCount = (int)jointSkinXforms.size();
                    var identity = new GfMatrix4d(1.0);
                    for (var i = 0; i < _jointXformRemap.Length; i++)
                    {
                        var srcIndex = _jointXformRemap[i];
                        _remappedJointSkinXforms[i] =
                            srcIndex >= 0 && srcIndex < srcCount
                                ? jointSkinXforms[srcIndex]
                                : identity;
                    }

                    xformsForMesh = _remappedJointSkinXforms;
                }

                _ = UsdSkel.UsdSkelSkinPoints(_skinningMethod, _geomBindTransform, xformsForMesh, _jointIndices, _jointWeights, _numInfluencesPerComponent, _workingPoints);
            }

            if (string.IsNullOrWhiteSpace(_primPath))
            {
                return;
            }

            var prim = stage.GetPrimAtPath(new SdfPath(_primPath));
            if (!prim.IsValid())
            {
                return;
            }

            var world = xformCache.GetLocalToWorldTransform(prim);
            var positions = MeshGeometry.Positions;
            if (positions is null || positions.Count != _sourcePointIndexByVertex.Length)
            {
                return;
            }

            for (var vertexIndex = 0; vertexIndex < _sourcePointIndexByVertex.Length; vertexIndex++)
            {
                var sourceIndex = _sourcePointIndexByVertex[vertexIndex];
                if (sourceIndex < 0 || sourceIndex >= pointCount)
                {
                    continue;
                }

                var p = world.TransformAffine(_workingPoints[sourceIndex]);
                positions[vertexIndex] = UsdWpfMeshLoader.ConvertUsdPointToWpf(new GfVec3f((float)p[0], (float)p[1], (float)p[2]), axisMode);
            }

            RecomputeSmoothNormals();
        }

        public void Dispose()
        {
            _basePoints.Dispose();
            _workingPoints.Dispose();
            _jointIndices?.Dispose();
            _jointWeights?.Dispose();
            _remappedJointSkinXforms?.Dispose();
        }

        private static void TryBuildJointRemap(
            UsdSkelSkinningQuery skinQuery,
            Dictionary<string, int> skeletonJointIndexByName,
            out int[]? jointXformRemap,
            out VtMatrix4dArray? remappedJointSkinXforms)
        {
            jointXformRemap = null;
            remappedJointSkinXforms = null;

            if (skeletonJointIndexByName.Count == 0)
            {
                return;
            }

            try
            {
                using var meshJointOrder = new VtTokenArray();
                if (!skinQuery.GetJointOrder(meshJointOrder) || meshJointOrder.size() == 0)
                {
                    return;
                }

                var count = (int)meshJointOrder.size();
                var remap = new int[count];
                var anyDifference = false;
                for (var i = 0; i < count; i++)
                {
                    var jointName = meshJointOrder[i].ToString();
                    if (string.IsNullOrWhiteSpace(jointName) || !skeletonJointIndexByName.TryGetValue(jointName, out var skelIndex))
                    {
                        return;
                    }

                    remap[i] = skelIndex;
                    if (skelIndex != i)
                    {
                        anyDifference = true;
                    }
                }

                if (!anyDifference)
                {
                    return;
                }

                jointXformRemap = remap;
                remappedJointSkinXforms = new VtMatrix4dArray((uint)count);
                var identity = new GfMatrix4d(1.0);
                for (var i = 0; i < count; i++)
                {
                    remappedJointSkinXforms[i] = identity;
                }
            }
            catch
            {
                jointXformRemap = null;
                remappedJointSkinXforms?.Dispose();
                remappedJointSkinXforms = null;
            }
        }

        private void RecomputeSmoothNormals()
        {
            var positions = MeshGeometry.Positions;
            var triangles = MeshGeometry.TriangleIndices;
            if (positions is null || triangles is null || positions.Count == 0 || triangles.Count < 3)
            {
                return;
            }

            var vertexCount = positions.Count;
            var scratch = _normalScratch;
            if (scratch is null || scratch.Length != vertexCount)
            {
                scratch = new Vector3D[vertexCount];
                _normalScratch = scratch;
            }
            else
            {
                Array.Clear(scratch, 0, scratch.Length);
            }

            for (var i = 0; i + 2 < triangles.Count; i += 3)
            {
                var i0 = triangles[i];
                var i1 = triangles[i + 1];
                var i2 = triangles[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                {
                    continue;
                }

                var p0 = positions[i0];
                var p1 = positions[i1];
                var p2 = positions[i2];
                var faceNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
                if (faceNormal.LengthSquared < 1e-12)
                {
                    continue;
                }

                scratch[i0] += faceNormal;
                scratch[i1] += faceNormal;
                scratch[i2] += faceNormal;
            }

            var normals = MeshGeometry.Normals;
            if (normals is null || normals.Count != vertexCount)
            {
                normals = new Vector3DCollection(vertexCount);
                for (var i = 0; i < vertexCount; i++)
                {
                    var n = scratch[i];
                    if (n.LengthSquared < 1e-12)
                    {
                        n = new Vector3D(0, 1, 0);
                    }
                    else
                    {
                        n.Normalize();
                    }

                    normals.Add(n);
                }

                MeshGeometry.Normals = normals;
                return;
            }

            for (var i = 0; i < vertexCount; i++)
            {
                var n = scratch[i];
                if (n.LengthSquared < 1e-12)
                {
                    n = new Vector3D(0, 1, 0);
                }
                else
                {
                    n.Normalize();
                }

                normals[i] = n;
            }
        }
    }
}
