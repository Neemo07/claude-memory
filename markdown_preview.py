#!/usr/bin/env python3
"""Small, dependency-free Markdown parser for the UI preview.

The parser covers Markdown emitted by claude-memory and common README shapes:
headings, emphasis, strikethrough, links, inline code, fenced code, lists, task
lists, block quotes, tables, and horizontal rules. Raw HTML is never interpreted.
Every text value stays plain text so each front end can render it without trusting
markup from generated content.
"""
from __future__ import annotations

import re
from typing import Any, Dict, List, Optional, Sequence, Tuple

Span = Dict[str, Any]
Block = Dict[str, Any]
ParserResult = Optional[Tuple[Block, int]]

_HEADING = re.compile(r"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$")
_RULE = re.compile(r"^ {0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$")
_BULLET = re.compile(r"^ {0,3}([-+*])\s+(.+)$")
_ORDERED = re.compile(r"^ {0,3}(\d+)[.)]\s+(.+)$")
_FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})\s*([^\s`]*)")
_TABLE_SEPARATOR = re.compile(
    r"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$"
)
_SAFE_SCHEMES = ("http", "https", "mailto")
_TASK_ITEM = re.compile(r"^\[([ xX])\]\s+(.*)$")


def parse_markdown(text: str) -> List[Block]:
    """Parse Markdown into JSON-serialisable blocks."""
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    return _parse_blocks(lines)


def _parse_blocks(lines: Sequence[str]) -> List[Block]:
    blocks: List[Block] = []
    index = 0
    while index < len(lines):
        if not lines[index].strip():
            index += 1
            continue
        block, index = _parse_next_block(lines, index)
        blocks.append(block)
    return blocks


def _parse_next_block(lines: Sequence[str], index: int) -> Tuple[Block, int]:
    for parser in _BLOCK_PARSERS:
        parsed = parser(lines, index)
        if parsed is not None:
            return parsed
    return _parse_paragraph(lines, index)


def _parse_code_block(lines: Sequence[str], index: int) -> ParserResult:
    fence = _FENCE.match(lines[index])
    if not fence:
        return None
    marker = fence.group(1)[0]
    width = len(fence.group(1))
    closing = re.compile(r"^ {0,3}" + re.escape(marker) + "{" + str(width) + r",}\s*$")
    index += 1
    code_lines: List[str] = []
    while index < len(lines) and not closing.match(lines[index]):
        code_lines.append(lines[index])
        index += 1
    if index < len(lines):
        index += 1
    return {"type": "code", "text": "\n".join(code_lines)}, index


def _parse_rule(lines: Sequence[str], index: int) -> ParserResult:
    if not _RULE.match(lines[index]):
        return None
    return {"type": "rule"}, index + 1


def _parse_heading(lines: Sequence[str], index: int) -> ParserResult:
    heading = _HEADING.match(lines[index])
    if not heading:
        return None
    block = {
        "type": "heading",
        "level": len(heading.group(1)),
        "spans": parse_inline(heading.group(2)),
    }
    return block, index + 1


def _parse_quote(lines: Sequence[str], index: int) -> ParserResult:
    if not re.match(r"^ {0,3}>", lines[index]):
        return None
    quote_lines: List[str] = []
    while index < len(lines) and re.match(r"^ {0,3}>", lines[index]):
        quote_lines.append(re.sub(r"^ {0,3}> ?", "", lines[index]))
        index += 1
    return {"type": "quote", "blocks": _parse_blocks(quote_lines)}, index


def _parse_list(lines: Sequence[str], index: int) -> ParserResult:
    if not (_BULLET.match(lines[index]) or _ORDERED.match(lines[index])):
        return None
    ordered = bool(_ORDERED.match(lines[index]))
    first_index = index
    items: List[Dict[str, Any]] = []
    while index < len(lines):
        parsed = _parse_list_item(lines, index, ordered)
        if parsed is None:
            break
        content, index = parsed
        item: Dict[str, Any] = {}
        task = _TASK_ITEM.match(content)
        if task:
            item["checked"] = task.group(1).lower() == "x"
            content = task.group(2)
        item["spans"] = parse_inline(content)
        items.append(item)
    block: Block = {"type": "list", "ordered": ordered, "items": items}
    first = _ORDERED.match(lines[first_index]) if ordered else None
    if first:
        block["start"] = int(first.group(1))
    return block, index


def _parse_list_item(lines: Sequence[str], index: int,
                     ordered: bool) -> Optional[Tuple[str, int]]:
    bullet = _BULLET.match(lines[index])
    number = _ORDERED.match(lines[index])
    if ordered and not number:
        return None
    if not ordered and not bullet:
        return None
    content = number.group(2) if number else bullet.group(2)
    index += 1
    continuation: List[str] = []
    while index < len(lines) and lines[index].strip() and lines[index].startswith("  "):
        continuation.append(lines[index].strip())
        index += 1
    if continuation:
        content += " " + " ".join(continuation)
    return content, index


def _parse_table(lines: Sequence[str], index: int) -> ParserResult:
    if not _is_table(lines, index):
        return None
    headers = [parse_inline(cell) for cell in _table_cells(lines[index])]
    align = [_column_align(cell) for cell in _table_cells(lines[index + 1])]
    index += 2
    rows: List[List[List[Span]]] = []
    while index < len(lines) and lines[index].strip() and "|" in lines[index]:
        rows.append([parse_inline(cell) for cell in _table_cells(lines[index])])
        index += 1
    return {"type": "table", "headers": headers, "rows": rows,
            "align": align}, index


def _parse_paragraph(lines: Sequence[str], index: int) -> Tuple[Block, int]:
    paragraph_lines: List[str] = []
    while index < len(lines) and lines[index].strip():
        if paragraph_lines and _starts_block(lines, index):
            break
        paragraph_lines.append(lines[index].strip())
        index += 1
    block = {"type": "paragraph", "spans": parse_inline(" ".join(paragraph_lines))}
    return block, index


def _starts_block(lines: Sequence[str], index: int) -> bool:
    line = lines[index]
    return bool(
        _HEADING.match(line)
        or _RULE.match(line)
        or _FENCE.match(line)
        or re.match(r"^ {0,3}>", line)
        or _BULLET.match(line)
        or _ORDERED.match(line)
        or _is_table(lines, index)
    )


def _is_table(lines: Sequence[str], index: int) -> bool:
    if index + 1 >= len(lines):
        return False
    return "|" in lines[index] and bool(_TABLE_SEPARATOR.match(lines[index + 1]))


def _table_cells(line: str) -> List[str]:
    stripped = line.strip()
    if stripped.startswith("|"):
        stripped = stripped[1:]
    if stripped.endswith("|"):
        stripped = stripped[:-1]
    return [cell.strip() for cell in stripped.split("|")]


def _column_align(cell: str) -> str:
    """GitHub table alignment from the separator row (:--, --:, :--:)."""
    left = cell.startswith(":")
    right = cell.endswith(":")
    if left and right:
        return "center"
    if right:
        return "right"
    return "left"


def parse_inline(text: str) -> List[Span]:
    """Parse inline emphasis and code into styled plain-text spans."""
    spans: List[Span] = []
    plain: List[str] = []
    index = 0
    while index < len(text):
        consumed = _consume_inline(text, index)
        if consumed is None:
            plain.append(text[index])
            index += 1
            continue
        _flush_plain(spans, plain)
        parsed_spans, index = consumed
        spans.extend(parsed_spans)
    _flush_plain(spans, plain)
    return spans


def _consume_inline(text: str, index: int) -> Optional[Tuple[List[Span], int]]:
    for consumer in _INLINE_CONSUMERS:
        consumed = consumer(text, index)
        if consumed is not None:
            return consumed
    return None


def _consume_code(text: str, index: int) -> Optional[Tuple[List[Span], int]]:
    if text[index] != "`":
        return None
    end = text.find("`", index + 1)
    if end == -1:
        return None
    return [{"text": text[index + 1:end], "code": True}], end + 1


def _consume_link(text: str, index: int) -> Optional[Tuple[List[Span], int]]:
    if text[index] != "[":
        return None
    close = text.find("](", index + 1)
    if close == -1:
        return None
    end = text.find(")", close + 2)
    if end == -1:
        return None
    label = text[index + 1:close]
    target = text[close + 2:end].strip()
    if not label or not target or any(ch in target for ch in ' \t\n<>"'):
        return None
    href = _safe_href(target)
    if href is None:
        return None
    spans = parse_inline(label)
    for span in spans:
        span["href"] = href
    return spans, end + 1


def _safe_href(target: str) -> Optional[str]:
    """Allow http(s), mailto, anchors and relative paths; refuse javascript: etc."""
    match = re.match(r"^([A-Za-z][A-Za-z0-9+.-]*):", target)
    if match and match.group(1).lower() not in _SAFE_SCHEMES:
        return None
    return target


def _consume_strike(text: str, index: int) -> Optional[Tuple[List[Span], int]]:
    if not text.startswith("~~", index):
        return None
    end = text.find("~~", index + 2)
    if end == -1:
        return None
    spans = parse_inline(text[index + 2:end])
    for span in spans:
        span["strike"] = True
    return spans, end + 2


def _consume_bold(text: str, index: int) -> Optional[Tuple[List[Span], int]]:
    if not text.startswith("**", index):
        return None
    end = text.find("**", index + 2)
    if end == -1:
        return None
    spans = parse_inline(text[index + 2:end])
    for span in spans:
        span["bold"] = True
    return spans, end + 2


def _consume_italic(text: str, index: int) -> Optional[Tuple[List[Span], int]]:
    if text[index] not in ("*", "_"):
        return None
    marker = text[index]
    if marker == "_" and index > 0 and text[index - 1].isalnum():
        return None
    end = text.find(marker, index + 1)
    if end == -1:
        return None
    if marker == "_" and end + 1 < len(text) and text[end + 1].isalnum():
        return None
    spans = parse_inline(text[index + 1:end])
    for span in spans:
        span["italic"] = True
    return spans, end + 1


def _flush_plain(spans: List[Span], plain: List[str]) -> None:
    if plain:
        spans.append({"text": "".join(plain)})
        plain.clear()


_BLOCK_PARSERS = (
    _parse_code_block,
    _parse_rule,
    _parse_heading,
    _parse_quote,
    _parse_list,
    _parse_table,
)
_INLINE_CONSUMERS = (_consume_code, _consume_link, _consume_strike,
                    _consume_bold, _consume_italic)
