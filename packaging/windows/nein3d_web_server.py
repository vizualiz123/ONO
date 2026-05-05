from __future__ import annotations

import argparse
import http.server
import os
import socketserver
from pathlib import Path


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 7870


class Nein3DStaticHandler(http.server.SimpleHTTPRequestHandler):
    def end_headers(self) -> None:
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        super().end_headers()

    def log_message(self, format: str, *args) -> None:
        print(f"[web] {self.address_string()} - {format % args}")


class ReusableThreadingTCPServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True


def _candidate_roots() -> list[Path]:
    roots: list[Path] = []
    env_root = os.environ.get("NEIN3D_HOME") or os.environ.get("KIMODO_HOME")
    if env_root:
        roots.append(Path(env_root))
    here = Path(__file__).resolve()
    roots.extend([Path.cwd(), here.parent])
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


def find_web_root() -> Path:
    for root in _candidate_roots():
        web_root = root / "web" / "nein3d_studio"
        if (web_root / "index.html").exists():
            return web_root
    raise RuntimeError("Cannot find web/nein3d_studio. Set NEIN3D_HOME to the project root.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Serve the Nein3D web UI.")
    parser.add_argument("--host", default=os.environ.get("NEIN3D_WEB_HOST", DEFAULT_HOST))
    parser.add_argument("--port", type=int, default=int(os.environ.get("NEIN3D_WEB_PORT", DEFAULT_PORT)))
    parser.add_argument("--web-root", default=os.environ.get("NEIN3D_WEB_ROOT"))
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    web_root = Path(args.web_root).resolve() if args.web_root else find_web_root()
    handler = lambda *handler_args, **handler_kwargs: Nein3DStaticHandler(  # noqa: E731
        *handler_args,
        directory=str(web_root),
        **handler_kwargs,
    )
    with ReusableThreadingTCPServer((args.host, args.port), handler) as httpd:
        print(f"Nein3D web UI: http://{args.host}:{args.port}")
        print(f"Serving: {web_root}")
        httpd.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
