from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import os
import signal
import socket
import subprocess
import sys
import time
import webbrowser
from pathlib import Path


APP_NAME = "Nein3D"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_UI_PORT = 7860
DEFAULT_TEXT_ENCODER_PORT = 9550
DEFAULT_MODEL = "kimodo-soma-seed"
DEFAULT_WINDOW_DOCK = "right"
NORMAL_WINDOW_WIDTH = 1440
NORMAL_WINDOW_HEIGHT = 920
DOCK_WINDOW_RATIO = 0.42
DOCK_WINDOW_MIN_WIDTH = 720
WINDOW_MIN_SIZE = (720, 640)


def _frozen() -> bool:
    return getattr(sys, "frozen", False)


def _app_dir() -> Path:
    if _frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def _candidate_roots() -> list[Path]:
    roots: list[Path] = []
    env_root = os.environ.get("NEIN3D_HOME") or os.environ.get("KIMODO_HOME")
    if env_root:
        roots.append(Path(env_root))
    roots.extend([Path.cwd(), _app_dir()])
    expanded: list[Path] = []
    for root in roots:
        root = root.resolve()
        expanded.append(root)
        expanded.extend(root.parents)
    deduped: list[Path] = []
    seen: set[Path] = set()
    for root in expanded:
        if root not in seen:
            seen.add(root)
            deduped.append(root)
    return deduped


def find_project_root() -> Path:
    if _frozen():
        # In a frozen build the kimodo package is bundled inside the exe; we use
        # the exe directory only for log output and runtime files next to it.
        return _app_dir()
    for root in _candidate_roots():
        if (root / "kimodo" / "demo" / "__init__.py").exists() and python_exe(root).exists():
            return root
    searched = "\n".join(f"  - {path}" for path in _candidate_roots()[:12])
    raise RuntimeError(
        "Cannot find Nein3D project root. Put Nein3D.exe inside the project tree "
        "or set NEIN3D_HOME.\nSearched:\n" + searched
    )


def python_exe(root: Path) -> Path:
    return root / ".venv" / "Scripts" / "python.exe"


def child_executable(root: Path) -> str:
    """Pick the interpreter for spawning textencoder/studio child processes."""
    if _frozen():
        return sys.executable  # the bundled exe re-invokes itself with --role
    return str(python_exe(root))


def _run_role(role: str, role_args: list[str]) -> int:
    """Direct in-process dispatch for child roles when launched with --role."""
    if role == "textencoder":
        # Replicate `python -m kimodo.scripts.run_text_encoder_server` semantics.
        sys.argv = ["kimodo.scripts.run_text_encoder_server", *role_args]
        from kimodo.scripts.run_text_encoder_server import main as te_main

        te_main()
        return 0

    if role == "studio":
        # Replicate `python -m kimodo.demo`. Pass through extra args (e.g. --model X).
        sys.argv = ["kimodo.demo", *role_args]
        from kimodo.demo import main as demo_main

        demo_main()
        return 0

    raise SystemExit(f"Unknown --role: {role}")


