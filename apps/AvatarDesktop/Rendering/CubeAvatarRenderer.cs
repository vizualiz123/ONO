using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.IO;

namespace AvatarDesktop.Rendering;

public sealed class CubeAvatarRenderer : IAvatarRenderer
{
    private readonly Viewport3D _viewport;
    private readonly AxisAngleRotation3D _rotationY;
    private readonly AxisAngleRotation3D _rotationX;
    private readonly DiffuseMaterial _cubeMaterial;
    private readonly GeometryModel3D _cubeModel;
    private readonly Model3DGroup _avatarModelGroup;

    private double _ySpeedDegPerSec = 18;
    private double _xTargetDeg = 0;
    private double _pulse;
    private string _currentAnimation = "idle";
    private string _usdPath = string.Empty;
    private string _baseUsdPath = string.Empty;
    private UsdWpfSkinnedAvatarPlayer? _usdAnimatedPlayer;
    private bool _isUsdModelLoaded;
    private readonly Dictionary<string, double> _blendshapes = new(StringComparer.OrdinalIgnoreCase);

    public CubeAvatarRenderer(bool showGroundPlane = true)
    {
        _viewport = new Viewport3D
        {
            ClipToBounds = true,
        };

        var camera = showGroundPlane
            ? new PerspectiveCamera
            {
                Position = new Point3D(0, 1.6, 4.2),
                LookDirection = new Vector3D(0, -0.3, -4.2),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 55,
            }
            : new PerspectiveCamera
            {
                Position = new Point3D(0, 0.0, 4.0),
                LookDirection = new Vector3D(0, 0.0, -4.0),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 52,
            };
        _viewport.Camera = camera;

        var root = new Model3DGroup();
        // WPF has no real GI, so approximate it with layered ambient/fill/bounce/rim lights.
        root.Children.Add(new AmbientLight(Color.FromRgb(92, 96, 108))); // global base illumination
        root.Children.Add(new DirectionalLight(Color.FromRgb(236, 229, 220), new Vector3D(-0.9, -1.1, -1.4))); // key
        root.Children.Add(new DirectionalLight(Color.FromRgb(138, 168, 255), new Vector3D(0.85, -0.35, -0.35))); // sky fill
        root.Children.Add(new DirectionalLight(Color.FromRgb(122, 100, 82), new Vector3D(0.15, 1.0, 0.2))); // ground bounce
        root.Children.Add(new DirectionalLight(Color.FromRgb(170, 190, 235), new Vector3D(0.25, -0.15, 1.0))); // rim
        root.Children.Add(new PointLight(Color.FromRgb(88, 94, 106), new Point3D(0.0, 2.2, 2.8))); // soft camera-side fill

        _cubeMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(100, 175, 220)));
        _cubeModel = new GeometryModel3D
        {
            Geometry = BuildCubeMesh(),
            Material = _cubeMaterial,
            BackMaterial = _cubeMaterial,
        };

        _rotationY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 15);
        _rotationX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        var transformGroup = new Transform3DGroup();
        transformGroup.Children.Add(new RotateTransform3D(_rotationX));
        transformGroup.Children.Add(new RotateTransform3D(_rotationY));

        _avatarModelGroup = new Model3DGroup
        {
            Transform = transformGroup,
        };
        _avatarModelGroup.Children.Add(_cubeModel);

        root.Children.Add(_avatarModelGroup);
        if (showGroundPlane)
        {
            root.Children.Add(BuildGroundPlane());
        }

        _viewport.Children.Add(new ModelVisual3D { Content = root });
    }

    public UIElement View => _viewport;

    public void LoadUsd(string path)
    {
        _usdAnimatedPlayer?.Dispose();
        _usdAnimatedPlayer = null;
        _isUsdModelLoaded = false;

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
            _avatarModelGroup.Children.Add(_usdAnimatedPlayer.Model);
        }
        else if (!string.IsNullOrWhiteSpace(pathToLoad)
            && File.Exists(pathToLoad)
            && UsdWpfMeshLoader.TryCreateModel(pathToLoad, _cubeMaterial, out var usdModel))
        {
            _isUsdModelLoaded = true;
            _rotationY.Angle = 0;
            _rotationX.Angle = 0;
            _avatarModelGroup.Children.Clear();
            _avatarModelGroup.Children.Add(usdModel);
        }
        else
        {
            _isUsdModelLoaded = false;
            _avatarModelGroup.Children.Clear();
            _avatarModelGroup.Children.Add(_cubeModel);
        }

        SetAnimation("idle");
        UpdateColorFromMood();
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

        if (_isUsdModelLoaded)
        {
            _rotationY.Angle = 0;
            _rotationX.Angle = 0;
            _usdAnimatedPlayer?.Update(dt);
        }
        else
        {
            _rotationY.Angle = (_rotationY.Angle + (_ySpeedDegPerSec * seconds)) % 360;

            var xDelta = _xTargetDeg - _rotationX.Angle;
            _rotationX.Angle += xDelta * Math.Min(1.0, seconds * 8.0);
        }

        _pulse += seconds;
        UpdateColorFromMood();
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
