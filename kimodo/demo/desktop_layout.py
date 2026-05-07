# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0

from __future__ import annotations

import json
import os
import textwrap
from collections.abc import Mapping

import viser


_TRUE_VALUES = {"1", "true", "yes", "on"}


def desktop_ui_enabled() -> bool:
    """Return True only for the Windows desktop shell.

    Web/browser launches intentionally stay on the stock Viser layout until the
    desktop window is ready enough to replace it everywhere.
    """
    return os.environ.get("NEIN3D_DESKTOP_UI", "").strip().lower() in _TRUE_VALUES


def _track_names(timeline_tracks: Mapping[object, Mapping[str, object]]) -> list[str]:
    names: list[str] = []
    for track in timeline_tracks.values():
        name = track.get("name")
        if isinstance(name, str) and name:
            names.append(name)
    return names


def _object_tree(model_name: str, timeline_tracks: Mapping[object, Mapping[str, object]]) -> list[dict[str, object]]:
    return [
        {
            "label": "Сцена",
            "children": ["Камера", "Сетка пола", "Персонаж", "Скелет", "Таймлайн"],
        },
        {
            "label": "Движение",
            "children": ["Prompt track", *_track_names(timeline_tracks)],
        },
        {
            "label": "Модель",
            "children": [model_name, "SOMA / G1 skeleton", "Локальный кеш"],
        },
        {
            "label": "3D ассеты",
            "children": ["USD loader", "UE-style Manny", "UE-style Quinn", "Greybox тест"],
        },
        {
            "label": "Windows runtime",
            "children": ["Отдельное окно", "GPU cleanup", "CPU text encoder"],
        },
    ]


