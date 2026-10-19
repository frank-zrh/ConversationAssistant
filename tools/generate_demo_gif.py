"""Render an English-only, fictional Conversation Assistant product tour.

Requires Python 3.10+ and Pillow. No network, Speech, Work IQ or app settings access.
"""
from __future__ import annotations

import argparse
import json
import math
from dataclasses import asdict, dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
WIDTH, HEIGHT = 1440, 900
BG = "#e9edf5"
INK = "#20263b"
MUTED = "#606c85"
ACCENT = "#6350c9"
BORDER = "#dce1ed"
WHITE = "#ffffff"
GREEN = "#168166"
PRODUCTS = ["Power Apps", "Power Automate", "Power BI", "Power Pages", "Copilot Studio", "Dataverse"]
LANGUAGES = {"listening": "English (en-US)", "analysis": "English", "display": "English"}
TRANSCRIPT = [
    ("00:04", "Speaker 1", "We need a Power Apps solution for 120 field technicians using Dataverse."),
    ("00:12", "Speaker 2", "Power Automate will route approvals. Some users only approve requests."),
    ("00:20", "Speaker 1", "Power BI needs to show service performance to regional managers."),
    ("00:28", "Speaker 2", "Customers should check requests through Power Pages, outside our tenant."),
    ("00:36", "Speaker 1", "Could Copilot Studio answer status questions using the same business data?"),
    ("00:44", "Speaker 2", "Check premium connector rights, external access and Dataverse capacity."),
    ("00:52", "Speaker 1", "Compare license dependencies and DLP boundaries before proposing a design."),
]
QUESTIONS = [
    "Which Power Apps users need premium rights?",
    "Do approval-only users need a Power Automate license?",
    "What changes Power BI sharing eligibility?",
    "How should Power Pages external access be costed?",
    "Which DLP policies affect Copilot Studio actions?",
]


@dataclass(frozen=True)
class Scene:
    key: str
    title: str
    caption: str
    hold_ms: int = 3000
    count: int = 7
    tab: str = "Intent"
    cursor: tuple[int, int] = (1333, 157)