def detect_gpu_backend(child_exec: str) -> tuple[str, str]:
    """Probe the venv (or the frozen exe) for the best available accelerator.

    Returns (backend, label). Backend is one of: cuda, directml, xpu, mps, cpu.
    """
    override = os.environ.get("KIMODO_GPU_BACKEND", "").strip().lower()
    if override in {"cuda", "directml", "xpu", "mps", "cpu"}:
        return override, f"override:{override}"

    probe_code = (
        "import json, sys\n"
        "result = {'backend': 'cpu', 'label': 'cpu'}\n"
        "try:\n"
        "    import torch\n"
        "    if torch.cuda.is_available() and torch.cuda.device_count() > 0:\n"
        "        result = {'backend': 'cuda', 'label': torch.cuda.get_device_name(0)}\n"
        "    else:\n"
        "        try:\n"
        "            import torch_directml\n"
        "            if torch_directml.is_available():\n"
        "                idx = torch_directml.default_device()\n"
        "                result = {'backend': 'directml', 'label': torch_directml.device_name(idx)}\n"
        "        except Exception:\n"
        "            pass\n"
        "        if result['backend'] == 'cpu' and hasattr(torch, 'xpu') and torch.xpu.is_available():\n"
        "            result = {'backend': 'xpu', 'label': torch.xpu.get_device_name(0)}\n"
        "        if result['backend'] == 'cpu' and getattr(torch.backends, 'mps', None) and torch.backends.mps.is_available():\n"
        "            result = {'backend': 'mps', 'label': 'Apple MPS'}\n"
        "except Exception as exc:\n"
        "    result = {'backend': 'cpu', 'label': f'cpu (probe failed: {exc})'}\n"
        "sys.stdout.write(json.dumps(result))\n"
    )

    if _frozen():
        # When frozen, the child exe doesn't accept arbitrary -c. Probe in-process.
        try:
            import torch  # noqa: E402

            if torch.cuda.is_available() and torch.cuda.device_count() > 0:
                return "cuda", torch.cuda.get_device_name(0)
            try:
                import torch_directml  # type: ignore

                if torch_directml.is_available():
                    idx = torch_directml.default_device()
                    return "directml", torch_directml.device_name(idx)
            except Exception:
                pass
            if hasattr(torch, "xpu") and torch.xpu.is_available():
                return "xpu", torch.xpu.get_device_name(0)
            mps = getattr(torch.backends, "mps", None)
            if mps is not None and mps.is_available():
                return "mps", "Apple MPS"
            return "cpu", "cpu"
        except Exception as exc:
            return "cpu", f"cpu (probe error: {exc})"

    try:
        completed = subprocess.run(
            [child_exec, "-c", probe_code],
            capture_output=True,
            text=True,
            timeout=30,
        )
    except Exception as exc:
        return "cpu", f"cpu (probe error: {exc})"

    if completed.returncode != 0:
        return "cpu", f"cpu (probe rc={completed.returncode})"

    try:
        import json

        payload = json.loads(completed.stdout.strip().splitlines()[-1])
        return str(payload.get("backend", "cpu")), str(payload.get("label", "cpu"))
    except Exception as exc:
        return "cpu", f"cpu (parse error: {exc})"


def port_open(host: str, port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.6)
        return sock.connect_ex((host, port)) == 0


def wait_for_port(
    *,
    host: str,
    port: int,
    name: str,
    timeout_seconds: int,
    process: subprocess.Popen | None = None,
) -> None:
    deadline = time.monotonic() + timeout_seconds
    last_message = 0.0
    while time.monotonic() < deadline:
        if port_open(host, port):
            print(f"[OK] {name}: http://{host}:{port}")
            return
        if process is not None and process.poll() is not None:
            raise RuntimeError(f"{name} stopped during startup. Check logs.")
        now = time.monotonic()
        if now - last_message > 5:
            print(f"[..] Waiting for {name} on {host}:{port}")
            last_message = now
        time.sleep(1)
    raise TimeoutError(f"{name} did not open port {host}:{port} in time.")


def _default_dock() -> str:
    value = os.environ.get("NEIN3D_WINDOW_DOCK", DEFAULT_WINDOW_DOCK).strip().lower()
    return value if value in {"none", "left", "right"} else DEFAULT_WINDOW_DOCK


def _windows_work_area() -> tuple[int, int, int, int]:
    """Return the usable desktop work area as x, y, width, height."""
    if os.name == "nt":
        class RECT(ctypes.Structure):
            _fields_ = [
                ("left", ctypes.c_long),
                ("top", ctypes.c_long),
                ("right", ctypes.c_long),
                ("bottom", ctypes.c_long),
            ]

        rect = RECT()
        ok = ctypes.windll.user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0)
        if ok:
            return (
                int(rect.left),
                int(rect.top),
                int(rect.right - rect.left),
                int(rect.bottom - rect.top),
            )
    return (0, 0, NORMAL_WINDOW_WIDTH, NORMAL_WINDOW_HEIGHT)