def _desktop_script(tree: list[dict[str, object]]) -> str:
    script = textwrap.dedent(
        """
        (() => {
          const objectTree = __OBJECT_TREE__;
          const treeTitle = __TREE_TITLE__;
          const panelHints = __PANEL_HINTS__;
          const state = window.__nein3dDesktopLayout || {};
          window.__nein3dDesktopLayout = state;
          state.objectTree = objectTree;

          const STYLE_ID = "nein3d-desktop-layout-style";
          const DOCK_ID = "nein3d-object-tree-dock";
          const PANEL_ATTR = "data-nein3d-desktop-panel";
          const DEFAULT_TIMELINE_HEIGHT = 188;

          function escapeHtml(value) {
            return String(value).replace(/[&<>"']/g, (char) => ({
              "&": "&amp;",
              "<": "&lt;",
              ">": "&gt;",
              '"': "&quot;",
              "'": "&#39;",
            })[char]);
          }

          function setInlineStyle(el, name, value) {
            if (el.style[name] !== value) {
              el.style[name] = value;
            }
          }

          function setCssVar(name, value) {
            if (document.body.style.getPropertyValue(name) !== value) {
              document.body.style.setProperty(name, value);
            }
          }

          function installStyle() {
            let style = document.getElementById(STYLE_ID);
            if (!style) {
              style = document.createElement("style");
              style.id = STYLE_ID;
              document.head.appendChild(style);
            }
            style.textContent = `
              body.nein3d-desktop-ui {
                --nein3d-timeline-height: ${DEFAULT_TIMELINE_HEIGHT}px;
                --nein3d-panel-bottom: calc(var(--nein3d-timeline-height) + 14px);
              }
              #${DOCK_ID} {
                position: fixed;
                left: 14px;
                bottom: var(--nein3d-panel-bottom);
                width: clamp(236px, 18vw, 310px);
                max-height: calc(100vh - var(--nein3d-timeline-height) - 82px);
                overflow: auto;
                z-index: 14;
                box-sizing: border-box;
                color: #f4f7fb;
                background: rgba(26, 27, 30, 0.78);
                border: 1px solid rgba(255, 255, 255, 0.14);
                box-shadow: 0 18px 42px rgba(0, 0, 0, 0.38);
                backdrop-filter: blur(18px) saturate(132%);
                border-radius: 8px;
                padding: 10px;
                font: 12px/1.4 Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
              }
              #${DOCK_ID} .nein3d-tree-title {
                display: flex;
                align-items: center;
                justify-content: space-between;
                gap: 8px;
                margin: 0 0 8px;
                font-size: 13px;
                font-weight: 700;
              }
              #${DOCK_ID} .nein3d-tree-dot {
                width: 8px;
                height: 8px;
                border-radius: 50%;
                background: #23d160;
                box-shadow: 0 0 0 3px rgba(35, 209, 96, 0.15);
              }
              #${DOCK_ID} details {
                border-top: 1px solid rgba(255, 255, 255, 0.08);
                padding: 6px 0;
              }
              #${DOCK_ID} details:first-of-type {
                border-top: 0;
              }
              #${DOCK_ID} summary {
                cursor: default;
                list-style: none;
                font-weight: 650;
                color: #ffffff;
              }
              #${DOCK_ID} summary::-webkit-details-marker {
                display: none;
              }
              #${DOCK_ID} summary::before {
                content: "\\\\25BE";
                display: inline-block;
                width: 14px;
                color: #49d7f2;
              }
              #${DOCK_ID} details:not([open]) summary::before {
                content: "\\\\25B8";
              }
              #${DOCK_ID} ul {
                margin: 4px 0 0 18px;
                padding: 0;
              }
              #${DOCK_ID} li {
                margin: 3px 0;
                color: rgba(244, 247, 251, 0.78);
                white-space: nowrap;
                overflow: hidden;
                text-overflow: ellipsis;
              }
              body.nein3d-desktop-ui [${PANEL_ATTR}="true"] {
                position: fixed !important;
                top: auto !important;
                left: auto !important;
                right: 14px !important;
                bottom: var(--nein3d-panel-bottom) !important;
                max-height: calc(100vh - var(--nein3d-timeline-height) - 82px) !important;
                overflow: auto !important;
                z-index: 14 !important;
                background: rgba(28, 29, 32, 0.82) !important;
                border: 1px solid rgba(255, 255, 255, 0.14) !important;
                box-shadow: 0 18px 42px rgba(0, 0, 0, 0.4) !important;
                backdrop-filter: blur(18px) saturate(132%) !important;
                border-radius: 8px !important;
              }
              @media (max-width: 900px) {
                #${DOCK_ID} {
                  width: 220px;
                }
                body.nein3d-desktop-ui [${PANEL_ATTR}="true"] {
                  max-width: calc(100vw - 270px) !important;
                }
              }
              @media (max-width: 760px) {
                #${DOCK_ID} {
                  display: none;
                }
                body.nein3d-desktop-ui [${PANEL_ATTR}="true"] {
                  left: 12px !important;
                  right: 12px !important;
                  max-width: none !important;
                }
              }
            `;
          }

          function renderDock() {
            let dock = document.getElementById(DOCK_ID);
            if (!dock) {
              dock = document.createElement("aside");
              dock.id = DOCK_ID;
              document.body.appendChild(dock);
            }

            const groups = (state.objectTree || []).map((group) => {
              const children = (group.children || []).map((child) => `<li>${escapeHtml(child)}</li>`).join("");
              return `<details open><summary>${escapeHtml(group.label)}</summary><ul>${children}</ul></details>`;
            }).join("");

            dock.innerHTML = `
              <div class="nein3d-tree-title">
                <span>${escapeHtml(treeTitle)}</span>
                <span class="nein3d-tree-dot" aria-hidden="true"></span>
              </div>
              ${groups}
            `;
          }

          function measureTimelineHeight() {
            const nodes = Array.from(document.querySelectorAll("body *"));
            let best = 0;
            for (const el of nodes) {
              if (!(el instanceof HTMLElement)) continue;
              if (el.closest(`#${DOCK_ID}`)) continue;
              const style = window.getComputedStyle(el);
              if (style.position !== "fixed") continue;
              const rect = el.getBoundingClientRect();
              const touchesBottom = Math.abs(rect.bottom - window.innerHeight) < 8;
              const plausible = rect.height >= 72 && rect.height <= window.innerHeight * 0.55;
              if (!touchesBottom || !plausible) continue;
              const hasCanvas = Boolean(el.querySelector("canvas")) || el.tagName === "CANVAS";
              const hasTimelineText = /Prompts|Full-Body|Left Hand|Right Foot/i.test(el.textContent || "");
              if (hasCanvas || hasTimelineText) {
                best = Math.max(best, rect.height);
              }
            }
            return Math.round(best || DEFAULT_TIMELINE_HEIGHT);
          }

          function findControlPanel(timelineHeight) {
            const nodes = Array.from(document.querySelectorAll("div"));
            const scored = [];
            for (const el of nodes) {
              if (!(el instanceof HTMLElement)) continue;
              if (el.id === DOCK_ID || el.closest(`#${DOCK_ID}`)) continue;
              const rect = el.getBoundingClientRect();
              if (rect.width < 250 || rect.width > 560 || rect.height < 120) continue;
              if (rect.height > window.innerHeight - timelineHeight - 32) continue;
              const style = window.getComputedStyle(el);
              const inlineStyle = el.getAttribute("style") || "";
              const text = (el.textContent || "").slice(0, 1200);
              let score = 0;
              if (style.position === "absolute" || style.position === "fixed") score += 2;
              if (inlineStyle.includes("right") || Math.abs(rect.right - window.innerWidth) < 80) score += 2;
              if (/z-index:\\s*10|z-index:\\s*14/i.test(inlineStyle) || Number(style.zIndex) >= 10) score += 1;
              if (text.includes("Nein3D")) score += 4;
              if (panelHints.some((hint) => text.includes(hint))) score += 4;
              if (el.querySelector("button,input,select,[role='tablist']")) score += 2;
              if (rect.top < Math.max(96, window.innerHeight * 0.25)) score += 1;
              if (score >= 7) scored.push([score, el]);
            }
            scored.sort((a, b) => b[0] - a[0]);
            return scored.length ? scored[0][1] : null;
          }

          function layout() {
            document.body.classList.add("nein3d-desktop-ui");
            const timelineHeight = measureTimelineHeight();
            setCssVar("--nein3d-timeline-height", `${timelineHeight}px`);

            const panel = findControlPanel(timelineHeight);
            document.querySelectorAll(`[${PANEL_ATTR}="true"]`).forEach((candidate) => {
              if (candidate !== panel) candidate.removeAttribute(PANEL_ATTR);
            });
            if (panel) {
              if (panel.getAttribute(PANEL_ATTR) !== "true") {
                panel.setAttribute(PANEL_ATTR, "true");
              }
              setInlineStyle(panel, "position", "fixed");
              setInlineStyle(panel, "top", "auto");
              setInlineStyle(panel, "left", "auto");
              setInlineStyle(panel, "right", "14px");
              setInlineStyle(panel, "bottom", "var(--nein3d-panel-bottom)");
              setInlineStyle(panel, "maxHeight", "calc(100vh - var(--nein3d-timeline-height) - 82px)");
              setInlineStyle(panel, "overflow", "auto");
            }
          }

          function scheduleLayout() {
            if (state.raf) return;
            state.raf = window.requestAnimationFrame(() => {
              state.raf = 0;
              layout();
            });
          }

          installStyle();
          renderDock();
          scheduleLayout();

          if (!state.installed) {
            state.installed = true;
            window.addEventListener("resize", scheduleLayout);
            window.addEventListener("orientationchange", scheduleLayout);
            const observer = new MutationObserver(scheduleLayout);
            observer.observe(document.body, {
              attributes: true,
              childList: true,
              subtree: true,
              attributeFilter: ["class", "style"],
            });
            state.observer = observer;
          }
        })();
        """
    )
    return (
        script.replace("__OBJECT_TREE__", json.dumps(tree, ensure_ascii=True))
        .replace("__TREE_TITLE__", json.dumps("Дерево объектов", ensure_ascii=True))
        .replace(
            "__PANEL_HINTS__",
            json.dumps(
                ["Модель", "Генерация", "Файлы", "Экспорт", "Контроль", "Скриншот", "Видео"],
                ensure_ascii=True,
            ),
        )
    )


def apply_desktop_layout(
    client: viser.ClientHandle,
    *,
    model_name: str,
    timeline_tracks: Mapping[object, Mapping[str, object]],
) -> None:
    if not desktop_ui_enabled():
        return

    try:
        from viser import _messages as _viser_messages

        client.gui._websock_interface.queue_message(
            _viser_messages.RunJavascriptMessage(
                source=_desktop_script(_object_tree(model_name, timeline_tracks)),
            )
        )
    except Exception as exc:
        print(f"[WARN] Failed to apply Nein3D desktop layout: {exc}")