SCENES = [
    Scene("welcome", "A Power Platform meeting, with useful answers close at hand",
          "Fictional scenario: field service, approvals, analytics, customer portals and an agent.", 2800, 0),
    Scene("languages", "Three independent languages. All English for this tour.",
          "Listening: English (en-US). Analysis: English. Display: English. Settings stay off the workspace.", 4000, 0),
    Scene("azure", "Configure Azure Speech with an organizational identity",
          "Endpoint, tenant, application client ID and audio consent; no resource key. Values shown are placeholders.", 4200, 0),
    Scene("offline", "Or explicitly select Whisper offline recognition",
          "Local speech and speaker models are provisioned ahead of time. Work IQ remains a separate online service.", 3200, 0),
    Scene("audio", "Hear both sides through shared-mode audio",
          "Choose microphone, system audio or both; match Teams devices, refresh endpoints and review privacy.", 4000, 0),
    Scene("preferences", "Set context and answer style without taking workspace height",
          "Automatic analysis is enabled. Audio storage is off; readable conversation JSON stays on this PC.", 4000, 0),
    Scene("signin", "Sign in once; keep service credentials separate",
          "Simulated sign-in only. Speech uses Entra authorization; Work IQ uses its own account-bound CLI session.", 2600, 0, cursor=(1225, 157)),
    Scene("start", "Start Conversation", "English recognition begins; no question is sent just because speech is detected.", 1800, 0, cursor=(128, 764)),
    Scene("listen1", "Capture the Power Apps and automation requirements",
          "Final transcript cards retain speaker labels; partial speech stays separate until finalized.", 2400, 2, cursor=(300, 425)),
    Scene("listen2", "Keep listening while the scenario gets richer",
          "Power BI, Power Pages and Copilot Studio enter the same conversation.", 2800, 5, cursor=(310, 555)),
    Scene("listen3", "Seven final utterances trigger automatic analysis",
          "The complete conversation supports overall intent; the current stage provides focused new context.", 2800, 7, cursor=(1250, 223)),
    Scene("group", "Group and rename the licensing discussion",
          "Create a transcript group, collapse or expand it, then rename it or analyze only that group.", 3300, cursor=(480, 359)),
    Scene("intent", "Intent: understand the decision, not just the words",
          "The whole conversation informs goals, constraints and open decisions. Unsupported assumptions stay explicit.", 3600, cursor=(638, 316)),
    Scene("stages", "Stage summaries build a useful meeting history",
          "Each successful analysis appends only new stage content. Select earlier analyses without sending a request.", 3400, tab="Stage summaries", cursor=(795, 316)),
    Scene("questions", "Difficult questions. Simple, clickable wording.",
          "Recommend hard-to-remember rules and tradeoffs. Keep background in hover details, not long question labels.", 3800, tab="Recommended questions", cursor=(850, 410)),
    Scene("question_hint", "Hover for context; click for a reference answer",
          "A click asks Work IQ. A right-click fills the question box so the user can edit before asking.", 3000, tab="Recommended questions", cursor=(914, 411)),
    Scene("guide_apps", "Product guide: Power Apps and Dataverse",
          "Only conversation-relevant facts. Licensing and limitations are tables, without a reference-link appendix.", 5000, tab="Product guide", cursor=(1258, 316)),
    Scene("guide_automate", "Product guide: Power Automate",
          "Separate user eligibility, flow ownership, connector dependencies and capacity assumptions.", 4300, tab="Product guide", cursor=(1258, 316)),
    Scene("guide_bi", "Product guide: Power BI",
          "Show sharing prerequisites and capacity choices as conditions to verify, not an unconditional promise.", 4300, tab="Product guide", cursor=(1258, 316)),
    Scene("guide_pages", "Product guide: Power Pages",
          "External identities, access scope and usage inputs belong beside the cost model.", 4300, tab="Product guide", cursor=(1258, 316)),
    Scene("guide_copilot", "Product guide: Copilot Studio",
          "Agent actions, data boundaries and consumption assumptions are relevant to this customer scenario.", 4300, tab="Product guide", cursor=(1258, 316)),
    Scene("answer", "Ask the selected question while listening continues",
          "Questions and answers have their own history. The conversation does not stop for Work IQ.", 3700, tab="Recommended questions", cursor=(825, 408)),
    Scene("expand", "Expand the answer panel for the detail that matters",
          "Read the conclusion, a small comparison table and concrete next checks; copy, retry or dismiss locally.", 4500, tab="Recommended questions", cursor=(985, 353)),
    Scene("original", "Inspect the original answer, then return to rich view",
          "Raw Markdown remains available when needed. Copy Answer preserves the response text.", 3100, tab="Recommended questions", cursor=(943, 695)),
    Scene("edit", "Edit a recommendation or ask manually",
          "Right-click a question or transcript card to prefill the question box. Ctrl+Enter submits the edited request.", 3400, tab="Recommended questions", cursor=(900, 765)),
    Scene("retry", "Malformed analysis? Recover automatically.",
          "Illustrated internal flow: retry schema validation up to three attempts. No partial analysis is committed.", 3200, tab="Stage summaries", cursor=(1180, 580)),
    Scene("recovered", "Recovery succeeds without asking the user to reanalyze",
          "Only a validated result updates intent, stage summaries, recommendations and product guides.", 2700, tab="Stage summaries", cursor=(1180, 580)),
    Scene("pause", "Pause listening without losing the workspace",
          "Existing transcripts, analyses and answers remain available. Recognition settings can now be changed.", 2800, tab="Recommended questions", cursor=(358, 765)),
    Scene("resume", "Resume listening in English",
          "Audio sources reopen with the selected devices. Analysis and display languages remain English.", 2400, tab="Recommended questions", cursor=(466, 765)),
    Scene("save", "Save the conversation as readable JSON",
          "Transcripts, groups, analysis, product guides and replies are retained. No audio or credentials are saved.", 3100, tab="Stage summaries", cursor=(300, 278)),
    Scene("end", "End the conversation; keep the reference workspace",
          "End stops capture but leaves the conversation visible. Copy, inspect or export the saved results.", 2300, tab="Product guide", cursor=(248, 765)),
    Scene("import", "Import a record without restarting listening or Work IQ",
          "A saved JSON record opens as a separate local copy. Inspect history, then explicitly ask for new analysis.", 3300, tab="Stage summaries", cursor=(201, 278)),
    Scene("finish", "Less configuration chrome. More room for the conversation.",
          "English throughout. Power Platform scenario. Simulated UI and responses; no live service calls or real quotes.", 3500, tab="Product guide", cursor=(1330, 157)),
]


