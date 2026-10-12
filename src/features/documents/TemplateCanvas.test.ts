import { describe, expect, it } from "vitest";
import { parseBlocks, serializeBlocks } from "./TemplateCanvas";

describe("template canvas blocks", () => {
  const body = "Dear Sir,\n\n# 1. Scope\n\nDesign phase: [[list]]\nHandover: [[list]]\n\n- one\n- two\n\nYours sincerely,";

  it("reads wording into headings, paragraphs and bullet lists", () => {
    const blocks = parseBlocks(body);
    expect(blocks.map((b) => b.kind)).toEqual(["paragraph", "heading", "paragraph", "bullets", "paragraph"]);
    expect(blocks[1].text).toBe("1. Scope");
    expect(blocks[3].text).toBe("one\ntwo");
  });

  it("writes the same wording back, so the canvas and the text view never disagree", () => {
    expect(serializeBlocks(parseBlocks(body))).toBe(body);
  });

  it("splits a heading that shares a block with its text, and drops empty blocks", () => {
    const blocks = parseBlocks("# Heading\nfollowing text");
    expect(blocks.map((b) => b.kind)).toEqual(["heading", "paragraph"]);
    expect(serializeBlocks([...blocks, { id: 99, kind: "paragraph", text: "   " }])).toBe("# Heading\n\nfollowing text");
  });

  it("keeps fields and prompts untouched", () => {
    expect(serializeBlocks(parseBlocks("Fee {{agreement.fee}} for [[describe]]"))).toBe("Fee {{agreement.fee}} for [[describe]]");
  });
});
