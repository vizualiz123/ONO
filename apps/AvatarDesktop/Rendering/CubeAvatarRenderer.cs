using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.IO;
using ShapePath = System.Windows.Shapes.Path;

namespace AvatarDesktop.Rendering;

public sealed class CubeAvatarRenderer : IAvatarRenderer
{
    private static readonly bool GhostPresentationEnabled = false;
    private static readonly bool WireframeEnabled = true;
    private static readonly bool WireframeOnlyPresentationEnabled = true;
    private static readonly bool SceneLightingEnabled = false;
    private static readonly bool AutoExportProceduralMouthMorphTarget = true;
    private static readonly bool AreaLightWaveEffectEnabled = false;
    private static readonly bool MainLayerBlurEnabled = false;
    private static readonly bool WireframeBlurEnabled = false;
    private static readonly bool WireframeGlowEnabled = true;
    private static readonly bool WireframeBackfaceCullingEnabled = true;
    private static readonly bool RadialFrameFadeEnabled = true;
    private const string UniversalBlendshape1Name = "blendshape_1";
    private const string UniversalBlendshape2Name = "blendshape_2";
    private const double MainLayerOpacity = 0.90; // 10% transparency
    private const double MainLayerGaussianBlurRadius = 3.2;
    private const double WireframeGaussianBlurRadius = 2.6;
    private const double WireframeGlowBlurRadius = 10.0;
    private const double AreaLightWaveBaseOpacity = 0.22;
    private const double AreaLightWaveSweepCyclesPerSecond = 0.18;
    private const double RadialFrameFadeMidStopOffset = 0.80;
    private const double RadialFrameFadeMidStopOpacity = 0.80;
    private const double IdleMouthOpenAmount = 0.58;
    private const double IdleMouthHeadCompensation = 0.14;
    private const double ExportMouthOpenAmount = 0.72;
    private const double ExportMouthHeadCompensation = 0.18;

