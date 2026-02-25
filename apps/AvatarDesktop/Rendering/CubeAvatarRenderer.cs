using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.IO;

namespace AvatarDesktop.Rendering;

public sealed class CubeAvatarRenderer : IAvatarRenderer
{
    private readonly Viewport3D _viewport;
    private readonly PerspectiveCamera _camera;
    private readonly bool _showGroundPlane;
    private readonly AxisAngleRotation3D _rotationY;
    private readonly AxisAngleRotation3D _rotationX;
    private readonly RotateTransform3D _rotationXTransform;
    private readonly RotateTransform3D _rotationYTransform;
    private readonly AmbientLight _ambientLight;
    private readonly DirectionalLight _keyLight;
    private readonly DirectionalLight _fillLight;
    private readonly DirectionalLight _bounceLight;
    private readonly DirectionalLight _rimLight;
    private readonly PointLight _cameraFillLight;
    private readonly DiffuseMaterial _cubeMaterial;
    private readonly GeometryModel3D _cubeModel;
    private readonly Model3DGroup _avatarModelGroup;
    private Vector3D _cameraUpDirection = new(0, 1, 0);

    private double _ySpeedDegPerSec = 18;
    private double _xTargetDeg = 0;
    private double _pulse;
    private string _currentAnimation = "idle";
    private string _usdPath = string.Empty;
    private string _baseUsdPath = string.Empty;
    private UsdWpfSkinnedAvatarPlayer? _usdAnimatedPlayer;
    private Model3D? _currentUsdModel;
    private bool _isUsdModelLoaded;
    private double _usdPitchBaseDeg;
    private bool _hasFaceOrbitPivot;
    private Point3D _faceOrbitPivot;
    private bool _isViewportDragging;
    private Point _lastViewportPointer;
    private readonly Dictionary<string, double> _blendshapes = new(StringComparer.OrdinalIgnoreCase);

    public CubeAvatarRenderer(bool showGroundPlane = true)
    {
        _showGroundPlane = showGroundPlane;
        _viewport = new Viewport3D
        {
            ClipToBounds = true,
        };
        RenderOptions.SetEdgeMode(_viewport, EdgeMode.Unspecified);
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.HighQuality);
        _viewport.SizeChanged += (_, _) => ReframeLoadedUsdCamera();
        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        _viewport.MouseLeave += Viewport_MouseLeave;

        _camera = new PerspectiveCamera();
        ApplyCubeCameraPreset();
        _viewport.Camera = _camera;

        var root = new Model3DGroup();
        // WPF has no real GI, so use a stylized dramatic key/fill/rim stack.
        _ambientLight = new AmbientLight(Color.FromRgb(64, 70, 80));
        _keyLight = new DirectionalLight(Color.FromRgb(248, 228, 206), new Vector3D(-0.55, -0.28, -1.0));
        _fillLight = new DirectionalLight(Color.FromRgb(118, 148, 215), new Vector3D(0.52, -0.12, -0.45));
        _bounceLight = new DirectionalLight(Color.FromRgb(118, 94, 76), new Vector3D(0.18, 1.0, 0.15));
        _rimLight = new DirectionalLight(Color.FromRgb(126, 214, 248), new Vector3D(0.22, -0.08, 1.0));
        _cameraFillLight = new PointLight(Color.FromRgb(76, 84, 98), new Point3D(0.0, 1.9, 2.4));
        root.Children.Add(_ambientLight);
        root.Children.Add(_keyLight);
        root.Children.Add(_fillLight);
        root.Children.Add(_bounceLight);
        root.Children.Add(_rimLight);
        root.Children.Add(_cameraFillLight);

