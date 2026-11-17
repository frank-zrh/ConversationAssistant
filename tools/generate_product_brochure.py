"""Generate the three-page Chinese product brochure from repository demo assets."""

from __future__ import annotations

import argparse
import io
import json
import os
from pathlib import Path

import pymupdf as pdf
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "docs" / "product"
PDF_NAME = "ConversationAssistant-产品介绍.pdf"
WIDTH, HEIGHT = 595.28, 841.89
MARGIN = 36
CONTENT = WIDTH - 2 * MARGIN
INK = "182C47"
MUTED = "53667C"
BLUE = "365AC7"
TEAL = "007F80"
PALE = "F1F5FC"
LINE = "DCE4EF"
WHITE = "FFFFFF"


def rgb(value: str) -> tuple[float, float, float]:
    return tuple(int(value[i:i + 2], 16) / 255 for i in (0, 2, 4))


def find_font(explicit: str | None) -> Path:
    candidates = [Path(explicit)] if explicit else [
        Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts/msyh.ttc",
        Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"),
    ]
    for path in candidates:
        if path.is_file():
            return path
    raise FileNotFoundError("A Chinese font is required; supply --font with a CJK font path.")


class Brochure:
    def __init__(self, font_path: Path):
        self.doc = pdf.open()
        self.font_path = font_path
        self.font = pdf.Font(fontfile=str(font_path))
        for character in "会话应用场景功能隐私":
            if not self.font.has_glyph(ord(character)):
                raise ValueError(f"Font lacks Chinese glyph: {character}")
        self.page = None

    def box(self, x, y, width, height, fill=PALE, border=None):
        rect = pdf.Rect(x, y, x + width, y + height)
        if not pdf.Rect(0, 0, WIDTH, HEIGHT).contains(rect):
            raise ValueError(f"Drawing outside page: {rect}")
        self.page.draw_rect(rect, color=rgb(border) if border else None,
                            fill=rgb(fill), width=0.6, overlay=True)

    def text(self, value, x, y, width, size=10, color=INK, leading=None, bottom=772):
        """Wrap using embedded-font advances; y is the top of the text area."""
        leading = leading or size * 1.55
        lines = []
        for paragraph in value.split("\n"):
            line = ""
            for char in paragraph:
                if self.font.text_length(line + char, fontsize=size) > width and line:
                    lines.append(line)
                    line = char
                else:
                    line += char
            lines.append(line)
        for line in lines:
            if self.font.text_length(line, fontsize=size) > width + 0.1:
                raise ValueError(f"Text exceeds width: {line}")
            baseline = y + size * 1.12
            if baseline + size * 0.3 > bottom:
                raise ValueError(f"Text exceeds area: {value[:40]} at {y}")
            self.page.insert_text((x, baseline), line, fontname="BrochureCJK",
                                  fontsize=size, color=rgb(color))
            y += leading
        return y

    def new_page(self, number, section):
        self.page = self.doc.new_page(width=WIDTH, height=HEIGHT)
        self.page.insert_font(fontname="BrochureCJK", fontfile=str(self.font_path))
        self.box(0, 0, WIDTH, 7, BLUE)
        self.text("CONVERSATION ASSISTANT", MARGIN, 23, 340, 9, BLUE)
        self.text(section, 420, 23, 140, 9, MUTED)
        self.page.draw_line((MARGIN, 786), (WIDTH - MARGIN, 786), color=rgb(LINE))
        self.text("Conversation Assistant  |  产品介绍", MARGIN, 798, 410,
                  8, MUTED, bottom=830)
        self.text(f"0{number} / 03", WIDTH - MARGIN - 48, 798, 48,
                  8, BLUE, bottom=830)

    def section(self, label, y):
        self.box(MARGIN, y + 3, 3, 15, TEAL)
        self.text(label, MARGIN + 12, y, CONTENT - 12, 13)

    def card(self, x, y, width, height, number, title, question, detail, result):
        self.box(x, y, width, height, PALE, LINE)
        self.text(number, x + 13, y + 10, 29, 16, BLUE)
        self.text(title, x + 48, y + 12, width - 61, 12)
        cursor = self.text(question, x + 13, y + 40, width - 26, 10, TEAL,
                           bottom=y + height - 10)
        cursor = self.text(detail, x + 13, cursor + 6, width - 26, 9, MUTED,
                           bottom=y + height - 10)
        self.text(result, x + 13, cursor + 7, width - 26, 9, BLUE,
                  bottom=y + height - 10)

    def overview(self, demo):
        self.new_page(1, "产品定位 / OVERVIEW")
        self.text("让每一段对话，都更有方向", MARGIN, 57, CONTENT, 25)
        self.text("Conversation Assistant · Windows 桌面会话辅助工具",
                  MARGIN, 101, CONTENT, 12, BLUE)
        self.text("把正在发生的讨论转成可读记录，再结合上下文提供意图、阶段总结、\n推荐问题与参考回答，帮助你听清需求、推进讨论、回看关键信息。",
                  MARGIN, 128, CONTENT, 10.5, MUTED)
        y = 184
        self.page.insert_image(pdf.Rect(MARGIN, y, WIDTH - MARGIN, y + CONTENT * 900 / 1440),
                               stream=demo)
        self.text("界面示意：复用仓库中文演示素材；对话、登录与回答均为虚构，非实机截图。",
                  MARGIN, 518, CONTENT, 8, MUTED)
        self.section("一个工作台，连接听写、理解与回答", 552)
        gap = 12
        width = (CONTENT - gap * 2) / 3
        for i, (title, body) in enumerate([
            ("01  听清对话", "共享式获取麦克风与系统声音；\n不独占设备，可与其它应用并行。"),
            ("02  看懂重点", "查看会话意图与阶段总结；\n发现未回答的问题和后续方向。"),
            ("03  有据回应", "主动提问或选择推荐问题；\n查看参考答案与来源 / 上下文。"),
        ]):
            x = MARGIN + i * (width + gap)
            self.box(x, 584, width, 100, PALE, LINE)
            self.text(title, x + 12, 597, width - 24, 11, BLUE)
            self.text(body, x + 12, 622, width - 24, 9, MUTED, bottom=678)
        self.box(MARGIN, 706, CONTENT, 53, "E9F5F3")
        self.text("适合谁？", MARGIN + 13, 716, 90, 11, TEAL)
        self.text("客户顾问、售前 / 销售、项目负责人、方案评审参与者。\n尤其适合需要边听边理解、及时澄清并沉淀讨论内容的工作场景。",
                  MARGIN + 99, 715, CONTENT - 112, 9, INK, bottom=755)

    def scenarios(self):
        self.new_page(2, "应用场景 / USE CASES")
        self.text("线下私聊、线上会议，都能辅助", MARGIN, 57, CONTENT, 25)
        self.text("共享式获取麦克风与系统声音，不独占音频设备，可与多种会议应用同时使用。",
                  MARGIN, 103, CONTENT, 10, MUTED)
        width = (CONTENT - 12) / 2
        scenarios = [
            ("01", "线下私聊与客户沟通", "面对面交流，也能及时抓住需求。",
             "通过麦克风听取线下私聊、客户访谈或售前交流；结合意图与推荐问题澄清需求和约束。",
             "价值：少漏问，让沟通更聚焦。"),
            ("02", "多种平台的在线会议", "沿用原来的会议系统和沟通方式。",
             "获取麦克风中的本地发言与系统播放的远端声音，辅助项目会议与方案评审；不依赖单一平台。",
             "价值：无需切换会议工具。"),
            ("03", "产品咨询与选型讨论", "方案能力和适用边界是什么？",
             "查看对话相关的产品指南，了解能力、场景、限制及许可 / 价格信息；再追问关键条件。",
             "价值：形成有依据的比较思路。"),
            ("04", "会后复盘与知识交接", "如何回看讨论与回答依据？",
             "保存并导入会话记录，回看转写、分组、分析与答复历史；选择、复制内容用于整理和交接。",
             "价值：让讨论内容可回看、可复用。"),
        ]
        for i, scenario in enumerate(scenarios):
            self.card(MARGIN + (i % 2) * (width + 12), 139 + (i // 2) * 161,
                      width, 149, *scenario)
        self.section("一条清晰的使用路径", 480)
        steps = [("采集", "选语言与音源\n开始 / 暂停听写"),
                 ("分析", "自动或手动分析\n全会话 / 单个组"),
                 ("追问", "选择推荐问题\n或输入自己的问题"),
                 ("沉淀", "保存本地记录\n导入、回看与复制")]
        step_width = (CONTENT - 3 * 15) / 4
        for i, (title, body) in enumerate(steps):
            x = MARGIN + i * (step_width + 15)
            self.box(x, 516, step_width, 90, PALE, LINE)
            self.text(title, x + 12, 527, step_width - 24, 13, BLUE)
            self.text(body, x + 12, 554, step_width - 24, 9, MUTED, bottom=601)
            if i < 3:
                middle = x + step_width + 7.5
                self.page.draw_line((middle - 4, 560), (middle + 4, 560), color=rgb(TEAL))
                self.page.draw_line((middle, 556), (middle + 4, 560), color=rgb(TEAL))
                self.page.draw_line((middle, 564), (middle + 4, 560), color=rgb(TEAL))
        self.box(MARGIN, 632, CONTENT, 124, "E9F5F3")
        self.text("共享式采集，不打断原有沟通", MARGIN + 15, 644, CONTENT - 30, 13, TEAL)
        self.text("不独占麦克风或播放设备，不改变其它应用的音频设置，也不接管会议。\n线下用麦克风，线上可同时获取麦克风与系统声音；无需会议平台专用接口。",
                  MARGIN + 15, 675, CONTENT - 30, 10, INK, bottom=723)
        self.text("系统声音来自所选播放设备；请先取得参与者同意，音频设备须允许共享访问。",
                  MARGIN + 15, 731, CONTENT - 30, 8, MUTED, bottom=751)

    def capabilities(self):
        self.new_page(3, "核心能力 / CAPABILITIES")
        self.text("核心功能与服务支持", MARGIN, 57, CONTENT, 25)
        self.text("从实时听写到上下文问答，帮助用户理解需求、组织回应并沉淀会话价值。",
                  MARGIN, 102, CONTENT, 10, MUTED)
        rows = [
            ("共享式音频采集", "麦克风 / 系统声音 / 混合输入；不独占设备，可与其它音频应用并行。"),
            ("实时语音转写", "支持中文、英文输入；保留发言时间与匿名说话人标签，方便阅读和回看。"),
            ("上下文分析", "意图、有用建议、阶段总结；自动 / 手动分析；分组分析与分析历史。"),
            ("推荐与问答", "未回答问题、可能后续问题、会话引导；需主动选择或提交才请求回答。"),
            ("产品与回答呈现", "提供相关产品指南；回答支持标题、列表、表格与链接，便于理解与引用。"),
            ("会话记录与复用", "保存 / 导入会话记录；回看转写、分析与答复历史，选择、复制所需内容。"),
        ]
        for i, (title, detail) in enumerate(rows):
            y = 135 + i * 43
            self.box(MARGIN, y, CONTENT, 43, PALE if i % 2 == 0 else WHITE)
            self.text(title, MARGIN + 11, y + 12, 109, 10, BLUE, bottom=y + 40)
            self.text(detail, MARGIN + 132, y + 7, CONTENT - 143, 9,
                      INK, bottom=y + 41)
        self.section("默认服务支持 · 其它系统可定制", 412)
        self.box(MARGIN, 443, CONTENT, 102, PALE, LINE)
        self.text("语音服务", MARGIN + 12, 454, 88, 10, BLUE)
        self.text("默认支持 Azure Cognitive Services（认知服务），用于语音转写。",
                  MARGIN + 104, 454, CONTENT - 116, 9)
        self.text("智能辅助", MARGIN + 12, 481, 88, 10, BLUE)
        self.text("默认支持 M365 Copilot / Work IQ；Copilot Chat 当前为预览能力。",
                  MARGIN + 104, 481, CONTENT - 116, 9)
        self.text("定制扩展", MARGIN + 12, 508, 88, 10, BLUE)
        self.text("其它语音、AI 与业务系统可按需求定制对接，具体范围另行确认。",
                  MARGIN + 104, 508, CONTENT - 116, 9, bottom=539)
        self.section("用户始终掌握沟通与使用节奏", 565)
        self.text("• 工具在原有沟通方式旁提供辅助，不接管会议，也不自动发送消息。\n  推荐问题需要主动选择或提交，才能获取对应参考回答。",
                  MARGIN + 2, 596, CONTENT - 4, 9, INK)
        self.text("• 在线语音与 AI 服务会处理音频或相关会话内容，记录可能含敏感内容。\n  使用前取得参与者同意，并遵守组织的数据与权限要求。",
                  MARGIN + 2, 632, CONTENT - 4, 9, INK)
        self.text("• 说话人标签不等于身份识别；中英文输入不是自动翻译。AI 答案、转写、\n  产品及许可 / 价格信息均需核验；本工具不自动发送消息或执行会议决策。",
                  MARGIN + 2, 668, CONTENT - 4, 9, INK)
        self.box(MARGIN, 714, CONTENT, 48, "E9F5F3")
        self.text("开始体验", MARGIN + 12, 723, 91, 11, TEAL)
        self.text("线下私聊或线上会议：开始听写 → 分析 → 主动提问 → 保存记录。\n保持原有沟通方式，让实时辅助自然融入你的工作。",
                  MARGIN + 103, 721, CONTENT - 115, 9, INK, bottom=757)


def demo_frame() -> bytes:
    manifest = json.loads((ROOT / "docs/demos/conversation-assistant-current-zh.json")
                          .read_text(encoding="utf-8"))
    start = 0
    target = None
    for scene in manifest["scenes"]:
        if scene["key"] == "live":
            target = start + scene["duration_ms"] - 1
            break
        start += scene["duration_ms"]
    if target is None:
        raise ValueError("Demo manifest has no live-transcript scene")
    elapsed = 0
    with Image.open(ROOT / "docs/demos/conversation-assistant-current-zh.gif") as image:
        for index in range(image.n_frames):
            image.seek(index)
            elapsed += image.info.get("duration", 0)
            if elapsed > target:
                stream = io.BytesIO()
                image.convert("RGB").save(stream, format="PNG")
                return stream.getvalue()
    raise ValueError("Demo frame timing does not match the manifest")


def validate(path: Path):
    with pdf.open(path) as doc:
        if len(doc) != 3:
            raise ValueError(f"Expected three pages, got {len(doc)}")
        required = [
            ["让每一段对话", "共享式", "非实机截图"],
            ["线下私聊", "在线会议", "不独占", "不改变其它应用", "会后复盘"],
            ["Azure Cognitive Services", "M365 Copilot", "Work IQ", "定制", "敏感内容"],
        ]
        for number, page in enumerate(doc):
            text = page.get_text().replace("\u00a0", " ")
            for phrase in required[number]:
                if phrase not in text:
                    raise ValueError(f"Page {number + 1} lacks searchable text: {phrase}")
            for phrase in ("SDK", "WebView2", "RBAC", "CLI", "Whisper", "源码", "部署"):
                if phrase in text:
                    raise ValueError(f"Page {number + 1} contains unwanted technical detail: {phrase}")
            for block in page.get_text("dict")["blocks"]:
                if not page.rect.contains(pdf.Rect(block["bbox"])):
                    raise ValueError(f"Page {number + 1} has out-of-page content")
            fonts = page.get_fonts()
            if not fonts or any(not doc.extract_font(font[0])[3] for font in fonts):
                raise ValueError(f"Page {number + 1} lacks embedded fonts")
        if not doc[0].get_images():
            raise ValueError("Overview page lacks its interface illustration")
        if len(doc[1].get_drawings()) < 10:
            raise ValueError("Scenario page lacks its vector cards and diagram")
        print(f"Validated: {path} | 3 A4 pages | searchable Chinese | embedded fonts")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--font", help="Path to a Chinese TTF/OTF/TTC font")
    parser.add_argument("--check", action="store_true", help="Validate existing PDF without rewriting")
    args = parser.parse_args()
    destination = args.output / PDF_NAME
    if args.check:
        validate(destination)
        return
    brochure = Brochure(find_font(args.font))
    brochure.overview(demo_frame())
    brochure.scenarios()
    brochure.capabilities()
    brochure.doc.set_metadata({
        "title": "Conversation Assistant 产品介绍",
        "author": "Conversation Assistant",
        "subject": "共享式音频采集、线下与线上应用场景、核心功能与服务支持",
        "keywords": "Conversation Assistant, 会话辅助, 语音转写, Work IQ, 产品介绍",
    })
    args.output.mkdir(parents=True, exist_ok=True)
    brochure.doc.subset_fonts()
    brochure.doc.save(destination, garbage=4, deflate=True)
    brochure.doc.close()
    validate(destination)
    with pdf.open(destination) as document:
        for number, page in enumerate(document, 1):
            page.get_pixmap(matrix=pdf.Matrix(1.8, 1.8)).save(
                args.output / f"page-{number}.png")
    print(f"Generated PDF and three page previews in {args.output}")


if __name__ == "__main__":
    main()
