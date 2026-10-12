import { describe, expect, it } from "vitest";
import { toCsv } from "./csv";

describe("CSV export", () => {
  it("neutralises cells a spreadsheet could run as a formula", () => {
    const out = toCsv([["=cmd|' /C calc'!A0", "+1", "-2", "@SUM(A1)", "plain"]]);
    expect(out).toBe(`'=cmd|' /C calc'!A0,'+1,'-2,'@SUM(A1),plain`);
  });
  it("leaves real numbers alone, including negatives", () => {
    expect(toCsv([[-5, 1234.5, 0]])).toBe("-5,1234.5,0");
  });
  it("quotes commas, quotes and newlines", () => {
    expect(toCsv([['a,b', 'say "hi"', "x\ny"]])).toBe('"a,b","say ""hi""","x\ny"');
  });
});