def _window_geometry(dock: str) -> tuple[int, int, int, int]:
    x, y, work_width, work_height = _windows_work_area()
    if dock in {"left", "right"}:
        width = min(work_width, max(DOCK_WINDOW_MIN_WIDTH, int(work_width * DOCK_WINDOW_RATIO)))
        height = work_height
        return (x if dock == "left" else x + work_width - width, y, width, height)

    width = min(NORMAL_WINDOW_WIDTH, work_width)
    height = min(NORMAL_WINDOW_HEIGHT, work_height)
    return (x + max(0, (work_width - width) // 2), y + max(0, (work_height - height) // 2), width, height)


def _open_folder(path: Path) -> None:
    try:
        path.mkdir(parents=True, exist_ok=True)
        os.startfile(str(path))  # type: ignore[attr-defined]
    except Exception as exc:
        print(f"[WARN] Could not open folder {path}: {exc}")


def _make_window_menu(url: str, root: Path, window_ref: dict[str, object]):
    from webview.menu import Menu, MenuAction, MenuSeparator

    def current_window():
        return window_ref.get("window")

    def reload_view() -> None:
        window = current_window()
        if window is not None:
            window.load_url(url)

    def dock_window(side: str) -> None:
        window = current_window()
        if window is None:
            return
        x, y, width, height = _window_geometry(side)
        window.restore()
        window.resize(width, height)
        window.move(x, y)

    def normal_window() -> None:
        dock_window("none")

    def toggle_on_top() -> None:
        window = current_window()
        if window is not None:
            window.on_top = not window.on_top

    def close_window() -> None:
        window = current_window()
        if window is not None:
            window.destroy()

    def show_help() -> None:
        window = current_window()
        if window is None:
            webbrowser.open(url)
            return
        try:
            shown = window.evaluate_js(
                "Boolean(window.__nein3dShowHelp && window.__nein3dShowHelp())"
            )
            if not shown:
                webbrowser.open(url)
        except Exception as exc:
            print(f"[WARN] Could not open in-app help: {exc}")
            webbrowser.open(url)

    return [
        Menu(
            "Файл",
            [
                MenuAction("Открыть папку программы", lambda: _open_folder(root)),
                MenuAction("Открыть логи", lambda: _open_folder(root / "logs")),
                MenuSeparator(),
                MenuAction("Закрыть", close_window),
            ],
        ),
        Menu(
            "Вид",
            [
                MenuAction("Обновить интерфейс", reload_view),
                MenuAction("Открыть в браузере", lambda: webbrowser.open(url)),
            ],
        ),
        Menu(
            "Окно",
            [
                MenuAction("Прикрепить слева", lambda: dock_window("left")),
                MenuAction("Прикрепить справа", lambda: dock_window("right")),
                MenuAction("Обычный размер", normal_window),
                MenuAction("Во весь экран", lambda: current_window() and current_window().toggle_fullscreen()),
                MenuAction("Поверх окон", toggle_on_top),
            ],
        ),
        Menu(
            "Помощь",
            [
                MenuAction("Справка", show_help),
                MenuAction("Открыть адрес студии", lambda: webbrowser.open(url)),
            ],
        ),
    ]


def open_desktop_window(url: str, title: str, *, root: Path, dock: str = DEFAULT_WINDOW_DOCK) -> None:
    try:
        import webview  # type: ignore
    except Exception as exc:
        raise RuntimeError(
            "Desktop window runtime is missing. Install it with: "
            ".venv\\Scripts\\python.exe -m pip install pywebview"
        ) from exc

    dock = dock if dock in {"none", "left", "right"} else DEFAULT_WINDOW_DOCK
    x, y, width, height = _window_geometry(dock)
    window_ref: dict[str, object] = {}
    print(f"[>>] Opening desktop window: {title} ({dock}, {width}x{height} at {x},{y})")
    window = webview.create_window(
        title,
        url,
        width=width,
        height=height,
        x=x,
        y=y,
        min_size=WINDOW_MIN_SIZE,
        resizable=True,
        text_select=True,
        menu=_make_window_menu(url, root, window_ref),
    )
    window_ref["window"] = window
    webview.start(gui="edgechromium", debug=False)


def start_process(
    *,
    root: Path,
    name: str,
    args: list[str],
    env: dict[str, str],
    log_stem: str,
) -> subprocess.Popen:
    log_dir = root / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    out_path = log_dir / f"{log_stem}_{stamp}.out.log"
    err_path = log_dir / f"{log_stem}_{stamp}.err.log"
    print(f"[>>] Starting {name}")
    print(f"     stdout: {out_path}")
    print(f"     stderr: {err_path}")

    out_file = out_path.open("ab")
    err_file = err_path.open("ab")
    full_env = os.environ.copy()
    full_env.update(env)

    creation_flags = 0
    if os.name == "nt":
        creation_flags |= getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)

    process = subprocess.Popen(
        args,
        cwd=str(root),
        env=full_env,
        stdout=out_file,
        stderr=err_file,
        creationflags=creation_flags,
    )
    (root / f".nein3d_{log_stem}.pid").write_text(str(process.pid), encoding="utf-8")
    return process


def build_env(
    host: str,
    ui_port: int,
    text_encoder_port: int,
    title: str,
    backend: str,
) -> tuple[dict[str, str], dict[str, str]]:
    text_encoder_env = {
        "GRADIO_SERVER_NAME": host,
        "GRADIO_SERVER_PORT": str(text_encoder_port),
        "CUDA_VISIBLE_DEVICES": "",
        "TEXT_ENCODER_DEVICE": "cpu",
    }
    studio_env = {
        "SERVER_NAME": host,
        "SERVER_PORT": str(ui_port),
        "TEXT_ENCODER_MODE": "api",
        "TEXT_ENCODER_URL": f"http://{host}:{text_encoder_port}/",
        "LOCAL_CACHE": "True",
        "KIMODO_APP_TITLE": title,
        "KIMODO_PANEL_LABEL": title,
        # Desktop-only shell flags. Browser/web launches do not set these.
        "NEIN3D_DESKTOP_UI": "true",
        "NEIN3D_WINDOWS_APP": "true",
        "KIMODO_DARK_MODE": "true",
        "KIMODO_AGGRESSIVE_GPU_CLEANUP": "true",
        "KIMODO_TEXT_ENCODER_CPU": "true",
        "KIMODO_GPU_BACKEND": backend,
        "CUDA_MODULE_LOADING": os.environ.get("CUDA_MODULE_LOADING", "LAZY"),
        "GRADIO_ANALYTICS_ENABLED": os.environ.get("GRADIO_ANALYTICS_ENABLED", "False"),
        "TOKENIZERS_PARALLELISM": os.environ.get("TOKENIZERS_PARALLELISM", "false"),
        "PYTORCH_CUDA_ALLOC_CONF": os.environ.get(
            "PYTORCH_CUDA_ALLOC_CONF",
            "max_split_size_mb:128,garbage_collection_threshold:0.8",
        ),
    }
    return text_encoder_env, studio_env


def stop_processes(processes: list[subprocess.Popen]) -> None:
    for process in reversed(processes):
        if process.poll() is not None:
            continue
        print(f"[!!] Stopping PID {process.pid}")
        try:
            if os.name == "nt":
                process.send_signal(signal.CTRL_BREAK_EVENT)
                time.sleep(2)
            process.terminate()
        except Exception:
            pass
        try:
            process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            process.kill()


def parse_args() -> tuple[argparse.Namespace, list[str]]:
    parser = argparse.ArgumentParser(description="Start the Nein3D Windows studio.")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--ui-port", type=int, default=DEFAULT_UI_PORT)
    parser.add_argument("--text-encoder-port", type=int, default=DEFAULT_TEXT_ENCODER_PORT)
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--title", default=APP_NAME)
    parser.add_argument("--browser", action="store_true", help="Open in the external browser instead of the app window.")
    parser.add_argument("--no-browser", action="store_true", help="Legacy: do not open the external browser.")
    parser.add_argument("--no-window", action="store_true", help="Start services without opening the desktop app window.")
    parser.add_argument("--detach", action="store_true", help="Start services and exit the launcher.")
    parser.add_argument(
        "--dock",
        choices=["none", "left", "right"],
        default=_default_dock(),
        help="Initial desktop-window docking. Default: right. Use --dock none for centered window.",
    )
    parser.add_argument(
        "--gpu-backend",
        choices=["auto", "cuda", "directml", "xpu", "mps", "cpu"],
        default="auto",
        help="Force a specific GPU backend. 'auto' picks the best available (CUDA -> DirectML -> XPU -> MPS -> CPU).",
    )
    parser.add_argument(
        "--role",
        choices=["textencoder", "studio"],
        default=None,
        help=argparse.SUPPRESS,
    )
    return parser.parse_known_args()


def main() -> int:
    args, role_args = parse_args()

    # Child-role dispatch (used by the bundled exe to host textencoder / studio).
    if args.role:
        return _run_role(args.role, role_args)

    started: list[subprocess.Popen] = []
    try:
        root = find_project_root()
        child_exec = child_executable(root)
        print(f"=== {APP_NAME} Launcher ===")
        print(f"Project: {root}")
        print(f"Runtime: {child_exec}")
        print(f"Frozen:  {_frozen()}")

        if args.gpu_backend == "auto":
            backend, label = detect_gpu_backend(child_exec)
        else:
            backend, label = args.gpu_backend, f"forced:{args.gpu_backend}"
        print(f"GPU:     {backend} ({label})")

        text_encoder_env, studio_env = build_env(
            args.host,
            args.ui_port,
            args.text_encoder_port,
            args.title,
            backend,
        )

        if _frozen():
            te_args = [child_exec, "--role", "textencoder"]
            studio_args = [child_exec, "--role", "studio", "--model", args.model]
        else:
            te_args = [child_exec, "-m", "kimodo.scripts.run_text_encoder_server"]
            studio_args = [child_exec, "-m", "kimodo.demo", "--model", args.model]

        if port_open(args.host, args.text_encoder_port):
            print(f"[OK] Text encoder already running on {args.host}:{args.text_encoder_port}")
        else:
            encoder = start_process(
                root=root,
                name="text encoder",
                args=te_args,
                env=text_encoder_env,
                log_stem="textencoder",
            )
            started.append(encoder)
            wait_for_port(
                host=args.host,
                port=args.text_encoder_port,
                name="Text encoder",
                timeout_seconds=600,
                process=encoder,
            )

        if port_open(args.host, args.ui_port):
            print(f"[OK] Studio already running on {args.host}:{args.ui_port}")
        else:
            studio = start_process(
                root=root,
                name="studio",
                args=studio_args,
                env=studio_env,
                log_stem="studio",
            )
            started.append(studio)
            wait_for_port(
                host=args.host,
                port=args.ui_port,
                name="Studio",
                timeout_seconds=600,
                process=studio,
            )

        url = f"http://{args.host}:{args.ui_port}"
        print(f"[OK] {APP_NAME} is ready: {url}")
        if not args.detach and not args.no_window and not args.browser:
            try:
                open_desktop_window(url, args.title, root=root, dock=args.dock)
            finally:
                stop_processes(started)
            return 0

        if args.browser and not args.no_browser:
            webbrowser.open(url)

        if args.detach:
            return 0

        print("Keep this window open. Press Ctrl+C to stop services started by this launcher.")
        while True:
            alive = [process for process in started if process.poll() is None]
            if started and not alive:
                print("[!!] All started services stopped. Check logs.")
                return 1
            time.sleep(2)
    except KeyboardInterrupt:
        print("\nInterrupted.")
        stop_processes(started)
        return 0
    except Exception as exc:
        print(f"[ERROR] {exc}")
        stop_processes(started)
        if _frozen() and not getattr(args, "detach", False) and sys.stdin.isatty():
            input("Press Enter to close...")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
