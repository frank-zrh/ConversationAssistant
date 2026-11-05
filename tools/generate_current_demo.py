"""Render a 28-second Chinese or English tour of the current UI using fictional data.

Requires Python 3.10+, Pillow, and Microsoft YaHei or Noto Sans CJK fonts.
Reads repository source/assets only. Never opens the app, user records, settings,
credentials, microphones, network connections, or service clients.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass, replace
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
WIDTH, HEIGHT, SCALE = 1440, 900, 2
STEM = "conversation-assistant-current-zh"
LANGUAGE = "zh-CN"
TRANSLATIONS: dict[str, str] = {}
VISIBLE_TEXT: set[str] = set()
NS = {"ui": "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
      "x": "http://schemas.microsoft.com/winfx/2006/xaml"}
X = "{" + NS["x"] + "}"
SOURCES = (
    "App/MainPage.xaml", "App/Themes/TeamsTheme.xaml", "Core/Localization/UiStrings.zh-CN.resx",
    "App/UI/ConversationHistoryRow.cs", "App/UI/SelectableAnalysisView.cs",
    "Core/Authentication/StartupSignIn.cs", "Core/Conversation/ConversationSessionManager.cs",
    "App/Assets/AppIcon.png",
)

def contains_chinese(value: str) -> bool:
    return any("\u4e00" <= character <= "\u9fff" for character in value)


def translate(value: str) -> str:
    if LANGUAGE == "zh-CN" or not contains_chinese(value):
        return value
    if value not in TRANSLATIONS:
        raise ValueError(f"Missing English demo translation: {value!r}")
    return TRANSLATIONS[value]


def configure_language(language: str) -> None:
    global LANGUAGE, STEM, LABELS, SOURCES, SCENES, TRANSLATIONS
    LANGUAGE = language
    STEM = "conversation-assistant-current-en" if language == "en-US" else "conversation-assistant-current-zh"
    SOURCES, SCENES, LABELS = BASE_SOURCES, BASE_SCENES, CHINESE_LABELS
    TRANSLATIONS = {}
    VISIBLE_TEXT.clear()
    font.cache_clear()
    if language == "en-US":
        from current_demo_english import TRANSLATIONS as english_copy

        SOURCES = (*BASE_SOURCES[:2], "Core/Localization/UiStrings.resx", *BASE_SOURCES[3:], "tools/current_demo_english.py")
        LABELS = {item.attrib["name"]: item.findtext("value", "")
                  for item in ET.parse(ROOT / "Core/Localization/UiStrings.resx").getroot().findall("data")}
        TRANSLATIONS = {value: LABELS[key] for key, value in CHINESE_LABELS.items() if key in LABELS}
        TRANSLATIONS.update(english_copy)
        SCENES = tuple(replace(scene, title=translate(scene.title), caption=translate(scene.caption),
                               cursor=(1280, 230) if scene.key == "products" else scene.cursor) for scene in BASE_SCENES)


LABELS = {item.attrib["name"]: item.findtext("value", "")
          for item in ET.parse(ROOT / SOURCES[2]).getroot().findall("data")}
PALETTE = {item.attrib[X + "Key"]: item.attrib["Color"] for item in
           ET.parse(ROOT / SOURCES[1]).getroot().find(
               "ui:ResourceDictionary.ThemeDictionaries/ui:ResourceDictionary[@x:Key='Default']", NS)}
BG, SURFACE, SUBTLE = (PALETTE[key] for key in ("TeamsCanvasBrush", "TeamsSurfaceBrush", "TeamsSubtleBrush"))
INK, MUTED, STROKE = (PALETTE[key] for key in ("TeamsTextBrush", "TeamsSecondaryTextBrush", "TeamsStrokeBrush"))
ACCENT, GREEN = PALETTE["TeamsAccentBrush"], PALETTE["TeamsSuccessBrush"]
TRANSCRIPT = (
    "我们要为 120 位外勤人员建设移动应用。",
    "用 Power Apps 和 Dataverse 统一数据。",
    "审批希望通过 Power Automate 完成。",
    "还要用 Power BI 查看处理进度。",
    "外部客户能否在 Power Pages 提交申请？",
    "需要核实权限、授权条件和数据安全边界。",
    "请比较上线范围、成本输入与实施风险。",
)


@dataclass(frozen=True)
class Scene:
    key: str
    title: str
    caption: str
    duration_ms: int
    cursor: tuple[int, int]


SCENES = (
    Scene("startup", "窗口先打开，Microsoft 登录在后台进行", "缓存优先；需要授权时再显示提示。本动画不执行真实登录。", 2200, (1280, 75)),
    Scene("live", "实时转写，保留时间与说话人", "只读会话卡片不触发提问；分析与听写互不阻塞。", 3200, (82, 793)),
    Scene("history", "结束会话后，一键导入历史记录", "开始 / 结束各占 20%，意图占 60%；导入保留原始记录。", 3000, (374, 350)),
    Scene("intent", "有用建议与会话意图，一次选择全部", "结论、措辞、要点、依据与意图，共享一个文本选择范围。", 3600, (824, 586)),
    Scene("stages", "阶段总结持续积累，完整选择与复制", "每个阶段保留范围与时间；Select All 覆盖整个页签。", 2600, (849, 230)),
    Scene("questions", "推荐精炼问题，按需获取专业回复", "主动选择推荐问题才获取回复；也可手动输入并提问。", 2800, (906, 298)),
    Scene("products", "产品向导：快速查阅方案与限制", "展示对话相关信息；授权与费用仍需核实官方条款。", 2600, (1194, 230)),
    Scene("answers", "展开回复区域，专注阅读关键细节", "左键保持原有尺寸循环；低频操作收进回复下拉菜单。", 3000, (974, 309)),
    Scene("collapse", "右键一键隐藏，保留恢复入口", "隐藏全部回复内容，把更多空间还给对话分析。", 1800, (974, 309)),
    Scene("newchat", "先保存，再立即切换到新聊天", "会话与工作区同步清空并开始新会话；原记录留在历史中。", 3200, (422, 236)),
)

BASE_SCENES, BASE_SOURCES, CHINESE_LABELS = SCENES, SOURCES, LABELS


def beats(scene: Scene) -> list[tuple[float, int]]:
    custom = {
        "live": [(0, 400), (.33, 600), (.66, 800), (1, 1400)],
        "history": [(0, 200), (.2, 200), (.45, 1000), (.65, 800), (1, 800)],
        "intent": [(0, 150), (.3, 150), (.6, 1800), (1, 1500)],
        "stages": [(0, 100), (.33, 100), (.66, 900), (1, 1500)],
        "collapse": [(0, 100), (.3, 100), (.6, 500), (1, 1100)],
        "newchat": [(0, 150), (.3, 1300), (.6, 350), (1, 1400)],
    }
    result = custom.get(scene.key, [(0, 100), (.33, 100), (.66, 100), (1, scene.duration_ms - 300)])
    if sum(duration for _, duration in result) != scene.duration_ms:
        raise ValueError(f"Invalid scene timing: {scene.key}")
    return result


def font_paths() -> tuple[Path, Path]:
    candidates = (
        (Path("C:/Windows/Fonts/segoeui.ttf"), Path("C:/Windows/Fonts/seguisb.ttf")),
        (Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
         Path("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf")),
    ) if LANGUAGE == "en-US" else (
        (Path("C:/Windows/Fonts/msyh.ttc"), Path("C:/Windows/Fonts/msyhbd.ttc")),
        (Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"),
         Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc")),
    )
    for regular, bold in candidates:
        if regular.is_file() and bold.is_file():
            return regular, bold
    raise RuntimeError("Install Segoe UI/DejaVu Sans for English or Microsoft YaHei/Noto Sans CJK for Chinese.")


@lru_cache(maxsize=64)
def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(font_paths()[int(bold)]), size * SCALE)


class Canvas:
    def __init__(self) -> None:
        self.image = Image.new("RGB", (WIDTH * SCALE, HEIGHT * SCALE), BG)
        self.draw = ImageDraw.Draw(self.image)

    def box(self, rect, fill=SURFACE, outline=STROKE, radius=8):
        self.draw.rounded_rectangle(tuple(round(value * SCALE) for value in rect),
                                    radius=radius * SCALE, fill=fill, outline=outline, width=SCALE)

    def line(self, points, fill=STROKE, width=1):
        self.draw.line([(round(x * SCALE), round(y * SCALE)) for x, y in points], fill=fill, width=width * SCALE)

    def text(self, x, y, value, size=16, color=INK, bold=False):
        value = translate(value)
        VISIBLE_TEXT.add(value)
        bounds = self.draw.textbbox((x * SCALE, y * SCALE), value, font=font(size, bold), anchor="lt")
        if bounds[0] < 0 or bounds[1] < 0 or bounds[2] > WIDTH * SCALE or bounds[3] > HEIGHT * SCALE:
            raise ValueError(f"Text exceeds canvas: {value}")
        self.draw.text((x * SCALE, y * SCALE), value, font=font(size, bold), fill=color, anchor="lt")

    def para(self, x, y, value, width, size=16, color=INK, bold=False, bottom=750, selected=False):
        value = translate(value)
        lines = []
        for paragraph in value.split("\n"):
            line = ""
            units = paragraph.split(" ") if LANGUAGE == "en-US" else paragraph
            for unit in units:
                candidate = (line + " " + unit).strip() if LANGUAGE == "en-US" else line + unit
                if line and self.draw.textlength(candidate, font=font(size, bold)) > width * SCALE:
                    lines.append(line)
                    line = unit
                else:
                    line = candidate
            lines.append(line)
        for line in lines:
            if y + size + 5 > bottom:
                raise ValueError(f"Text exceeds its UI region: {value}")
            if selected and line:
                length = self.draw.textlength(line, font=font(size, bold)) / SCALE
                self.box((x - 3, y - 2, x + length + 4, y + size + 4), ACCENT, None, 2)
            self.text(x, y, line, size, SURFACE if selected else color, bold)
            y += size + 7
        return y

    def button(self, rect, label, primary=False, disabled=False, size=14):
        label = translate(label)
        self.box(rect, ACCENT if primary and not disabled else SUBTLE,
                 ACCENT if primary and not disabled else STROKE, 6)
        x, y, right, bottom = rect
        length = self.draw.textlength(label, font=font(size)) / SCALE
        while LANGUAGE == "en-US" and length > right - x - 12 and size > 12:
            size -= 1
            length = self.draw.textlength(label, font=font(size)) / SCALE
        if length > right - x - 12:
            raise ValueError(f"Button label is too wide: {label}")
        self.text(x + (right - x - length) / 2, y + (bottom - y - size) / 2, label, size,
                  MUTED if disabled else SURFACE if primary else INK)

    def finish(self) -> Image.Image:
        return self.image.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)


def tabs(c: Canvas, x, y, labels, selected, widths):
    for index, (label, width) in enumerate(zip(labels, widths)):
        c.text(x + 10, y + 9, label, 16, ACCENT if index == selected else MUTED, index == selected)
        if index == selected:
            c.line([(x + 8, y + 35), (x + width - 8, y + 35)], ACCENT, 3)
        x += width


def toggle(c: Canvas, rect, expanded=False):
    c.button(rect, "")
    middle = (rect[0] + rect[2]) / 2
    y = (rect[1] + rect[3]) / 2
    offset = 3 if expanded else -3
    c.line([(middle - 6, y - offset), (middle, y + offset), (middle + 6, y - offset)], INK, 2)


def transcript(c: Canvas, count: int):
    for index, value in enumerate(TRANSCRIPT[:count]):
        y = 216 + index * 72
        c.box((34, y, 550, y + 64), SURFACE, STROKE, 8)
        c.text(46, y + 9, f"14:00:{4 + index * 8:02d}", 12, MUTED)
        c.text(146, y + 9, f"说话人 {index % 2 + 1}", 13, MUTED, True)
        c.para(46, y + 31, value, 490, 16, bottom=y + 63)


def history(c: Canvas, hovered: bool):
    c.button((34, 178, 160, 210), LABELS["HistoryRefresh"])
    c.text(174, 187, "共 3 条历史记录，按开始时间倒序", 12, MUTED)
    c.para(34, 224, LABELS["HistoryHint"], 510, 12, color=MUTED, bottom=274)
    left, top, width = 34, 280, 516
    columns = (width * .2, width * .2, width * .6)
    c.box((left, top, left + width, top + 32), SUBTLE, None, 3)
    x = left
    for label, column in zip(("HistoryColumnStartTime", "HistoryColumnEndTime", "HistoryIntent"), columns):
        c.text(x + 8, top + 8, LABELS[label], 14, bold=True)
        x += column
    rows = (
        ("2026-10-26\n14:00", "2026-10-26\n14:01", "外勤应用上线：核实授权条件、数据边界与实施风险。"),
        ("2026-10-25\n10:00", "2026-10-25\n10:35", "审批流程：比较人员职责、异常处理与交付范围。"),
        ("2026-10-24\n15:10", "2026-10-24\n15:45", "数据治理：确认环境隔离、安全角色与 DLP 策略。"),
    )
    for index, row in enumerate(rows):
        y = top + 40 + index * 100
        c.box((left, y, left + width, y + 90), "#EBEBFA" if hovered and index == 0 else SURFACE, STROKE, 4)
        x = left
        for column_index, (value, column) in enumerate(zip(row, columns)):
            c.para(x + 8, y + 13, value, column - 16, 12 if column_index < 2 else 15,
                   color=MUTED if column_index < 2 else INK, bottom=y + 88)
            x += column


def intent(c: Canvas, selected=False, empty=False):
    x, y = 608, 268
    if empty:
        c.text(x, y, LABELS["AssistanceHeader"], 18, bold=True)
        c.para(x, y + 34, LABELS["AssistanceWaiting"], 780, 16, color=MUTED)
        c.text(x, y + 102, LABELS["IntentSummaryHeader"], 18, bold=True)
        c.para(x, y + 138, LABELS["IntentEmpty"], 780, 16, color=MUTED)
        return
    entries = (
        ("此刻有用", 18, True), ("针对当前讨论（模拟）", 13, False),
        ("先确认上线范围，再比较授权与治理条件。", 17, True),
        ("可以这样说", 15, True), ("这次上线是否同时包含部署、培训与权限配置？", 16, False),
        ("• 区分应用使用者、审批者与外部客户。", 16, False),
        ("• 授权费用取决于身份、连接器及容量。", 16, False),
        ("需要确认", 15, True), ("审批参与者权限与外部访问范围仍待核实。", 16, False),
        ("依据与参考", 15, True), ("虚构会话第 1–7 段；未自动打开外部链接。", 14, False),
        ("会话意图", 18, True), ("形成可验证的外勤应用上线、授权与治理方案。", 16, False),
        ("已保存的分析覆盖 7/7 段发言（模拟）", 12, False),
    )
    for value, size, strong in entries:
        y = c.para(x, y, value, 780, size, bold=strong, bottom=701, selected=selected) + 6


def stages(c: Canvas, selected=False):
    y = 270
    for caption, summary in (
        ("分析 #1 · 10-26 14:00 · 整体会话", "目标明确：以移动应用统一外勤数据，并接入审批与进度分析。"),
        ("分析 #2 · 10-26 14:01 · 整体会话", "新增边界：区分应用用户、审批参与者与外部客户，核实权限和授权条件。"),
        ("分析 #3 · 10-26 14:01 · 整体会话", "下一步：整理连接器、容量和数据隔离假设，再确认上线计划与风险。"),
    ):
        y = c.para(608, y, caption, 780, 14, color=MUTED, bold=True, selected=selected) + 14
        y = c.para(608, y, summary, 780, 17, selected=selected) + 30


def questions(c: Canvas, answered: bool):
    for index, value in enumerate(("哪些用户需要核实高级授权？", "如何划分审批职责与数据访问？", "DLP 会限制哪些连接器组合？")):
        y = 266 + index * 78
        c.box((606, y, 1406, y + 68), SUBTLE, ACCENT if index == 0 and answered else STROKE, 6)
        c.text(618, y + 12, value, 17, bold=index == 0 and answered)
        c.text(618, y + 42, "已分析 · 点击查看已有结果" if index == 0 and answered else "未分析 · 主动选择后请求", 12, MUTED)


def products(c: Canvas):
    c.text(608, 269, "Power Apps 与 Dataverse", 19, bold=True)
    c.text(608, 302, "围绕当前场景核实能力、使用者与治理边界。", 16)
    c.box((608, 337, 1404, 370), SUBTLE, STROKE, 3)
    c.text(620, 347, "检查项", 14, bold=True)
    c.text(828, 347, "需确认的条件（示例）", 14, bold=True)
    for index, (name, value) in enumerate((
        ("连接器边界", "逐项核实高级连接器与环境策略"),
        ("使用者身份", "区分完整应用用户与审批参与者"),
        ("数据安全", "确认安全角色、环境隔离与 DLP 策略"),
    )):
        y = 370 + index * 37
        c.box((608, y, 1404, y + 37), SURFACE, STROKE, 0)
        c.text(620, y + 11, name, 15)
        c.text(828, y + 11, value, 15)


def answer(c: Canvas, expanded: bool):
    top = 290 if expanded else 534
    c.box((580, top, 1420, 760), SURFACE, STROKE, 10)
    toggle(c, (594, top + 12, 1352, top + 44), expanded)
    c.button((1362, top + 12, 1406, top + 44), "···")
    c.button((596, top + 54, 1406, top + 88), "哪些用户需要核实高级授权？                 v", size=15)
    c.text(608, top + 98, "已完成 · 演示回复", 12, GREEN)
    if not expanded:
        c.text(608, top + 124, "建议回复", 17, bold=True)
        c.para(608, top + 154, "先列出身份、连接器和容量，再核对适用的官方条款。", 780, 16, bottom=744)
        return
    c.text(608, top + 128, "建议回复", 19, bold=True)
    c.para(608, top + 163, "先明确谁在使用应用、谁只参与审批、谁是外部客户；\n再根据连接器、容量和数据访问条件核实适用条款。", 780, 16, bottom=500)
    c.text(608, top + 224, "关键检查项", 17, bold=True)
    for index, (label, value) in enumerate((
        ("身份", "应用使用者 / 审批参与者 / 外部客户"),
        ("技术范围", "连接器、Dataverse、环境与容量"),
        ("决策依据", "以当前官方条款和实际方案为准"),
    )):
        y = top + 256 + index * 40
        c.box((608, y, 1404, y + 40), SUBTLE if index % 2 == 0 else SURFACE, STROKE, 0)
        c.text(620, y + 12, label, 15, bold=True)
        c.text(828, y + 12, value, 15)
    c.text(608, top + 392, "虚构示例；不是已核实的授权、价格或客户承诺。", 14, MUTED)


def overflow(c: Canvas):
    c.box((324, 208, 552, 512), SURFACE, "#BDBDCB", 8)
    labels = ("NewChat", "CopyTranscript", "ClearTranscript", "CreateGroup", "SaveNow", "ViewSession", "OpenRecordsFolder")
    for index, key in enumerate(labels):
        y = 217 + index * 40
        if index == 0:
            c.box((330, y, 546, y + 36), "#EBEBFA", None, 4)
        c.text(344, y + 10, LABELS[key], 15, ACCENT if index == 0 else INK, index == 0)


def cursor(c: Canvas, position, previous, phase, right_click=False):
    ease = min(1, phase / .6)
    ease = ease * ease * (3 - 2 * ease)
    x = previous[0] + (position[0] - previous[0]) * ease
    y = previous[1] + (position[1] - previous[1]) * ease
    if phase >= .6:
        c.draw.ellipse(((x - 15) * SCALE, (y - 15) * SCALE, (x + 15) * SCALE, (y + 15) * SCALE),
                       outline=ACCENT, width=2 * SCALE)
    c.draw.polygon([(x * SCALE, y * SCALE), ((x + 4) * SCALE, (y + 22) * SCALE),
                    ((x + 11) * SCALE, (y + 15) * SCALE), ((x + 20) * SCALE, (y + 14) * SCALE)],
                   fill=INK, outline=SURFACE, width=SCALE)
    if right_click:
        c.box((x + 24, y - 6, x + (124 if LANGUAGE == "en-US" else 80), y + 20), SURFACE, ACCENT, 4)
        c.text(x + 31, y - 1, "右键", 13, ACCENT, True)


def cursor_target(scene: Scene, phase: float):
    if scene.key == "history":
        return (178, 791) if phase < .2 else (302 if LANGUAGE == "en-US" else 222, 146) if phase < .45 else (374, 350)
    if scene.key == "collapse" and phase >= .6:
        return (974, 736)
    if scene.key == "newchat" and phase == 1:
        return (535, 192)
    return scene.cursor


def render(scene: Scene, index: int, phase: float, previous) -> Image.Image:
    c = Canvas()
    fresh = scene.key == "newchat" and phase == 1
    empty = scene.key == "startup" or fresh
    analysis_ready = not empty and (scene.key != "live" or phase == 1)
    history_visible = scene.key == "history" and .2 <= phase < 1
    expanded = scene.key == "answers" or scene.key == "collapse" and phase < .6
    normal_answer = scene.key == "products" or scene.key == "questions" and phase >= .66
    listening = scene.key == "live" or fresh
    c.box((0, 0, WIDTH, 38), SURFACE, None, 0)
    with Image.open(ROOT / "App/Assets/AppIcon.png") as icon:
        c.image.paste(icon.resize((24 * SCALE, 24 * SCALE), Image.Resampling.LANCZOS), (12 * SCALE, 7 * SCALE),
                      icon.resize((24 * SCALE, 24 * SCALE), Image.Resampling.LANCZOS))
    c.text(46, 11, LABELS["AppTitle"], 14)
    c.text(1050, 12, "功能演示 · 虚构内容", 12, MUTED)
    c.text(1301, 8, "—      □      ×", 15, MUTED)
    c.text(20, 52, LABELS["AppTitle"], 21, bold=True)
    c.text(20, 84, LABELS["StatusListening"] if listening else LABELS["StatusIdle"], 14, GREEN if listening else MUTED, True)
    c.text(142, 84, "麦克风 + 系统音频" if listening else "音频已停止", 14, MUTED)
    c.text(354, 84, "00:00" if empty else "00:58", 14, MUTED)
    c.text(994, 60, "演示帐号（模拟）", 14, MUTED)
    c.button((1188, 49, 1354, 85), LABELS["SigningIn"] if scene.key == "startup" and phase < 1 else LABELS["SignedIn"])
    c.button((1364, 49, 1406, 85), "")
    for angle in range(0, 360, 45):
        radians = math.radians(angle)
        dx, dy = math.cos(radians), math.sin(radians)
        c.line([(1385 + dx * 6, 67 + dy * 6), (1385 + dx * 11, 67 + dy * 11)], MUTED, 2)
    c.draw.ellipse((1379 * SCALE, 61 * SCALE, 1391 * SCALE, 73 * SCALE), outline=MUTED, width=2 * SCALE)
    c.box((20, 116, 564, 760), SURFACE, STROKE, 10)
    tabs(c, 32, 127, [LABELS["LiveTranscript"], LABELS["HistoryTitle"]], int(history_visible),
         [220, 140] if LANGUAGE == "en-US" else [140, 140])
    if history_visible:
        history(c, phase >= .65)
    else:
        c.text(36, 185, "最近保存于 14:01" if not empty else "新会话 · 自动保存", 12, MUTED)
        c.button((386, 175, 492, 207), LABELS["PauseScrolling"])
        c.button((502, 175, 548, 207), "···")
        count = 0 if empty else (0 if phase == 0 else 2 if phase < .5 else 4 if phase < 1 else 7) if scene.key == "live" else 7
        transcript(c, count)
    analysis_bottom = 278 if expanded else 522 if normal_answer else 700
    c.box((580, 116, 1420, analysis_bottom), SURFACE, STROKE, 10)
    c.text(596, 132, LABELS["QuestionsHeader"], 17, bold=True)
    c.button((1252, 126, 1406, 160), LABELS["AnalyzeConversation"])
    c.button((596, 171, 1406, 205), LABELS["AnalysisHistory"] if not analysis_ready else "分析 #3 · 10-26 14:01 · 整体会话                    v", size=14)
    tab = 1 if scene.key == "stages" else 2 if scene.key in ("questions", "answers", "collapse") else 3 if scene.key == "products" else 0
    tabs(c, 596, 214, [LABELS[key] for key in ("IntentTab", "StageSummariesTab", "RecommendedQuestionsTab", "ProductGuideTab")],
         tab, [200, 170, 230, 170] if LANGUAGE == "en-US" else [210, 140, 150, 150])
    if not expanded:
        if not analysis_ready:
            intent(c, empty=True)
        elif tab == 1:
            stages(c, selected=phase == 1)
        elif tab == 2:
            questions(c, answered=normal_answer or scene.key == "collapse")
        elif tab == 3:
            products(c)
        else:
            intent(c, selected=scene.key == "intent" and phase == 1)
    if expanded or normal_answer:
        answer(c, expanded)
    else:
        c.box((580, 712, 1420, 760), SURFACE, STROKE, 10)
        toggle(c, (596, 720, 1406, 752))
    c.button((20, 777, 152, 813), LABELS["StartConversation"], primary=True, disabled=listening)
    for x, kind in ((162, "stop"), (206, "pause"), (250, "resume")):
        c.button((x, 777, x + 34, 813), "", disabled=not listening or kind == "resume")
        color = INK if listening and kind != "resume" else MUTED
        if kind == "stop":
            c.box((x + 12, 789, x + 22, 800), color, None, 1)
        elif kind == "pause":
            c.line([(x + 13, 789), (x + 13, 801)], color, 3)
            c.line([(x + 22, 789), (x + 22, 801)], color, 3)
        else:
            c.draw.polygon([((x + 12) * SCALE, 789 * SCALE), ((x + 12) * SCALE, 801 * SCALE),
                            ((x + 24) * SCALE, 795 * SCALE)], fill=color)
    c.box((296, 777, 1316, 813), SURFACE, STROKE, 6)
    c.text(308, 787, "手动输入问题（Ctrl+Enter）", 15, MUTED)
    c.button((1326, 777, 1420, 813), LABELS["Ask"], primary=True)
    if scene.key == "newchat" and phase < 1:
        overflow(c)
    c.box((0, 824, WIDTH, HEIGHT), SURFACE, None, 0)
    c.text(22, 835, scene.title, 24, bold=True)
    c.text(22, 873, scene.caption, 14, MUTED)
    c.text(1244, 837, f"{index + 1:02d} / {len(SCENES):02d}", 20, ACCENT, True)
    c.text(1340, 874, "28 秒循环", 12, MUTED)
    c.line([(20, 896), (1420, 896)], STROKE, 3)
    c.line([(20, 896), (20 + 1400 * (index + phase) / len(SCENES), 896)], ACCENT, 3)
    cursor(c, cursor_target(scene, phase), previous, min(1, phase / .3) if scene.key == "newchat" else phase,
           scene.key == "collapse" and phase >= .6)
    return c.finish()


def source_fingerprints() -> dict[str, str]:
    return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in SOURCES}


def validate_current_ui() -> None:
    page = ET.parse(ROOT / "App/MainPage.xaml").getroot()
    named = {item.attrib[X + "Name"]: item for item in page.iter() if X + "Name" in item.attrib}
    for name in ("NewChatMenuItem", "IntentDocument", "StageSummaryDocument", "TranscriptActionsButton", "AnswerActionsButton"):
        if name not in named:
            raise ValueError(f"Current UI contract changed; update the demo: {name}")
    widths = [item.attrib["Width"] for item in named["HistoryColumnsHeader"].find("ui:Grid.ColumnDefinitions", NS)]
    if widths != ["2*", "2*", "6*"]:
        raise ValueError("History must be illustrated with the current 20/20/60 layout")
    transcript_template = next(item for item in page.iter() if item.get(X + "Key") == "TranscriptTemplate")
    if transcript_template[0].tag.rsplit("}", 1)[-1] != "Border" or any(
        key in ("Click", "RightTapped") for item in transcript_template.iter() for key in item.attrib
    ):
        raise ValueError("This storyboard requires passive transcript cards")
    if named["AnswerPanelToggleButton"].get("RightTapped") != "AnswerPanelToggle_RightTapped":
        raise ValueError("Answer-panel collapse behavior changed; update the demo")
    if sum(scene.duration_ms for scene in SCENES) != 28_000:
        raise ValueError("All transitions must fit within exactly 28 seconds")


def validate(output: Path) -> None:
    manifest = json.loads((output / f"{STEM}.json").read_text(encoding="utf-8"))
    validate_current_ui()
    if manifest["source_fingerprints"] != source_fingerprints():
        raise ValueError("The app sources have changed; regenerate this current-version demo")
    if manifest["simulation"] is not True or manifest["language"] != LANGUAGE:
        raise ValueError("Simulation and language metadata are required")
    if LANGUAGE == "en-US" and (not manifest.get("visible_text") or contains_chinese(
        json.dumps([manifest["scenes"], manifest["visible_text"]], ensure_ascii=False)
    )):
        raise ValueError("The English demo must not contain untranslated Chinese text")
    if manifest["history_column_ratios"] != [.2, .2, .6] or sum(
        scene["duration_ms"] for scene in manifest["scenes"]
    ) != manifest["duration_ms"]:
        raise ValueError("Storyboard timing or current history-column proportions changed")
    path = output / f"{STEM}.gif"
    with Image.open(path) as gif:
        if gif.format != "GIF" or gif.size != (WIDTH, HEIGHT) or gif.info.get("loop") != 0:
            raise ValueError("Invalid GIF format, dimensions or looping")
        duration = 0
        for index in range(gif.n_frames):
            gif.seek(index)
            gif.load()
            delay = gif.info.get("duration", 0)
            if delay < 100:
                raise ValueError("Frame delays must avoid browser minimum-delay clamping")
            duration += delay
        if duration != manifest["duration_ms"] or not 0 < duration < 30_000 or gif.n_frames <= len(SCENES):
            raise ValueError(f"Invalid encoded playback duration: {duration}")
        print(f"Validated {gif.n_frames} decoded frames, {duration / 1000:.1f}s, {WIDTH}x{HEIGHT}, {path.stat().st_size / 1024 / 1024:.2f} MiB")
    for suffix, size in (("preview", (WIDTH, HEIGHT)), ("storyboard", (WIDTH, HEIGHT * 5 // 2))):
        with Image.open(output / f"{STEM}-{suffix}.png") as image:
            if image.size != size:
                raise ValueError(f"Invalid {suffix} dimensions")
            image.verify()


def generate(output: Path) -> None:
    validate_current_ui()
    output.mkdir(parents=True, exist_ok=True)
    frames, durations, stills = [], [], []
    previous = SCENES[0].cursor
    for index, scene in enumerate(SCENES):
        for phase, duration in beats(scene):
            frames.append(render(scene, index, phase, previous))
            durations.append(duration)
        preview_phase = .45 if scene.key == "history" else .3 if scene.key == "newchat" else .6 if scene.key == "intent" else 1
        stills.append(render(scene, index, preview_phase, previous))
        previous = cursor_target(scene, 1)
    sheet = Image.new("RGB", (WIDTH, HEIGHT * 5 // 2), BG)
    for index, image in enumerate(stills):
        sheet.paste(image.resize((WIDTH // 2, HEIGHT // 2), Image.Resampling.LANCZOS),
                    ((index % 2) * WIDTH // 2, (index // 2) * HEIGHT // 2))
    palette = sheet.resize((720, 1125), Image.Resampling.LANCZOS).quantize(colors=240, method=Image.Quantize.MEDIANCUT)
    indexed = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in frames]
    indexed[0].save(output / f"{STEM}.gif", save_all=True, append_images=indexed[1:], duration=durations,
                    loop=0, optimize=True, disposal=1,
                    comment=f"Fictional {LANGUAGE} UI simulation. No live services, user data or verified licensing/prices.".encode("ascii"))
    stills[3].save(output / f"{STEM}-preview.png", optimize=True)
    sheet.save(output / f"{STEM}-storyboard.png", optimize=True)
    manifest = {
        "simulation": True, "language": LANGUAGE, "size": [WIDTH, HEIGHT], "duration_ms": sum(durations),
        "visible_text": sorted(VISIBLE_TEXT),
        "history_column_ratios": [.2, .2, .6], "timestamp_format": "yyyy-MM-dd HH:mm",
        "font": font_paths()[0].name, "source_fingerprints": source_fingerprints(),
        "scenes": [dict(asdict(scene), beats=[{"phase": phase, "duration_ms": duration} for phase, duration in beats(scene)])
                   for scene in SCENES],
    }
    (output / f"{STEM}.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    validate(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "docs/demos")
    parser.add_argument("--language", choices=("zh-CN", "en-US"), default="zh-CN")
    parser.add_argument("--check", action="store_true", help="Validate existing exports without modifying them")
    args = parser.parse_args()
    configure_language(args.language)
    validate(args.output) if args.check else generate(args.output)