        _cubeMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(100, 175, 220)));
        _cubeModel = new GeometryModel3D
        {
            Geometry = BuildCubeMesh(),
            Material = _cubeMaterial,
            BackMaterial = _cubeMaterial,
        };

        _rotationY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 15);
        _rotationX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        _rotationXTransform = new RotateTransform3D(_rotationX);
        _rotationYTransform = new RotateTransform3D(_rotationY);
        var transformGroup = new Transform3DGroup();
        transformGroup.Children.Add(_rotationXTransform);
        transformGroup.Children.Add(_rotationYTransform);

        _avatarModelGroup = new Model3DGroup
        {
            Transform = transformGroup,
        };
        _avatarModelGroup.Children.Add(_cubeModel);
        SetRotationPivot(new Point3D(0, 0, 0));

        root.Children.Add(_avatarModelGroup);
        if (showGroundPlane)
        {
            root.Children.Add(BuildGroundPlane());
        }

        UpdateDramaticLighting();
        _viewport.Children.Add(new ModelVisual3D { Content = root });
    }

    public UIElement View => _viewport;

    public void LoadUsd(string path)
    {
        _usdAnimatedPlayer?.Dispose();
        _usdAnimatedPlayer = null;
        _currentUsdModel = null;
        _isUsdModelLoaded = false;
        _usdPitchBaseDeg = 0;
        _hasFaceOrbitPivot = false;

        _baseUsdPath = path ?? string.Empty;
        var pathToLoad = ResolveUsdPathForCurrentAnimation(_baseUsdPath, _currentAnimation);
        _usdPath = pathToLoad;

        if (!string.IsNullOrWhiteSpace(pathToLoad)
            && File.Exists(pathToLoad)
            && UsdWpfSkinnedAvatarPlayer.TryCreate(pathToLoad, _cubeMaterial, out var animatedPlayer))
        {
            _usdAnimatedPlayer = animatedPlayer!;
            _isUsdModelLoaded = true;
            _rotationY.Angle = 0;
            _rotationX.Angle = 0;
            _avatarModelGroup.Children.Clear();
            _currentUsdModel = _usdAnimatedPlayer.Model;
            _avatarModelGroup.Children.Add(_currentUsdModel);
            ApplyDefaultUsdOrientation(pathToLoad);
            ApplyUsdCameraPreset(pathToLoad, _currentUsdModel);
        }
        else if (!string.IsNullOrWhiteSpace(pathToLoad)
            && File.Exists(pathToLoad)
            && UsdWpfMeshLoader.TryCreateModel(pathToLoad, _cubeMaterial, out var usdModel))
        {
            _isUsdModelLoaded = true;
            _rotationY.Angle = 0;
            _rotationX.Angle = 0;
            _avatarModelGroup.Children.Clear();
            _currentUsdModel = usdModel;
            _avatarModelGroup.Children.Add(_currentUsdModel);
            ApplyDefaultUsdOrientation(pathToLoad);
            ApplyUsdCameraPreset(pathToLoad, _currentUsdModel);
        }
        else
        {
            _currentUsdModel = null;
            _isUsdModelLoaded = false;
            _usdPitchBaseDeg = 0;
            _avatarModelGroup.Children.Clear();
            _avatarModelGroup.Children.Add(_cubeModel);
            _hasFaceOrbitPivot = false;
            SetRotationPivot(new Point3D(0, 0, 0));
            ApplyCubeCameraPreset();
        }

        SetAnimation("idle");
        UpdateColorFromMood();
        UpdateDramaticLighting();
    }

    public void SetAnimation(string name)
    {
        _currentAnimation = string.IsNullOrWhiteSpace(name) ? "idle" : name.ToLowerInvariant();

        switch (_currentAnimation)
        {
            case "idle":
                _ySpeedDegPerSec = 18;
                _xTargetDeg = 0;
                break;
            case "listening":
                _ySpeedDegPerSec = 28;
                _xTargetDeg = -8;
                break;
            case "think":
                _ySpeedDegPerSec = 42;
                _xTargetDeg = 10;
                break;
            case "speaking":
                _ySpeedDegPerSec = 75;
                _xTargetDeg = 0;
                break;
            case "wave":
                _ySpeedDegPerSec = 140;
                _xTargetDeg = 15;
                break;
            case "dance_01":
                _ySpeedDegPerSec = 260;
                _xTargetDeg = 22;
                break;
            case "nod":
                _ySpeedDegPerSec = 55;
                _xTargetDeg = 25;
                break;
            case "shrug":
                _ySpeedDegPerSec = 95;
                _xTargetDeg = -15;
                break;
            default:
                _ySpeedDegPerSec = 22;
                _xTargetDeg = 0;
                break;
        }

        var clipUsdPath = ResolveUsdPathForCurrentAnimation(_baseUsdPath, _currentAnimation);
        if (!string.IsNullOrWhiteSpace(clipUsdPath)
            && !string.Equals(clipUsdPath, _usdPath, StringComparison.OrdinalIgnoreCase))
        {
            LoadUsd(_baseUsdPath);
        }
    }

    public void SetBlendshape(string name, double value)
    {
        _blendshapes[name] = Math.Clamp(value, 0, 1);
        UpdateColorFromMood();
    }

    public void Update(TimeSpan dt)
    {
        var seconds = Math.Max(0.0001, dt.TotalSeconds);
        var nextPulse = _pulse + seconds;

        if (_isUsdModelLoaded)
        {
            if (_usdAnimatedPlayer is not null)
            {
                var speaking = string.Equals(_currentAnimation, "speaking", StringComparison.OrdinalIgnoreCase);
                var mouthRhythm = (Math.Sin(nextPulse * 18.0) + 1.0) * 0.5;
                var mouthOpen = speaking ? (0.18 + (0.82 * mouthRhythm)) : 0.0;
                var headCompensation = speaking ? (0.10 + (0.25 * mouthRhythm)) : 0.0;
                _usdAnimatedPlayer.SetProceduralMouthRig(mouthOpen, headCompensation);
            }
            _usdAnimatedPlayer?.Update(dt);
        }
        else
        {
            _rotationY.Angle = (_rotationY.Angle + (_ySpeedDegPerSec * seconds)) % 360;

            var xDelta = _xTargetDeg - _rotationX.Angle;
            _rotationX.Angle += xDelta * Math.Min(1.0, seconds * 8.0);
        }

        _pulse = nextPulse;
        UpdateColorFromMood();
        UpdateDramaticLighting();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isViewportDragging = true;
        _lastViewportPointer = e.GetPosition(_viewport);
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isViewportDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(_viewport);
        var dx = current.X - _lastViewportPointer.X;
        var dy = current.Y - _lastViewportPointer.Y;
        _lastViewportPointer = current;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
        {
            return;
        }

        const double yawSpeed = 0.30;
        const double pitchSpeed = 0.25;
        var pitchBase = _isUsdModelLoaded ? _usdPitchBaseDeg : 0.0;

        _rotationY.Angle = NormalizeDegrees(_rotationY.Angle + (dx * yawSpeed));
        _rotationX.Angle = Math.Clamp(_rotationX.Angle - (dy * pitchSpeed), pitchBase - 75.0, pitchBase + 75.0);
        UpdateDramaticLighting();
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        EndViewportDrag();
        e.Handled = true;
    }

    private void Viewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isViewportDragging && e.LeftButton != MouseButtonState.Pressed)
        {
            EndViewportDrag();
        }
    }

    private void EndViewportDrag()
    {
        _isViewportDragging = false;
        if (_viewport.IsMouseCaptured)
        {
            _viewport.ReleaseMouseCapture();
        }
    }

    private void UpdateColorFromMood()
    {
        var smile = GetBlendshape("smile");
        var frown = GetBlendshape("frown");
        var brow = GetBlendshape("brow_raise");
        var anger = GetBlendshape("anger");

        var baseR = 100.0 + smile * 40 - frown * 15 + anger * 55;
        var baseG = 170.0 + smile * 35 - anger * 70;
        var baseB = 220.0 + brow * 30 - anger * 40 - frown * 20;

        var animBoost = _currentAnimation switch
        {
            "dance_01" => 30,
            "wave" => 18,
            "speaking" => 10,
            _ => 0
        };

        var pulse = (Math.Sin(_pulse * (_currentAnimation == "dance_01" ? 8 : 4)) + 1) * 0.5;
        var boost = animBoost * pulse;

        var color = Color.FromRgb(
            ClampToByte(baseR + boost),
            ClampToByte(baseG + (boost * 0.5)),
            ClampToByte(baseB + (boost * 0.8)));

        if (_cubeMaterial.Brush is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private double GetBlendshape(string key)
    {
        return _blendshapes.TryGetValue(key, out var value) ? value : 0;
    }

    private static byte ClampToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static GeometryModel3D BuildGroundPlane()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new Point3D(-3.5, -1.2, -3.5),
                new Point3D( 3.5, -1.2, -3.5),
                new Point3D( 3.5, -1.2,  3.5),
                new Point3D(-3.5, -1.2,  3.5),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };

        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(38, 42, 48)));
        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material,
        };
    }

    private static MeshGeometry3D BuildCubeMesh()
    {
        var mesh = new MeshGeometry3D();

        var p0 = new Point3D(-1, -1, 1);
        var p1 = new Point3D(1, -1, 1);
        var p2 = new Point3D(1, 1, 1);
        var p3 = new Point3D(-1, 1, 1);
        var p4 = new Point3D(-1, -1, -1);
        var p5 = new Point3D(1, -1, -1);
        var p6 = new Point3D(1, 1, -1);
        var p7 = new Point3D(-1, 1, -1);

        AddFace(mesh, p0, p1, p2, p3); // front
        AddFace(mesh, p5, p4, p7, p6); // back
        AddFace(mesh, p4, p0, p3, p7); // left
        AddFace(mesh, p1, p5, p6, p2); // right
        AddFace(mesh, p3, p2, p6, p7); // top
        AddFace(mesh, p4, p5, p1, p0); // bottom

        return mesh;
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.Positions.Add(d);

        mesh.TriangleIndices.Add(start + 0);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 0);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 3);
    }

    private void ApplyUsdCameraPreset(string usdPath, Model3D usdModel)
    {
        var faceCloseup = ShouldUseFaceCloseupCamera(usdPath);
        if (TryApplyUsdAutoFramedCamera(usdModel, faceCloseup))
        {
            return;
        }

        if (_hasFaceOrbitPivot)
        {
            SetRotationPivot(_faceOrbitPivot);
        }

        if (faceCloseup)
        {
            ApplyFaceCloseupCameraPreset();
            return;
        }

        ApplyPortraitCameraPreset();
    }

    private void ReframeLoadedUsdCamera()
    {
        if (!_isUsdModelLoaded || _currentUsdModel is null || string.IsNullOrWhiteSpace(_usdPath))
        {
            return;
        }

        ApplyUsdCameraPreset(_usdPath, _currentUsdModel);
    }

    private void ApplyCubeCameraPreset()
    {
        ConfigureOrbitAxes(useZAsUp: false);
        SetRotationPivot(new Point3D(0, 0, 0));
        if (_showGroundPlane)
        {
            SetCamera(
                position: new Point3D(0, 1.6, 4.2),
                lookDirection: new Vector3D(0, -0.3, -4.2),
                fieldOfView: 55);
            return;
        }

        SetCamera(
            position: new Point3D(0, 0.0, 4.0),
            lookDirection: new Vector3D(0, 0.0, -4.0),
            fieldOfView: 52);
    }

    private void ApplyPortraitCameraPreset()
    {
        ConfigureOrbitAxes(useZAsUp: false);
        if (_showGroundPlane)
        {
            // Portrait framing: prioritize head/upper torso for full-body normalized avatars.
            SetCamera(
                position: new Point3D(0, 1.35, 2.55),
                lookDirection: new Vector3D(0, -0.55, -2.55),
                fieldOfView: 34);
            return;
        }

        SetCamera(
            position: new Point3D(0, 1.25, 2.2),
            lookDirection: new Vector3D(0, -0.45, -2.2),
            fieldOfView: 32);
    }

    private void ApplyFaceCloseupCameraPreset()
    {
        ConfigureOrbitAxes(useZAsUp: false);
        if (_showGroundPlane)
        {
            SetCamera(
                position: new Point3D(0, 0.9, 2.05),
                lookDirection: new Vector3D(0, -0.33, -2.05),
                fieldOfView: 35);
            return;
        }

        SetCamera(
            position: new Point3D(0, 0.75, 1.8),
            lookDirection: new Vector3D(0, -0.22, -1.8),
            fieldOfView: 34);
    }

    private bool TryApplyUsdAutoFramedCamera(Model3D usdModel, bool faceCloseup)
    {
        var bounds = usdModel.Bounds;
        if (bounds.IsEmpty || !IsFiniteBounds(bounds))
        {
            return false;
        }

        var worldBounds = _avatarModelGroup.Transform?.TransformBounds(bounds) ?? bounds;
        if (worldBounds.IsEmpty || !IsFiniteBounds(worldBounds))
        {
            worldBounds = bounds;
        }

        // USD points are already converted into WPF Y-up space by the loaders.
        var useZAsUp = false;
        ConfigureOrbitAxes(useZAsUp);

        var width = _viewport.ActualWidth;
        var height = _viewport.ActualHeight;
        var aspect = (width > 1.0 && height > 1.0) ? (width / height) : (_showGroundPlane ? 1.55 : 1.0);
        aspect = Math.Clamp(aspect, 0.6, 3.2);

        var fov = faceCloseup
            ? (_showGroundPlane ? 35.0 : 33.0)
            : (_showGroundPlane ? 35.0 : 33.0);

        var centerX = bounds.X + (bounds.SizeX * 0.5);
        var centerY = bounds.Y + (bounds.SizeY * 0.5);
        var centerZ = bounds.Z + (bounds.SizeZ * 0.5);
        var faceVerticalBias = faceCloseup ? 0.72 : 0.56;

        var localTarget = useZAsUp
            ? new Point3D(centerX, centerY, bounds.Z + (bounds.SizeZ * faceVerticalBias))
            : new Point3D(centerX, bounds.Y + (bounds.SizeY * faceVerticalBias), centerZ);
        _faceOrbitPivot = localTarget;
        _hasFaceOrbitPivot = true;
        SetRotationPivot(localTarget);

        var target = _avatarModelGroup.Transform?.Transform(localTarget) ?? localTarget;

        var frameWidth = faceCloseup
            ? Math.Max(0.05, Math.Min(worldBounds.SizeX * 0.72, (useZAsUp ? worldBounds.SizeZ : worldBounds.SizeY) * 0.52)) * 1.10
            : Math.Max(0.05, worldBounds.SizeX) * 1.18;
        var frameHeight = faceCloseup
            ? Math.Max(0.05, (useZAsUp ? worldBounds.SizeZ : worldBounds.SizeY) * 0.52) * 1.08
            : Math.Max(0.05, useZAsUp ? worldBounds.SizeZ : worldBounds.SizeY) * 1.20;
        var frameDepth = Math.Max(0.02, useZAsUp ? worldBounds.SizeY : worldBounds.SizeZ);

        var verticalFovRad = DegreesToRadians(fov);
        var horizontalFovRad = 2.0 * Math.Atan(Math.Tan(verticalFovRad * 0.5) * aspect);
        if (verticalFovRad <= 0 || horizontalFovRad <= 0)
        {
            return false;
        }

        var distanceForHeight = (frameHeight * 0.5) / Math.Tan(verticalFovRad * 0.5);
        var distanceForWidth = (frameWidth * 0.5) / Math.Tan(horizontalFovRad * 0.5);
        var distance = Math.Max(distanceForHeight, distanceForWidth) + (frameDepth * 0.8) + (faceCloseup ? 0.10 : 0.20);
        if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0)
        {
            return false;
        }

        distance = Math.Clamp(distance, faceCloseup ? 0.65 : 0.9, faceCloseup ? 8.0 : 14.0);

        var lookDirectionUnit = faceCloseup
            ? (useZAsUp ? new Vector3D(0, -1.0, -0.05) : new Vector3D(0, -0.05, -1.0))
            : (useZAsUp ? new Vector3D(0, -1.0, -0.20) : new Vector3D(0, -0.20, -1.0));
        lookDirectionUnit.Normalize();

        var position = target - (lookDirectionUnit * distance);
        var lookDirection = target - position;
        if (lookDirection.LengthSquared < 1e-6 || !IsFiniteVector(lookDirection) || !IsFinitePoint(position))
        {
            return false;
        }

        SetCamera(position, lookDirection, fov);
        return true;
    }

    private void SetRotationPivot(Point3D pivot)
    {
        if (_rotationXTransform is null || _rotationYTransform is null)
        {
            return;
        }

        _rotationXTransform.CenterX = pivot.X;
        _rotationXTransform.CenterY = pivot.Y;
        _rotationXTransform.CenterZ = pivot.Z;

        _rotationYTransform.CenterX = pivot.X;
        _rotationYTransform.CenterY = pivot.Y;
        _rotationYTransform.CenterZ = pivot.Z;
    }

    private void SetCamera(Point3D position, Vector3D lookDirection, double fieldOfView)
    {
        _camera.Position = position;
        _camera.LookDirection = lookDirection;
        _camera.UpDirection = _cameraUpDirection;
        _camera.FieldOfView = fieldOfView;
    }

    private void ApplyDefaultUsdOrientation(string usdPath)
    {
        // Face-only/A2F stages in this project need a forward quarter-turn to align with the camera.
        _usdPitchBaseDeg = ShouldUseFaceCloseupCamera(usdPath) ? 90.0 : 0.0;
        _rotationX.Angle = _usdPitchBaseDeg;
        _rotationY.Angle = 0.0;
    }

    private void UpdateDramaticLighting()
    {
        if (_ambientLight is null || _keyLight is null || _fillLight is null || _bounceLight is null || _rimLight is null || _cameraFillLight is null)
        {
            return;
        }

        var yaw = NormalizeDegrees(_rotationY.Angle);
        var pitchBase = _isUsdModelLoaded ? _usdPitchBaseDeg : 0.0;
        var pitch = Math.Clamp(_rotationX.Angle - pitchBase, -75, 75);
        var profile = Math.Clamp(Math.Abs(yaw) / 70.0, 0.0, 1.0);
        var side = yaw >= 0 ? 1.0 : -1.0;
        var tilt = pitch / 75.0;

        // Keep the key on the visible cheek while preserving a strong rim on the opposite side.
        var keyDirection = NormalizeVector(new Vector3D(-0.60 * side, -0.18 - (0.22 * tilt), -1.0));
        var fillDirection = NormalizeVector(new Vector3D(0.48 * side, -0.06, -0.42));
        var bounceDirection = NormalizeVector(new Vector3D(0.12, 1.0, 0.10));
        var rimDirection = NormalizeVector(new Vector3D(0.20 * side, -0.08 + (0.10 * profile), 1.0));

        _keyLight.Direction = keyDirection;
        _fillLight.Direction = fillDirection;
        _bounceLight.Direction = bounceDirection;
        _rimLight.Direction = rimDirection;

        // More contrast on profile angles, but keep enough fill to avoid losing the face in shadows.
        _ambientLight.Color = ScaleColor(Color.FromRgb(76, 82, 94), 0.58 - (0.18 * profile));
        _keyLight.Color = ScaleColor(Color.FromRgb(255, 236, 212), 0.95 + (0.30 * profile));
        _fillLight.Color = ScaleColor(Color.FromRgb(116, 150, 228), 0.42 - (0.10 * profile));
        _bounceLight.Color = ScaleColor(Color.FromRgb(134, 104, 82), 0.18 + (0.04 * (1.0 - profile)));
        _rimLight.Color = ScaleColor(Color.FromRgb(120, 226, 255), 0.58 + (0.55 * profile));

        var cameraPos = _camera.Position;
        _cameraFillLight.Position = new Point3D(cameraPos.X * 0.15, cameraPos.Y + 0.15, cameraPos.Z + 0.25);
        _cameraFillLight.Color = ScaleColor(Color.FromRgb(84, 92, 112), 0.32 + (0.08 * (1.0 - profile)));
        _cameraFillLight.Range = 10.0;
    }

    private void ConfigureOrbitAxes(bool useZAsUp)
    {
        _cameraUpDirection = useZAsUp ? new Vector3D(0, 0, 1) : new Vector3D(0, 1, 0);
        if (_rotationY is null || _rotationX is null)
        {
            return;
        }

        _rotationY.Axis = _cameraUpDirection;
        _rotationX.Axis = new Vector3D(1, 0, 0);
    }

    private static bool ShouldUseFaceCloseupCamera(string? usdPath)
    {
        if (string.IsNullOrWhiteSpace(usdPath))
        {
            return false;
        }

        var path = usdPath.Replace('/', '\\');
        return path.Contains("\\Audio2Face_", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\Audio2Face", StringComparison.OrdinalIgnoreCase)
               || path.Contains("_A2F_", StringComparison.OrdinalIgnoreCase)
               || path.Contains("fitted_mesh", StringComparison.OrdinalIgnoreCase)
               || path.Contains("transferred", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\face", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\head", StringComparison.OrdinalIgnoreCase)
               || path.Contains("_face", StringComparison.OrdinalIgnoreCase)
               || path.Contains("_head", StringComparison.OrdinalIgnoreCase);
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static Vector3D NormalizeVector(Vector3D vector)
    {
        if (vector.LengthSquared <= 1e-8 || !IsFiniteVector(vector))
        {
            return new Vector3D(0, -0.2, -1);
        }

        vector.Normalize();
        return vector;
    }

    private static Color ScaleColor(Color color, double intensity)
    {
        intensity = Math.Clamp(intensity, 0.0, 2.5);
        return Color.FromRgb(
            ClampToByte(color.R * intensity),
            ClampToByte(color.G * intensity),
            ClampToByte(color.B * intensity));
    }

    private static double NormalizeDegrees(double angle)
    {
        angle %= 360.0;
        if (angle < -180.0)
        {
            angle += 360.0;
        }
        else if (angle > 180.0)
        {
            angle -= 360.0;
        }

        return angle;
    }

    private static bool IsFiniteBounds(in Rect3D bounds)
    {
        return IsFinite(bounds.X)
               && IsFinite(bounds.Y)
               && IsFinite(bounds.Z)
               && IsFinite(bounds.SizeX)
               && IsFinite(bounds.SizeY)
               && IsFinite(bounds.SizeZ)
               && bounds.SizeX >= 0
               && bounds.SizeY >= 0
               && bounds.SizeZ >= 0;
    }

    private static bool IsFinitePoint(in Point3D point)
    {
        return IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z);
    }

    private static bool IsFiniteVector(in Vector3D vector)
    {
        return IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string ResolveUsdPathForCurrentAnimation(string baseUsdPath, string animationName)
    {
        if (string.IsNullOrWhiteSpace(baseUsdPath) || !File.Exists(baseUsdPath))
        {
            return baseUsdPath;
        }

        var directory = Path.GetDirectoryName(baseUsdPath);
        var fileName = Path.GetFileNameWithoutExtension(baseUsdPath);
        var extension = Path.GetExtension(baseUsdPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return baseUsdPath;
        }

        // Support worker clip aliases from assets/Props/*.usd (same folder as Worker.usd).
        // Full skeletal playback is separate; this switches the USD clip source used by the renderer.
        var clipSuffix = animationName switch
        {
            "idle" => "Market_Sales_Assisting_M",
            "listening" => "StandingDiscussion_LookingDown_M",
            "think" => "StandingDiscussion_LookingDown_M",
            "speaking" => "Market_Sales_Assisting_M",
            "wave" => "TrafficGuard_M",
            "dance_01" => "Market_Sales_SortOut_M",
            "nod" => "Worker_Idle_Pose",
            "shrug" => "StandingDiscussion_LookingDown_M",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(clipSuffix))
        {
            return baseUsdPath;
        }

        // Handle cases when base file is already "Worker.xxx" clip or plain "Worker".
        var characterName = fileName.Split('.')[0];
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return baseUsdPath;
        }

        var clipCandidate = Path.Combine(directory, $"{characterName}.{clipSuffix}{extension}");
        return File.Exists(clipCandidate) ? clipCandidate : baseUsdPath;
    }
}