def fonts() -> dict[tuple[int, bool], ImageFont.FreeTypeFont]:
    pairs = [(Path("C:/Windows/Fonts/segoeui.ttf"), Path("C:/Windows/Fonts/seguisb.ttf")),
             (Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"), Path("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"))]
    for regular, bold in pairs:
        if regular.is_file() and bold.is_file():
            return {(size, strong): ImageFont.truetype(str(bold if strong else regular), size)
                    for size in (13, 14, 15, 16, 17, 18, 20, 22, 24, 26) for strong in (False, True)}
    raise RuntimeError("Install Segoe UI or DejaVu Sans before rendering the demo.")


FONTS = fonts()


class Canvas:
    def __init__(self) -> None:
        self.image = Image.new("RGB", (WIDTH, HEIGHT), BG)
        self.draw = ImageDraw.Draw(self.image)

    def box(self, rect, fill=WHITE, outline=BORDER, radius=10):
        self.draw.rounded_rectangle(rect, radius=radius, fill=fill, outline=outline)

    def text(self, xy, value, size=17, color=INK, bold=False):
        self.draw.text(xy, value, font=FONTS[size, bold], fill=color)

    def para(self, xy, value, width, size=17, color=INK, bold=False, max_lines=None):
        font = FONTS[size, bold]
        lines = []
        for paragraph in value.split("\n"):
            line = ""
            for word in paragraph.split():
                candidate = f"{line} {word}".strip()
                if self.draw.textlength(candidate, font=font) > width:
                    if not line:
                        raise ValueError(f"Unbreakable word exceeds {width}px: {word}")
                    lines.append(line)
                    line = word
                else:
                    line = candidate
            lines.append(line)
        if max_lines is not None and len(lines) > max_lines:
            raise ValueError(f"Text exceeds {max_lines} lines: {value}")
        x, y = xy
        for line in lines:
            self.text((x, y), line, size, color, bold)
            y += size + 7
        return y

    def button(self, rect, label, primary=False, selected=False, disabled=False, size=15):
        color = ACCENT if primary else "#eeeafb" if selected else "#f7f8fc"
        self.box(rect, color, ACCENT if selected or primary else BORDER, 6)
        left, top, right, bottom = rect
        font = FONTS[size, False]
        length = self.draw.textlength(label, font=font)
        if length > right - left - 12:
            raise ValueError(f"Button label too wide: {label}")
        self.text((left + (right - left - length) / 2, top + (bottom - top - size) / 2 - 3),
                  label, size, MUTED if disabled else WHITE if primary else INK)

    def table(self, x, y, widths, headings, rows, size=16):
        for row_number, row in enumerate([headings, *rows]):
            font = FONTS[size, row_number == 0]
            # Measure with the same wrapping algorithm used below.
            heights = []
            for value, width in zip(row, widths):
                count, line = 1, ""
                for word in value.split():
                    candidate = f"{line} {word}".strip()
                    if self.draw.textlength(candidate, font=font) > width - 20:
                        count += 1
                        line = word
                    else:
                        line = candidate
                heights.append(count * (size + 7) + 14)
            height = max(heights)
            cell_x = x
            for value, width in zip(row, widths):
                self.draw.rectangle((cell_x, y, cell_x + width, y + height),
                                    fill="#eeeafb" if row_number == 0 else WHITE, outline=BORDER)
                self.para((cell_x + 10, y + 7), value, width - 20, size,
                          bold=row_number == 0)
                cell_x += width
            y += height
        return y


def settings(c: Canvas, key: str):
    overlay = Image.new("RGBA", c.image.size, (30, 35, 65, 40))
    c.image = Image.alpha_composite(c.image.convert("RGBA"), overlay).convert("RGB")
    c.draw = ImageDraw.Draw(c.image)
    c.box((835, 185, 1390, 744), WHITE, "#b4abdb", 12)
    c.text((859, 200), "Settings", 22, bold=True)
    c.text((859, 233), "Simulated values • English throughout", 15, MUTED)
    c.draw.line((859, 265, 1365, 265), fill=BORDER)
    if key == "languages":
        pairs = [("Listening language", "English (en-US)"), ("Analysis language", "English"), ("Display language", "English")]
        y = 286
        for label, value in pairs:
            c.text((861, y + 10), label, 18)
            c.button((1077, y, 1364, y + 43), value + "   v", selected=True, size=17)
            y += 67
        c.para((861, 509), "Three independent controls. Listening selects recognition; analysis selects new output; display selects interface labels.", 478, 18)
        c.para((861, 625), "Pause listening before changing recognition language. All three stay English in this demo.", 478, 17, MUTED)
    elif key == "azure":
        items = [("Speech provider", "Azure AI Services / Speech"), ("Endpoint", "https://demo-resource.cognitiveservices.azure.com/"),
                 ("Tenant ID", "00000000-0000-0000-0000-000000000000"), ("Client ID (demo placeholder)", "11111111-1111-1111-1111-111111111111")]
        y = 279
        for label, value in items:
            c.text((861, y), label, 15, MUTED)
            c.box((861, y + 24, 1363, y + 63), "#f7f8fc", radius=5)
            c.text((870, y + 31), value, 15)
            y += 79
        c.text((861, 600), "[x] Allow selected audio to Azure Speech (demo)", 17)
        c.button((861, 642, 1062, 681), "Save Speech Settings")
        c.button((1073, 642, 1363, 681), "Test connection (simulated)")
        c.text((861, 704), "Entra identity • RBAC required • no resource key", 15, MUTED)
    elif key == "offline":
        c.text((861, 289), "Speech provider", 17, MUTED)
        c.button((861, 321, 1362, 368), "Whisper offline", selected=True, size=18)
        c.text((861, 394), "Local model readiness (simulated)", 18, bold=True)
        for i, value in enumerate(["[x] Whisper speech model", "[x] Local speaker segmentation model", "[x] Speaker embedding model"]):
            c.text((861, 435 + i * 37), value, 18, GREEN)
        c.para((861, 571), "No audio upload in offline mode. Provision models explicitly; opening settings does not download them.", 480, 18)
        c.para((861, 667), "Tour returns to Azure mode before capture starts.", 480, 16, MUTED)
    elif key == "audio":
        items = [("Audio sources (shared mode)", "Microphone + system audio"), ("Input device", "Headset microphone (demo)"), ("Output device", "Headphones / Teams playback (demo)")]
        y = 282
        for label, value in items:
            c.text((861, y), label, 16, MUTED)
            c.button((861, y + 28, 1362, y + 68), value + "   v", size=17)
            y += 88
        c.button((861, 558, 1090, 598), "Refresh devices")
        c.button((1102, 558, 1362, 598), "Microphone privacy")
        c.para((861, 621), "Alternatives: microphone only or system audio only. Shared mode does not change Teams settings. Playback capture includes other apps; obtain consent.", 478, 17, MUTED)
    else:
        items = [("Context window", "Medium • 3 minutes"), ("Answer style", "Concise"),
                 ("Automatic analysis", "On • 7 final turns or 5 minutes"), ("Store audio", "Off (fixed)"),
                 ("Store transcript / JSON", "On (fixed)"), ("Clear after conversation", "Off (fixed)")]
        for i, (label, value) in enumerate(items):
            y = 281 + i * 55
            c.text((861, y + 8), label, 16)
            c.button((1083, y, 1362, y + 40), value, size=14)
        c.para((861, 622), "Web grounding: off. Records are readable JSON. Protect transcripts and workplace content; no audio or credentials are stored.", 478, 17, MUTED)
        c.button((861, 699, 1100, 735), "Open Record Folder")
    c.draw.rounded_rectangle((1375, 278, 1380, 710), radius=2, fill="#edf0f6")
    c.draw.rounded_rectangle((1375, 278, 1380, 375), radius=2, fill="#aaa4c4")


def transcript(c: Canvas, scene: Scene, phase: float):
    c.box((44, 197, 568, 730))
    c.text((60, 215), "LIVE TRANSCRIPT", 17, bold=True)
    for rect, label in [((315, 208, 386, 242), "Copy"), ((393, 208, 463, 242), "Clear"), ((470, 208, 551, 242), "Pause scroll")]:
        c.button(rect, label, size=13)
    c.button((60, 256, 167, 290), "New Group", size=14)
    c.button((174, 256, 285, 290), "Import JSON", size=14, selected=scene.key == "import")
    c.button((292, 256, 389, 290), "Save Now", size=14, selected=scene.key == "save")
    c.text((402, 264), "Saved locally", 13, MUTED)
    if scene.count == 0:
        c.para((84, 407), "Ready for the Power Platform conversation", 415, 22, bold=True)
        c.para((84, 489), "Final speech appears here with anonymous speaker labels. No audio is written to disk.", 415, 18, MUTED)
        return
    grouped = scene.key not in ("listen1", "listen2", "listen3")
    y = 308
    if grouped:
        c.box((60, y, 551, y + 43), "#eeeafb", radius=7)
        c.text((73, y + 10), "v  Power Platform • licensing & governance", 17, ACCENT, True)
        y += 54
    visible = list(TRANSCRIPT[:scene.count])[-4:]
    for i, (time, speaker, message) in enumerate(visible):
        last = i == len(visible) - 1
        if scene.key in ("listen1", "listen2", "listen3") and last:
            words = message.split()
            message = " ".join(words[:max(3, math.ceil(len(words) * (0.35 + phase * 0.65)))])
        fill = "#edf5fd" if speaker == "Speaker 1" else "#f3effb"
        c.box((60, y, 551, y + 76), fill, radius=8)
        c.text((73, y + 7), time + "     " + speaker, 13, MUTED)
        c.para((73, y + 29), message, 465, 16, max_lines=2)
        y += 84
    c.text((66, 704), "Paused" if scene.key == "pause" else "English (en-US) • speaker labels preserved", 14, MUTED)
    if scene.key == "group":
        c.box((228, 365, 540, 508), WHITE, "#b4abdb")
        for i, value in enumerate(["Rename Group", "Analyze This Group with Work IQ", "Collapse group"]):
            c.text((244, 382 + i * 38), value, 16, ACCENT if i == 1 else INK)


GUIDES = {
    "guide_apps": ("Power Apps + Dataverse", "Field service for 120 technicians; distinguish usage rights from storage capacity.",
                   [("Technician access", "Verify premium rights per user", "120 x applicable rate"), ("Dataverse storage", "Check tenant capacity + add-ons", "Included + overage inputs")],
                   [("Connector rights", "Verify exact connector + plan", "Do not assume M365 coverage"), ("Data capacity", "Database / file / log quotas", "Size before commitments")]),
    "guide_automate": ("Power Automate", "Route approvals without assuming every participant has identical licensing needs.",
                       [("Flow ownership", "Verify user / process eligibility", "Applicable plan + flow inputs"), ("Approval-only use", "Verify actor rights + connectors", "Unknown until terms checked")],
                       [("Premium connectors", "Confirm flow and user context", "Validate before rollout"), ("Request capacity", "Confirm scope and thresholds", "Model peak approval demand")]),
    "guide_bi": ("Power BI", "Regional managers need secure, shareable service-performance reports.",
                 [("Creators / viewers", "Check rights for each audience", "Eligible users x rate"), ("Capacity option", "Verify sharing prerequisites", "Capacity + required licenses")],
                 [("Report sharing", "Workspace / capacity conditions", "Check viewer eligibility"), ("Refresh / model size", "Plan-specific service limits", "Test expected workload")]),
    "guide_pages": ("Power Pages", "Customers outside the tenant need a bounded request-status portal.",
                    [("External audience", "Verify access and identity model", "Applicable usage x rate"), ("Capacity / usage", "Confirm meter + included units", "Included + excess usage")],
                    [("Dataverse exposure", "Check table permissions + roles", "Least-privilege access"), ("Usage assumptions", "Identity / site / period scope", "Validate before estimating")]),
    "guide_copilot": ("Copilot Studio", "Let a customer-facing agent retrieve status through controlled actions.",
                      [("Agent consumption", "Verify current meter and scope", "Usage x applicable rate"), ("Connected actions", "Check downstream service rights", "Agent + dependency costs")],
                      [("Data / action policy", "Environment DLP and identity", "Review every connector"), ("External access", "Channel + authentication rules", "Test authorization boundary")]),
}


def product_guide(c: Canvas, key: str):
    selected = key if key in GUIDES else "guide_apps"
    name, overview, pricing, limits = GUIDES[selected]
    c.text((608, 355), name, 22, ACCENT, True)
    y = c.para((608, 385), overview, 750, 16) + 5
    c.text((608, y), "LICENSE / PRICE • illustrative model, not a quote", 16, bold=True)
    y = c.table(608, y + 26, [181, 303, 266], ["Item", "Condition to verify", "Cost input"], pricing, 15)
    c.text((608, y + 10), "LIMITATIONS • verify current product terms", 16, bold=True)
    y = c.table(608, y + 36, [181, 303, 266], ["Boundary", "Scope / prerequisite", "Decision impact"], limits, 15)
    if y > 705:
        raise ValueError(f"Product table overflows workspace: {key}, {y}")
    c.text((608, 710), "Rates unverified • no reference-link appendix • cached card", 14, MUTED)


def answer(c: Canvas, key: str):
    expanded = key in ("expand", "original")
    top = 367 if expanded else 515
    c.box((590, top, 1396, 730))
    c.button((608, top + 10, 1377, top + 39), "v  Collapse answer" if expanded else "^  Expand answer to 3/4", size=14)
    c.text((609, top + 51), "WORK IQ • QUESTION REPLIES", 15, MUTED, True)
    c.text((609, top + 78), "Which Power Apps users need premium rights?", 18, bold=True)
    if key == "original":
        c.box((608, top + 112, 1378, 674), "#f6f5fa")
        c.para((624, top + 125), "## SUGGESTED ANSWER\nValidate entitlements by user role and the actual app, data source and connector combination.\n\n| Role | Check |\n| Technician | App and Dataverse rights |\n| Approver | Flow and connector context |", 718, 17)
    elif expanded:
        c.para((609, top + 113), "Do not assume every participant has the same rights. Validate the app, Dataverse access and connected flow context for each role.", 748, 18)
        c.table(609, top + 177, [230, 518], ["Role", "Verification needed"],
                [("120 technicians", "App + Dataverse rights for the actual deployment"), ("Approvers / managers", "Approval context; Power BI sharing eligibility")], 16)
        c.text((609, 656), "Next: verify current terms before committing a plan or price.", 16, MUTED)
    else:
        c.para((609, top + 112), "Check entitlements by role, app and connector context. Do not assume M365 licenses cover this design. Expand for the comparison.", 748, 17)
    y = 691
    for rect, label in [((608, y, 747, y + 29), "Copy Answer"), ((756, y, 827, y + 29), "Retry"),
                        ((836, y, 921, y + 29), "Dismiss"), ((930, y, 1100, y + 29), "Rich view" if key == "original" else "View original")]:
        c.button(rect, label, size=14)


def workspace(c: Canvas, scene: Scene):
    answer_open = scene.key in ("answer", "expand", "original", "edit")
    expanded = scene.key in ("expand", "original")
    bottom = 357 if expanded else 503 if answer_open else 730
    c.box((590, 197, 1396, bottom))
    c.text((608, 215), "WORK IQ • CONVERSATION ANALYSIS", 17, bold=True)
    c.button((1201, 207, 1378, 244), "Analyze all chat", size=15)
    history = "Analysis 2 • Licensing group" if scene.key in ("stages", "retry", "recovered", "save", "import") else "Analysis 1 • All chat"
    if scene.count == 0:
        history = "Select an analysis"
    c.button((608, 258, 1378, 294), history + "                         v", size=15)
    tabs = [("Intent", 100), ("Stage summaries", 177), ("Recommended questions", 246), ("Product guide", 175)]
    x = 607
    for label, width in tabs:
        selected = label == scene.tab
        c.text((x + 8, 310), label, 15, ACCENT if selected else MUTED, selected)
        if selected:
            c.draw.rounded_rectangle((x + 7, 337, x + width - 9, 340), radius=1, fill=ACCENT)
        x += width
    if expanded:
        return answer(c, scene.key)
    if scene.count == 0 or scene.key in ("start", "listen1", "listen2"):
        c.para((655, 415), "Intent, stage summaries, recommendations and product guides appear after analysis.", 652, 22, MUTED)
        c.para((655, 519), "Click a recommendation to request its answer. Listening alone never sends a question.", 652, 18, MUTED)
        return
    if scene.tab == "Product guide":
        product_guide(c, scene.key)
    elif scene.tab == "Recommended questions":
        count = 2 if answer_open else 5
        for i, question in enumerate(QUESTIONS[:count]):
            y = 355 + i * (61 if answer_open else 65)
            c.box((608, y, 1378, y + 53), "#f7f6fc", radius=7)
            c.text((623, y + 13), question, 18, bold=True)
        if scene.key == "question_hint":
            c.box((739, 433, 1347, 541), "#24283e", "#24283e", 8)
            c.para((756, 445), "Check user-role, connector and entitlement dependencies before promising coverage. One clear question; expert detail stays here.", 571, 17, WHITE)
        if not answer_open:
            c.text((611, 700), "Left-click: ask / view answer     Right-click: edit before sending", 15, MUTED)
    elif scene.tab == "Stage summaries":
        for i, (title, body) in enumerate([
            ("Stage 1 • Architecture fit", "Field service on Power Apps + Dataverse; approvals, reports, a portal and a status agent."),
            ("Stage 2 • Licensing & governance", "Verify role-specific rights, premium connectors, external identities, capacity and DLP boundaries.")]):
            y = 357 + i * 123
            c.box((608, y, 1378, y + 110), "#f7f8fc")
            c.text((625, y + 13), title, 18, bold=True)
            c.para((625, y + 45), body, 715, 18)
        if scene.key in ("retry", "recovered"):
            color = "#fff4dc" if scene.key == "retry" else "#e3f5ee"
            c.box((608, 613, 1378, 705), color)
            c.text((626, 626), "ILLUSTRATED INTERNAL RECOVERY" if scene.key == "retry" else "Validated result • workspace updated", 17, bold=True)
            c.para((626, 655), "Attempt 1: invalid JSON  >  automatic attempt 2  >  validate" if scene.key == "retry"
                   else "Attempt 2 succeeded. No manual reanalysis. No partial result committed.", 718, 16)
        else:
            c.text((612, 686), "Analysis history is local; selecting an older entry sends no request.", 16, MUTED)
    else:
        c.text((611, 361), "A governed Power Platform field-service solution", 22, bold=True)
        for i, (label, body) in enumerate([
            ("GOAL", "120 technicians need field service, approvals, reporting, a customer portal and a status agent."),
            ("CONSTRAINTS", "External users, premium connectors, data capacity and environment-level governance."),
            ("OPEN DECISION", "Confirm role-specific entitlements and cost inputs before proposing the deployment.")]):
            y = 414 + i * 89
            c.text((611, y), label, 14, ACCENT, True)
            c.para((611, y + 24), body, 745, 18)
    if answer_open:
        answer(c, scene.key)


def record_overlay(c: Canvas, key: str):
    c.box((665, 524, 1367, 707), "#eef8f3", "#8dcdb4", 10)
    c.text((686, 540), "Local record saved" if key == "save" else "Imported as a new local copy", 22, GREEN, True)
    c.text((686, 580), "power-platform-discovery.demo.json", 18, bold=True)
    c.para((686, 614), "Transcript • groups • analyses • product guides • answers\nNo audio or credentials. Protect this readable file." if key == "save"
           else "Capture: stopped. No Work IQ requests resumed.\nHistory and linked answers remain available for inspection.", 647, 18)


def render(scene: Scene, index: int, phase: float, previous_cursor: tuple[int, int]):
    c = Canvas()
    c.text((30, 14), "CONVERSATION ASSISTANT  /  POWER PLATFORM TOUR", 14, ACCENT, True)
    c.text((30, 39), scene.title, 26, bold=True)
    c.para((30, 79), scene.caption, 1374, 17, MUTED, max_lines=2)
    c.box((24, 123, 1416, 812), WHITE, "#c8cfdf", 12)
    c.text((45, 136), "Conversation Assistant", 22, bold=True)
    status = "Ready" if scene.count == 0 else "Paused" if scene.key == "pause" else "Ended" if scene.key in ("end", "import", "finish") else "Listening"
    c.draw.ellipse((337, 148, 346, 157), fill=GREEN if status == "Listening" else MUTED)
    c.text((355, 141), status, 17, GREEN if status == "Listening" else MUTED)
    c.text((462, 141), "Microphone + system audio • Azure Speech", 16, MUTED)
    c.text((837, 141), "00:00" if scene.count == 0 else "01:12", 17, MUTED)
    c.button((1051, 136, 1265, 174), "Microsoft sign-in" if index < 6 else "Microsoft • signed in", size=15)
    c.button((1275, 136, 1395, 174), "Settings", selected=scene.key in ("languages", "azure", "offline", "audio", "preferences"))
    transcript(c, scene, phase)
    workspace(c, scene)
    c.draw.line((44, 740, 1395, 740), fill=BORDER)
    c.button((44, 750, 205, 788), "Start Conversation", primary=scene.key in ("welcome", "start"))
    c.button((213, 750, 302, 788), "End", selected=scene.key == "end")
    c.button((310, 750, 420, 788), "Pause", selected=scene.key == "pause")
    c.button((428, 750, 538, 788), "Resume", selected=scene.key == "resume")
    c.box((548, 750, 1290, 788), "#f8f9fc", radius=6)
    manual = "For 120 Power Apps technicians, which license inputs must we verify?" if scene.key == "edit" else "Ask manually... (Ctrl+Enter)"
    c.text((559, 758), manual, 16, INK if scene.key == "edit" else MUTED)
    c.button((1300, 750, 1395, 788), "Ask", primary=scene.key == "edit")
    if scene.key in ("languages", "azure", "offline", "audio", "preferences"):
        settings(c, scene.key)
    if scene.key in ("save", "import"):
        record_overlay(c, scene.key)
    c.text((31, 830), f"{index + 1:02d} / {len(SCENES):02d}   SIMULATED DEMO • NOT A LIVE RECORDING", 14, MUTED, True)
    c.text((785, 830), "Listening: EN-US   |   Analysis: English   |   Display: English", 16, ACCENT, True)
    c.draw.rounded_rectangle((31, 866, 1408, 872), radius=3, fill="#d9deeb")
    c.draw.rounded_rectangle((31, 866, 31 + int(1377 * (index + 1) / len(SCENES)), 872), radius=3, fill=ACCENT)
    # Four positions per scene create real pointer animation rather than a slide-only GIF.
    ease = phase * phase * (3 - 2 * phase)
    x = previous_cursor[0] + (scene.cursor[0] - previous_cursor[0]) * ease
    y = previous_cursor[1] + (scene.cursor[1] - previous_cursor[1]) * ease
    if phase == 1:
        c.draw.ellipse((x - 17, y - 17, x + 17, y + 17), outline="#a795e4", width=2)
    c.draw.polygon([(x, y), (x + 5, y + 23), (x + 11, y + 16), (x + 20, y + 15)], fill=INK, outline=WHITE)
    return c.image


def generate(output: Path):
    output.mkdir(parents=True, exist_ok=True)
    frames, durations, stills = [], [], []
    previous = SCENES[0].cursor
    for index, scene in enumerate(SCENES):
        for phase in (0.0, 0.33, 0.66, 1.0):
            image = render(scene, index, phase, previous)
            frames.append(image)
            durations.append(scene.hold_ms if phase == 1.0 else 100)
            if phase == 1.0:
                stills.append(image)
        previous = scene.cursor
    # One global palette keeps UI colors consistent, with no dither on small text.
    sample = Image.new("RGB", (480 * 6, 300 * 6), BG)
    for index, image in enumerate(stills):
        sample.paste(image.resize((480, 300), Image.Resampling.LANCZOS), ((index % 6) * 480, (index // 6) * 300))
    palette = sample.quantize(colors=240, method=Image.Quantize.MEDIANCUT)
    indexed = [image.quantize(palette=palette, dither=Image.Dither.NONE) for image in frames]
    destination = output / "conversation-assistant-power-platform.gif"
    indexed[0].save(destination, save_all=True, append_images=indexed[1:], duration=durations,
                    loop=0, optimize=True, disposal=1, comment=b"Simulated UI. English only. Fictional scenario; no verified prices or live service calls.")
    stills[16].save(output / "conversation-assistant-power-platform-preview.png", optimize=True)
    sample.save(output / "conversation-assistant-power-platform-storyboard.png", optimize=True)
    manifest = {"simulation": True, "languages": LANGUAGES, "size": [WIDTH, HEIGHT],
                "duration_ms": sum(durations), "products": PRODUCTS, "scenes": [asdict(scene) for scene in SCENES]}
    (output / "conversation-assistant-power-platform.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    validate(destination, manifest)


def validate(path: Path, manifest: dict):
    with Image.open(path) as gif:
        assert gif.format == "GIF" and gif.size == (WIDTH, HEIGHT)
        assert gif.info.get("loop") == 0 and gif.n_frames > len(SCENES)
        duration = 0
        for index in range(gif.n_frames):
            gif.seek(index)
            gif.load()
            duration += gif.info.get("duration", 0)
        assert duration == manifest["duration_ms"], (duration, manifest["duration_ms"])
        print(f"Validated {path}: {gif.n_frames} frames, {duration / 1000:.1f}s, {WIDTH}x{HEIGHT}, {path.stat().st_size / 1024 / 1024:.2f} MiB")
    assert manifest["languages"] == {"listening": "English (en-US)", "analysis": "English", "display": "English"}
    for scene in SCENES:
        assert not any("\u4e00" <= character <= "\u9fff" for character in scene.title + scene.caption)
    assert set(GUIDES) <= {scene.key for scene in SCENES}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "docs" / "demos")
    args = parser.parse_args()
    generate(args.output)
