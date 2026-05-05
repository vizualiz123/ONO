from __future__ import annotations

import argparse
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
DEFAULT_WEB_PORT = 7870
DEFAULT_TEXT_ENCODER_PORT = 9550
DEFAULT_MODEL = "kimodo-soma-seed"


def _app_dir() -> Path:
    if getattr(sys, "frozen", False):
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


def build_env(host: str, ui_port: int, text_encoder_port: int, title: str) -> tuple[dict[str, str], dict[str, str]]:
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
        "KIMODO_DARK_MODE": "true",
        "KIMODO_AGGRESSIVE_GPU_CLEANUP": "true",
        "KIMODO_TEXT_ENCODER_CPU": "true",
        "PYTORCH_CUDA_ALLOC_CONF": os.environ.get(
            "PYTORCH_CUDA_ALLOC_CONF",
            "max_split_size_mb:128",
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


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Start the Nein3D local studio.")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--ui-port", type=int, default=DEFAULT_UI_PORT)
    parser.add_argument("--web-port", type=int, default=DEFAULT_WEB_PORT)
    parser.add_argument("--text-encoder-port", type=int, default=DEFAULT_TEXT_ENCODER_PORT)
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--title", default=APP_NAME)
    parser.add_argument("--no-browser", action="store_true")
    parser.add_argument("--detach", action="store_true", help="Start services and exit the launcher.")
    parser.add_argument("--legacy-ui", action="store_true", help="Open the backend Viser UI instead of the web DCC UI.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    started: list[subprocess.Popen] = []
    try:
        root = find_project_root()
        py = python_exe(root)
        print(f"=== {APP_NAME} Launcher ===")
        print(f"Project: {root}")
        print(f"Python:  {py}")

        text_encoder_env, studio_env = build_env(
            args.host,
            args.ui_port,
            args.text_encoder_port,
            args.title,
        )

        if port_open(args.host, args.text_encoder_port):
            print(f"[OK] Text encoder already running on {args.host}:{args.text_encoder_port}")
        else:
            encoder = start_process(
                root=root,
                name="text encoder",
                args=[str(py), "-m", "kimodo.scripts.run_text_encoder_server"],
                env=text_encoder_env,
                log_stem="textencoder",
            )
            started.append(encoder)
            wait_for_port(
                host=args.host,
                port=args.text_encoder_port,
                name="Text encoder",
                timeout_seconds=360,
                process=encoder,
            )

        if port_open(args.host, args.ui_port):
            print(f"[OK] Studio already running on {args.host}:{args.ui_port}")
        else:
            studio = start_process(
                root=root,
                name="studio",
                args=[str(py), "-m", "kimodo.demo", "--model", args.model],
                env=studio_env,
                log_stem="studio",
            )
            started.append(studio)
            wait_for_port(
                host=args.host,
                port=args.ui_port,
                name="Studio",
                timeout_seconds=420,
                process=studio,
            )

        if port_open(args.host, args.web_port):
            print(f"[OK] Web UI already running on {args.host}:{args.web_port}")
        else:
            web = start_process(
                root=root,
                name="web UI",
                args=[
                    str(py),
                    str(root / "packaging" / "windows" / "nein3d_web_server.py"),
                    "--host",
                    args.host,
                    "--port",
                    str(args.web_port),
                ],
                env={},
                log_stem="web",
            )
            started.append(web)
            wait_for_port(
                host=args.host,
                port=args.web_port,
                name="Web UI",
                timeout_seconds=45,
                process=web,
            )

        engine_url = f"http://{args.host}:{args.ui_port}"
        web_url = f"http://{args.host}:{args.web_port}/?engine={engine_url}"
        url = engine_url if args.legacy_ui else web_url
        print(f"[OK] {APP_NAME} is ready: {url}")
        if not args.no_browser:
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
        if getattr(sys, "frozen", False):
            input("Press Enter to close...")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