    private readonly Grid _viewRoot;
    private readonly Border _mainLayerHost;
    private readonly Border _areaLightWaveOverlay;
    private readonly Viewport3D _viewport;
    private readonly ShapePath _wireframeOverlay;
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
    private readonly List<WireframeOverlayMesh> _wireframeMeshes = new();
    private Vector3D _cameraUpDirection = new(0, 1, 0);
    private readonly HashSet<GeometryModel3D> _ghostStyledGeometry = new();
    private readonly TranslateTransform _areaLightWaveTranslate;
    private readonly RotateTransform _areaLightWaveRotate;
    private readonly ScaleTransform _areaLightWaveScale;

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
        _viewRoot = new Grid
        {
            ClipToBounds = true,
        };
        if (RadialFrameFadeEnabled)
        {
            _viewRoot.OpacityMask = CreateRadialFrameOpacityMask();
        }
        _viewport = new Viewport3D
        {
            ClipToBounds = true,
        };
        _mainLayerHost = new Border
        {
            Child = _viewport,
            Background = Brushes.Transparent,
            Opacity = WireframeOnlyPresentationEnabled ? 0.0 : MainLayerOpacity,
            CacheMode = new BitmapCache(),
        };
        if (!WireframeOnlyPresentationEnabled && MainLayerBlurEnabled)
        {
            _mainLayerHost.Effect = new BlurEffect
            {
                Radius = MainLayerGaussianBlurRadius,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            };
        }
        RenderOptions.SetEdgeMode(_viewport, EdgeMode.Unspecified);
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.HighQuality);
        _viewport.SizeChanged += (_, _) => ReframeLoadedUsdCamera();
        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        _viewport.MouseLeave += Viewport_MouseLeave;
        _viewport.MouseWheel += Viewport_MouseWheel;

        _camera = new PerspectiveCamera();
        ApplyCubeCameraPreset();
        _viewport.Camera = _camera;

        _areaLightWaveTranslate = new TranslateTransform();
        _areaLightWaveRotate = new RotateTransform(-17.0, 0.5, 0.5);
        _areaLightWaveScale = new ScaleTransform(1.25, 1.15, 0.5, 0.5);
        var areaWaveTransform = new TransformGroup();
        areaWaveTransform.Children.Add(_areaLightWaveScale);
        areaWaveTransform.Children.Add(_areaLightWaveRotate);
        areaWaveTransform.Children.Add(_areaLightWaveTranslate);

        var areaWaveBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
            RelativeTransform = areaWaveTransform,
        };
        areaWaveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 120, 210, 255), 0.30));
        areaWaveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(16, 140, 226, 255), 0.42));
        areaWaveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(86, 235, 248, 255), 0.50));
        areaWaveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(20, 130, 220, 255), 0.58));
        areaWaveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 120, 210, 255), 0.70));

        _areaLightWaveOverlay = new Border
        {
            Background = areaWaveBrush,
            IsHitTestVisible = false,
            Opacity = 0.0,
            CacheMode = new BitmapCache(),
        };
        if (AreaLightWaveEffectEnabled)
        {
            _areaLightWaveOverlay.Effect = new BlurEffect
            {
                Radius = 7.5,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };
        }

        _wireframeOverlay = new ShapePath
        {
            Stroke = new SolidColorBrush(Color.FromArgb(245, 72, 255, 236)),
            StrokeThickness = 1.05,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = WireframeEnabled ? (WireframeOnlyPresentationEnabled ? 1.0 : 0.9) : 0.0,
            CacheMode = new BitmapCache(),
        };
        if (WireframeEnabled && WireframeOnlyPresentationEnabled && WireframeBlurEnabled)
        {
            _wireframeOverlay.Effect = new BlurEffect
            {
                Radius = WireframeGaussianBlurRadius,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            };
        }
        else if (WireframeEnabled && WireframeGlowEnabled)
        {
            _wireframeOverlay.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(40, 255, 235),
                BlurRadius = WireframeGlowBlurRadius,
                ShadowDepth = 0,
                Opacity = 0.95,
                RenderingBias = RenderingBias.Performance
            };
        }
        RenderOptions.SetEdgeMode(_wireframeOverlay, EdgeMode.Aliased);

        var root = new Model3DGroup();
        // Default portrait lighting for textured face rendering.
        _ambientLight = new AmbientLight(Color.FromRgb(90, 96, 106));
        _keyLight = new DirectionalLight(Color.FromRgb(236, 228, 220), new Vector3D(-0.55, -0.26, -1.0));
        _fillLight = new DirectionalLight(Color.FromRgb(134, 156, 198), new Vector3D(0.52, -0.10, -0.45));
        _bounceLight = new DirectionalLight(Color.FromRgb(112, 94, 80), new Vector3D(0.18, 1.0, 0.15));
        _rimLight = new DirectionalLight(Color.FromRgb(166, 188, 228), new Vector3D(0.22, -0.08, 1.0));
        _cameraFillLight = new PointLight(Color.FromRgb(92, 100, 114), new Point3D(0.0, 1.9, 2.4));
        if (SceneLightingEnabled)
        {
            root.Children.Add(_ambientLight);
            root.Children.Add(_keyLight);
            root.Children.Add(_fillLight);
            root.Children.Add(_bounceLight);
            root.Children.Add(_rimLight);
            root.Children.Add(_cameraFillLight);
        }

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
        UpdateAreaLightWaveOverlay(0);
        _viewport.Children.Add(new ModelVisual3D { Content = root });
        _viewRoot.Children.Add(_mainLayerHost);
        _viewRoot.Children.Add(_areaLightWaveOverlay);
        _viewRoot.Children.Add(_wireframeOverlay);
    }

    public UIElement View => _viewRoot;

    public void LoadUsd(string path)
    {
        _usdAnimatedPlayer?.Dispose();
        _usdAnimatedPlayer = null;
        _currentUsdModel = null;
        _isUsdModelLoaded = false;
        _usdPitchBaseDeg = 0;
        _hasFaceOrbitPivot = false;
        _ghostStyledGeometry.Clear();
        _wireframeMeshes.Clear();
        _wireframeOverlay.Data = null;

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
            if (GhostPresentationEnabled)
            {
                ApplyGhostMaterialPresentation(_currentUsdModel);
            }
            RebuildWireframeOverlay(_currentUsdModel);
            ApplyDefaultUsdOrientation(pathToLoad);
            ApplyUsdCameraPreset(pathToLoad, _currentUsdModel);
            TryExportProceduralMouthMorphTarget();
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
            if (GhostPresentationEnabled)
            {
                ApplyGhostMaterialPresentation(_currentUsdModel);
            }
            RebuildWireframeOverlay(_currentUsdModel);
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
            RebuildWireframeOverlay(_cubeModel);
        }

        SetAnimation("idle");
        UpdateColorFromMood();
        UpdateDramaticLighting();
        RefreshWireframeOverlay();
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
                var mouthOpen = speaking ? (0.18 + (0.82 * mouthRhythm)) : IdleMouthOpenAmount;
                var headCompensation = speaking ? (0.10 + (0.25 * mouthRhythm)) : IdleMouthHeadCompensation;
                _usdAnimatedPlayer.SetProceduralMouthRig(mouthOpen, headCompensation);
                _usdAnimatedPlayer.SetProceduralBlendshapePair(
                    GetBlendshape(UniversalBlendshape1Name),
                    GetBlendshape(UniversalBlendshape2Name));
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
        UpdateAreaLightWaveOverlay(nextPulse);
        RefreshWireframeOverlay();
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
        RefreshWireframeOverlay();
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

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_camera is null || e.Delta == 0)
        {
            return;
        }

        var lookDirection = _camera.LookDirection;
        if (lookDirection.LengthSquared < 1e-8 || !IsFiniteVector(lookDirection))
        {
            return;
        }

        var distance = lookDirection.Length;
        if (!IsFinite(distance) || distance <= 1e-6)
        {
            return;
        }

        var target = _camera.Position + lookDirection;
        if (!IsFinitePoint(target))
        {
            return;
        }

        var stepCount = e.Delta / 120.0;
        var zoomFactor = Math.Pow(0.88, stepCount); // wheel up => zoom in
        var newDistance = Math.Clamp(distance * zoomFactor, 0.12, 40.0);

        var directionUnit = lookDirection;
        directionUnit.Normalize();
        var newPosition = target - (directionUnit * newDistance);
        var newLookDirection = target - newPosition;
        if (!IsFinitePoint(newPosition) || !IsFiniteVector(newLookDirection) || newLookDirection.LengthSquared < 1e-8)
        {
            return;
        }

        _camera.Position = newPosition;
        _camera.LookDirection = newLookDirection;
        UpdateDramaticLighting();
        RefreshWireframeOverlay();
        e.Handled = true;
    }

    private void EndViewportDrag()
    {
        _isViewportDragging = false;
        if (_viewport.IsMouseCaptured)
        {
            _viewport.ReleaseMouseCapture();
        }
    }

    private static Brush CreateRadialFrameOpacityMask()
    {
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            SpreadMethod = GradientSpreadMethod.Pad,
        };

        // Center = 100% opacity, at 80% radius = 80% opacity, edge = 0%.
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255.0 * RadialFrameFadeMidStopOpacity), 255, 255, 255), RadialFrameFadeMidStopOffset));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));

        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
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
        RefreshWireframeOverlay();
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
        RefreshWireframeOverlay();
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

        if (!SceneLightingEnabled)
        {
            _ambientLight.Color = Colors.Black;
            _keyLight.Color = Colors.Black;
            _fillLight.Color = Colors.Black;
            _bounceLight.Color = Colors.Black;
            _rimLight.Color = Colors.Black;
            _cameraFillLight.Color = Colors.Black;
            _cameraFillLight.Range = 0.0;
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

        // Default scene light: neutral portrait setup with subtle dynamic contrast on rotation.
        _ambientLight.Color = ScaleColor(Color.FromRgb(92, 98, 108), 0.78 - (0.06 * profile));
        _keyLight.Color = ScaleColor(Color.FromRgb(236, 228, 220), 0.92 + (0.14 * profile));
        _fillLight.Color = ScaleColor(Color.FromRgb(138, 158, 196), 0.46 - (0.10 * profile));
        _bounceLight.Color = ScaleColor(Color.FromRgb(118, 98, 84), 0.16 + (0.03 * (1.0 - profile)));
        _rimLight.Color = ScaleColor(Color.FromRgb(168, 188, 226), 0.36 + (0.26 * profile));

        var cameraPos = _camera.Position;
        _cameraFillLight.Position = new Point3D(cameraPos.X * 0.15, cameraPos.Y + 0.15, cameraPos.Z + 0.25);
        _cameraFillLight.Color = ScaleColor(Color.FromRgb(92, 100, 114), 0.28 + (0.06 * (1.0 - profile)));
        _cameraFillLight.Range = 10.0;
    }

    private void UpdateAreaLightWaveOverlay(double timeSeconds)
    {
        if (_areaLightWaveOverlay is null || _areaLightWaveTranslate is null || _areaLightWaveRotate is null || _areaLightWaveScale is null)
        {
            return;
        }

        if (!AreaLightWaveEffectEnabled)
        {
            _areaLightWaveOverlay.Opacity = 0.0;
            return;
        }

        var cycle = (timeSeconds * AreaLightWaveSweepCyclesPerSecond) % 1.0;
        if (cycle < 0)
        {
            cycle += 1.0;
        }

        var pulse = 0.72 + (0.28 * ((Math.Sin(timeSeconds * 1.7) + 1.0) * 0.5));
        var drift = Math.Sin(timeSeconds * 0.85) * 0.035;

        // Brush transform is in relative coordinates. Sweep beyond edges so the wave enters/exits smoothly.
        _areaLightWaveTranslate.X = -0.85 + (1.70 * cycle);
        _areaLightWaveTranslate.Y = drift;
        _areaLightWaveRotate.Angle = -17.0 + (Math.Sin(timeSeconds * 0.42) * 3.0);
        _areaLightWaveScale.ScaleX = 1.20 + (Math.Sin(timeSeconds * 0.63) * 0.08);
        _areaLightWaveScale.ScaleY = 1.10 + (Math.Cos(timeSeconds * 0.57) * 0.05);

        _areaLightWaveOverlay.Opacity = AreaLightWaveBaseOpacity * pulse;
    }

    private void TryExportProceduralMouthMorphTarget()
    {
        if (!AutoExportProceduralMouthMorphTarget || _usdAnimatedPlayer is null || string.IsNullOrWhiteSpace(_usdPath))
        {
            return;
        }

        try
        {
            var outputPath = ResolveMorphTargetExportPath();
            _ = _usdAnimatedPlayer.TryExportProceduralMouthOpenMorphTarget(
                outputPath,
                ExportMouthOpenAmount,
                ExportMouthHeadCompensation);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string ResolveMorphTargetExportPath()
    {
        // Prefer a repo-local usd/morph_targets folder when running from the project.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidateUsdDir = Path.Combine(current.FullName, "usd");
            if (Directory.Exists(candidateUsdDir))
            {
                return Path.Combine(candidateUsdDir, "morph_targets", "mouth_open_procedural.json");
            }

            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "usd_runtime", "morph_targets", "mouth_open_procedural.json");
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

    private void RebuildWireframeOverlay(Model3D model)
    {
        _wireframeMeshes.Clear();

        if (!WireframeEnabled)
        {
            if (_wireframeOverlay is not null)
            {
                _wireframeOverlay.Data = null;
            }
            return;
        }

        CollectWireframeMeshes(model, Transform3D.Identity);
    }

    private void CollectWireframeMeshes(Model3D model, Transform3D accumulatedTransform)
    {
        if (model is Model3DGroup group)
        {
            var groupTransform = CombineTransforms(accumulatedTransform, group.Transform);
            foreach (var child in group.Children)
            {
                CollectWireframeMeshes(child, groupTransform);
            }

            return;
        }

        if (model is not GeometryModel3D geometryModel || geometryModel.Geometry is not MeshGeometry3D mesh)
        {
            return;
        }

        if (mesh.Positions is null || mesh.Positions.Count == 0 || mesh.TriangleIndices is null || mesh.TriangleIndices.Count < 3)
        {
            return;
        }

        var topology = BuildWireframeTopology(mesh);
        if (topology is null || topology.EdgePairs.Length == 0)
        {
            return;
        }

        var localTransform = CombineTransforms(accumulatedTransform, geometryModel.Transform);
        _wireframeMeshes.Add(new WireframeOverlayMesh(mesh, localTransform, topology));
    }

    private void RefreshWireframeOverlay()
    {
        if (!WireframeEnabled || _wireframeOverlay is null || _viewport is null || _camera is null)
        {
            return;
        }

        var width = _viewport.ActualWidth;
        var height = _viewport.ActualHeight;
        if (width <= 1.0 || height <= 1.0 || _wireframeMeshes.Count == 0)
        {
            _wireframeOverlay.Data = null;
            return;
        }

        var stream = new StreamGeometry();
        using var ctx = stream.Open();
        var any = false;

        foreach (var overlayMesh in _wireframeMeshes)
        {
            var positions = overlayMesh.Mesh.Positions;
            if (positions is null || positions.Count == 0)
            {
                continue;
            }

            overlayMesh.EnsureCapacity(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                var point = positions[i];
                point = TransformPointSafe(overlayMesh.LocalTransform, point);
                point = TransformPointSafe(_avatarModelGroup.Transform, point);
                overlayMesh.World[i] = point;

                if (TryProjectPointToViewport(point, width, height, out var screen))
                {
                    overlayMesh.Visible[i] = true;
                    overlayMesh.Projected[i] = screen;
                }
                else
                {
                    overlayMesh.Visible[i] = false;
                }
            }

            if (WireframeBackfaceCullingEnabled)
            {
                var triangles = overlayMesh.Triangles;
                var triangleCount = triangles.Length / 3;
                overlayMesh.EnsureTriangleFacingCapacity(triangleCount);
                for (var triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
                {
                    overlayMesh.TriangleFrontFacing[triangleIndex] = IsTriangleFrontFacing(overlayMesh, triangleIndex);
                }
            }

            var edgePairs = overlayMesh.EdgePairs;
            for (var edgeIndex = 0; edgeIndex + 1 < edgePairs.Length; edgeIndex += 2)
            {
                if (WireframeBackfaceCullingEnabled)
                {
                    var edgeSlot = edgeIndex / 2;
                    var t0 = overlayMesh.EdgeFirstTriangleByEdge[edgeSlot];
                    var t1 = overlayMesh.EdgeSecondTriangleByEdge[edgeSlot];
                    var front0 = t0 >= 0 && t0 < overlayMesh.TriangleFrontFacing.Length && overlayMesh.TriangleFrontFacing[t0];
                    var front1 = t1 >= 0 && t1 < overlayMesh.TriangleFrontFacing.Length && overlayMesh.TriangleFrontFacing[t1];
                    if (!front0 && !front1)
                    {
                        continue;
                    }
                }

                var a = edgePairs[edgeIndex];
                var b = edgePairs[edgeIndex + 1];
                if (a < 0 || b < 0 || a >= overlayMesh.Visible.Length || b >= overlayMesh.Visible.Length)
                {
                    continue;
                }

                if (!overlayMesh.Visible[a] || !overlayMesh.Visible[b])
                {
                    continue;
                }

                var p0 = overlayMesh.Projected[a];
                var p1 = overlayMesh.Projected[b];
                if (!LineMightIntersectViewport(p0, p1, width, height))
                {
                    continue;
                }

                ctx.BeginFigure(p0, isFilled: false, isClosed: false);
                ctx.LineTo(p1, isStroked: true, isSmoothJoin: false);
                any = true;
            }
        }

        if (!any)
        {
            _wireframeOverlay.Data = null;
            return;
        }

        if (stream.CanFreeze)
        {
            stream.Freeze();
        }

        _wireframeOverlay.Data = stream;
    }

    private bool IsTriangleFrontFacing(WireframeOverlayMesh overlayMesh, int triangleIndex)
    {
        var triangles = overlayMesh.Triangles;
        var triOffset = triangleIndex * 3;
        if (triOffset + 2 >= triangles.Length)
        {
            return false;
        }

        var i0 = triangles[triOffset];
        var i1 = triangles[triOffset + 1];
        var i2 = triangles[triOffset + 2];
        if (i0 < 0 || i1 < 0 || i2 < 0
            || i0 >= overlayMesh.World.Length
            || i1 >= overlayMesh.World.Length
            || i2 >= overlayMesh.World.Length)
        {
            return false;
        }

        // Prefer screen-space winding when projected vertices are available.
        if (i0 < overlayMesh.Visible.Length && i1 < overlayMesh.Visible.Length && i2 < overlayMesh.Visible.Length
            && overlayMesh.Visible[i0] && overlayMesh.Visible[i1] && overlayMesh.Visible[i2])
        {
            var p0 = overlayMesh.Projected[i0];
            var p1 = overlayMesh.Projected[i1];
            var p2 = overlayMesh.Projected[i2];
            var signedArea2 = ((p1.X - p0.X) * (p2.Y - p0.Y)) - ((p1.Y - p0.Y) * (p2.X - p0.X));
            if (IsFinite(signedArea2) && Math.Abs(signedArea2) > 1e-8)
            {
                // Screen Y grows downward, so front-facing CCW triangles project with negative signed area.
                return signedArea2 < 0.0;
            }
        }

        var w0 = overlayMesh.World[i0];
        var w1 = overlayMesh.World[i1];
        var w2 = overlayMesh.World[i2];
        var normal = Vector3D.CrossProduct(w1 - w0, w2 - w0);
        if (normal.LengthSquared <= 1e-12 || !IsFiniteVector(normal))
        {
            return false;
        }

        var center = new Point3D(
            (w0.X + w1.X + w2.X) / 3.0,
            (w0.Y + w1.Y + w2.Y) / 3.0,
            (w0.Z + w1.Z + w2.Z) / 3.0);
        var toCamera = _camera.Position - center;
        return Vector3D.DotProduct(normal, toCamera) > 0.0;
    }

    private bool TryProjectPointToViewport(Point3D worldPoint, double viewportWidth, double viewportHeight, out Point screenPoint)
    {
        screenPoint = default;

        var forward = _camera.LookDirection;
        if (forward.LengthSquared < 1e-8 || !IsFiniteVector(forward))
        {
            return false;
        }

        forward.Normalize();

        var up = _camera.UpDirection;
        if (up.LengthSquared < 1e-8 || !IsFiniteVector(up))
        {
            up = new Vector3D(0, 1, 0);
        }

        up -= forward * Vector3D.DotProduct(up, forward);
        if (up.LengthSquared < 1e-8 || !IsFiniteVector(up))
        {
            up = Math.Abs(forward.Y) < 0.98 ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
            up -= forward * Vector3D.DotProduct(up, forward);
            if (up.LengthSquared < 1e-8)
            {
                return false;
            }
        }
        up.Normalize();

        var right = Vector3D.CrossProduct(forward, up);
        if (right.LengthSquared < 1e-8 || !IsFiniteVector(right))
        {
            return false;
        }
        right.Normalize();
        up = Vector3D.CrossProduct(right, forward);
        up.Normalize();

        var toPoint = worldPoint - _camera.Position;
        var depth = Vector3D.DotProduct(toPoint, forward);
        if (depth <= 0.01 || !IsFinite(depth))
        {
            return false;
        }

        var x = Vector3D.DotProduct(toPoint, right);
        var y = Vector3D.DotProduct(toPoint, up);

        var verticalFovRad = DegreesToRadians(Math.Clamp(_camera.FieldOfView, 1.0, 179.0));
        var tanHalfFov = Math.Tan(verticalFovRad * 0.5);
        if (!IsFinite(tanHalfFov) || tanHalfFov <= 1e-8)
        {
            return false;
        }

        var aspect = viewportWidth / Math.Max(1.0, viewportHeight);
        var halfHeight = depth * tanHalfFov;
        var halfWidth = halfHeight * aspect;
        if (halfWidth <= 1e-8 || halfHeight <= 1e-8)
        {
            return false;
        }

        var ndcX = x / halfWidth;
        var ndcY = y / halfHeight;
        if (!IsFinite(ndcX) || !IsFinite(ndcY))
        {
            return false;
        }

        screenPoint = new Point(
            ((ndcX * 0.5) + 0.5) * viewportWidth,
            ((-ndcY * 0.5) + 0.5) * viewportHeight);
        return IsFinitePoint2D(screenPoint);
    }

    private static Transform3D CombineTransforms(Transform3D? first, Transform3D? second)
    {
        var firstHasValue = HasNonIdentityTransform(first);
        var secondHasValue = HasNonIdentityTransform(second);

        if (!firstHasValue && !secondHasValue)
        {
            return Transform3D.Identity;
        }

        if (firstHasValue && !secondHasValue)
        {
            return first!;
        }

        if (!firstHasValue)
        {
            return second!;
        }

        var group = new Transform3DGroup();
        group.Children.Add(first!);
        group.Children.Add(second!);
        return group;
    }

    private static bool HasNonIdentityTransform(Transform3D? transform)
    {
        if (transform is null)
        {
            return false;
        }

        try
        {
            return !transform.Value.IsIdentity;
        }
        catch
        {
            return true;
        }
    }

    private static Point3D TransformPointSafe(Transform3D? transform, Point3D point)
    {
        if (!HasNonIdentityTransform(transform))
        {
            return point;
        }

        try
        {
            return transform!.Transform(point);
        }
        catch
        {
            return point;
        }
    }

    private static WireframeTopology? BuildWireframeTopology(MeshGeometry3D mesh)
    {
        var triangles = mesh.TriangleIndices;
        if (triangles is null || triangles.Count < 3)
        {
            return null;
        }

        var triangleValues = new int[triangles.Count];
        triangles.CopyTo(triangleValues, 0);

        var edgePairs = new List<int>(triangles.Count * 2);
        var edgeFirstTriangleByEdge = new List<int>(triangles.Count);
        var edgeSecondTriangleByEdge = new List<int>(triangles.Count);
        var edgeSlotByKey = new Dictionary<ulong, int>(triangles.Count);
        for (var i = 0; i + 2 < triangleValues.Length; i += 3)
        {
            var triangleIndex = i / 3;
            AddEdge(triangleValues[i], triangleValues[i + 1], triangleIndex);
            AddEdge(triangleValues[i + 1], triangleValues[i + 2], triangleIndex);
            AddEdge(triangleValues[i + 2], triangleValues[i], triangleIndex);
        }

        if (edgePairs.Count == 0)
        {
            return null;
        }

        return new WireframeTopology(
            triangleValues,
            edgePairs.ToArray(),
            edgeFirstTriangleByEdge.ToArray(),
            edgeSecondTriangleByEdge.ToArray());

        void AddEdge(int a, int b, int triangleIndex)
        {
            if (a == b || a < 0 || b < 0)
            {
                return;
            }

            var min = Math.Min(a, b);
            var max = Math.Max(a, b);
            var key = ((ulong)(uint)min << 32) | (uint)max;
            if (edgeSlotByKey.TryGetValue(key, out var existingSlot))
            {
                if (existingSlot >= 0
                    && existingSlot < edgeSecondTriangleByEdge.Count
                    && edgeSecondTriangleByEdge[existingSlot] < 0)
                {
                    edgeSecondTriangleByEdge[existingSlot] = triangleIndex;
                }
                return;
            }

            var edgeSlot = edgePairs.Count / 2;
            edgeSlotByKey[key] = edgeSlot;

            edgePairs.Add(a);
            edgePairs.Add(b);
            edgeFirstTriangleByEdge.Add(triangleIndex);
            edgeSecondTriangleByEdge.Add(-1);
        }
    }

    private static bool LineMightIntersectViewport(Point p0, Point p1, double width, double height)
    {
        const double margin = 64.0;
        var minX = Math.Min(p0.X, p1.X);
        var maxX = Math.Max(p0.X, p1.X);
        var minY = Math.Min(p0.Y, p1.Y);
        var maxY = Math.Max(p0.Y, p1.Y);

        return maxX >= -margin
               && maxY >= -margin
               && minX <= width + margin
               && minY <= height + margin;
    }

    private static bool IsFinitePoint2D(in Point point)
    {
        return IsFinite(point.X) && IsFinite(point.Y);
    }

    private void ApplyGhostMaterialPresentation(Model3D model)
    {
        foreach (var geometry in EnumerateGeometryModels(model))
        {
            if (!_ghostStyledGeometry.Add(geometry))
            {
                continue;
            }

            geometry.Material = CreateGhostMaterial(geometry.Material);
            geometry.BackMaterial = CreateGhostMaterial(geometry.BackMaterial ?? geometry.Material);
        }
    }

    private static IEnumerable<GeometryModel3D> EnumerateGeometryModels(Model3D model)
    {
        if (model is GeometryModel3D geometry)
        {
            yield return geometry;
            yield break;
        }

        if (model is not Model3DGroup group)
        {
            yield break;
        }

        foreach (var child in group.Children)
        {
            foreach (var item in EnumerateGeometryModels(child))
            {
                yield return item;
            }
        }
    }

    private static Material CreateGhostMaterial(Material? source)
    {
        var hasTexture = MaterialContainsImageBrush(source);
        var group = new MaterialGroup();
        group.Children.Add(CloneMaterialForGhostBase(source));

        var glowBrush = new SolidColorBrush(hasTexture
            ? Color.FromArgb(28, 108, 235, 235)
            : Color.FromArgb(86, 116, 255, 240));
        var specBrush = new SolidColorBrush(hasTexture
            ? Color.FromArgb(76, 220, 245, 250)
            : Color.FromArgb(110, 230, 255, 250));
        var emissive = new EmissiveMaterial(glowBrush);
        var specular = new SpecularMaterial(specBrush, hasTexture ? 52 : 84);
        if (glowBrush.CanFreeze) glowBrush.Freeze();
        if (specBrush.CanFreeze) specBrush.Freeze();
        if (emissive.CanFreeze) emissive.Freeze();
        if (specular.CanFreeze) specular.Freeze();
        group.Children.Add(emissive);
        group.Children.Add(specular);

        if (group.CanFreeze)
        {
            group.Freeze();
        }

        return group;
    }

    private static Material CloneMaterialForGhostBase(Material? source)
    {
        if (source is null)
        {
            var brush = new SolidColorBrush(Color.FromArgb(168, 120, 180, 220));
            if (brush.CanFreeze) brush.Freeze();
            var diffuse = new DiffuseMaterial(brush);
            if (diffuse.CanFreeze) diffuse.Freeze();
            return diffuse;
        }

        var clone = (Material)source.CloneCurrentValue();
        AdjustMaterialForGhost(clone);
        if (clone.CanFreeze)
        {
            clone.Freeze();
        }

        return clone;
    }

    private static void AdjustMaterialForGhost(Material material)
    {
        switch (material)
        {
            case MaterialGroup group:
                foreach (var child in group.Children)
                {
                    AdjustMaterialForGhost(child);
                }
                break;
            case DiffuseMaterial diffuse:
                diffuse.Brush = diffuse.Brush is ImageBrush
                    ? CloneBrushForGhost(diffuse.Brush, 0.98, tintTowardGhost: false)
                    : CloneBrushForGhost(diffuse.Brush, 0.58, tintTowardGhost: true);
                break;
            case EmissiveMaterial emissive:
                emissive.Brush = emissive.Brush is ImageBrush
                    ? CloneBrushForGhost(emissive.Brush, 0.18, tintTowardGhost: false)
                    : CloneBrushForGhost(emissive.Brush, 0.35, tintTowardGhost: true);
                break;
            case SpecularMaterial specular:
                specular.Brush = CloneBrushForGhost(specular.Brush, 0.55, tintTowardGhost: true);
                specular.SpecularPower = Math.Max(specular.SpecularPower, 42);
                break;
        }
    }

    private static Brush CloneBrushForGhost(Brush? brush, double opacityMultiplier, bool tintTowardGhost)
    {
        if (brush is null)
        {
            var fallback = new SolidColorBrush(Color.FromArgb(170, 130, 190, 232));
            if (fallback.CanFreeze) fallback.Freeze();
            return fallback;
        }

        var clone = (Brush)brush.CloneCurrentValue();
        var effectiveOpacityMultiplier = brush is ImageBrush ? Math.Max(opacityMultiplier, 0.84) : opacityMultiplier;
        clone.Opacity = Math.Clamp(clone.Opacity * effectiveOpacityMultiplier, 0.10, 0.98);

        if (clone is SolidColorBrush solid)
        {
            var c = solid.Color;
            if (tintTowardGhost)
            {
                c = LerpColor(c, Color.FromRgb(128, 236, 245), 0.28);
            }
            solid.Color = Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        else if (clone is ImageBrush imageBrush)
        {
            RenderOptions.SetBitmapScalingMode(imageBrush, BitmapScalingMode.HighQuality);
            RenderOptions.SetCachingHint(imageBrush, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(imageBrush, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(imageBrush, 2.0);
        }

        return clone;
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        var r = a.R + ((b.R - a.R) * t);
        var g = a.G + ((b.G - a.G) * t);
        var bl = a.B + ((b.B - a.B) * t);
        var alpha = a.A + ((b.A - a.A) * t);
        return Color.FromArgb(ClampToByte(alpha), ClampToByte(r), ClampToByte(g), ClampToByte(bl));
    }

    private static bool MaterialContainsImageBrush(Material? material)
    {
        if (material is null)
        {
            return false;
        }

        return material switch
        {
            DiffuseMaterial d => d.Brush is ImageBrush,
            EmissiveMaterial e => e.Brush is ImageBrush,
            SpecularMaterial s => s.Brush is ImageBrush,
            MaterialGroup g => g.Children.Any(MaterialContainsImageBrush),
            _ => false
        };
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

    private sealed class WireframeTopology
    {
        public WireframeTopology(int[] triangles, int[] edgePairs, int[] edgeFirstTriangleByEdge, int[] edgeSecondTriangleByEdge)
        {
            Triangles = triangles;
            EdgePairs = edgePairs;
            EdgeFirstTriangleByEdge = edgeFirstTriangleByEdge;
            EdgeSecondTriangleByEdge = edgeSecondTriangleByEdge;
        }

        public int[] Triangles { get; }
        public int[] EdgePairs { get; }
        public int[] EdgeFirstTriangleByEdge { get; }
        public int[] EdgeSecondTriangleByEdge { get; }
    }

    private sealed class WireframeOverlayMesh
    {
        public WireframeOverlayMesh(MeshGeometry3D mesh, Transform3D localTransform, WireframeTopology topology)
        {
            Mesh = mesh;
            LocalTransform = localTransform;
            _topology = topology;
        }

        private readonly WireframeTopology _topology;

        public MeshGeometry3D Mesh { get; }
        public Transform3D LocalTransform { get; }
        public int[] EdgePairs => _topology.EdgePairs;
        public int[] Triangles => _topology.Triangles;
        public int[] EdgeFirstTriangleByEdge => _topology.EdgeFirstTriangleByEdge;
        public int[] EdgeSecondTriangleByEdge => _topology.EdgeSecondTriangleByEdge;
        public Point[] Projected { get; private set; } = Array.Empty<Point>();
        public Point3D[] World { get; private set; } = Array.Empty<Point3D>();
        public bool[] Visible { get; private set; } = Array.Empty<bool>();
        public bool[] TriangleFrontFacing { get; private set; } = Array.Empty<bool>();

        public void EnsureCapacity(int vertexCount)
        {
            if (Projected.Length != vertexCount)
            {
                Projected = new Point[vertexCount];
            }

            if (World.Length != vertexCount)
            {
                World = new Point3D[vertexCount];
            }

            if (Visible.Length != vertexCount)
            {
                Visible = new bool[vertexCount];
            }
        }

        public void EnsureTriangleFacingCapacity(int triangleCount)
        {
            if (TriangleFrontFacing.Length != triangleCount)
            {
                TriangleFrontFacing = new bool[triangleCount];
            }
        }
    }
}
