// @vitest-environment jsdom
/**
 * Webview half of the container-child-ordinal contract guard (T-083). The C# half is
 * tests/SpecDesk.Diff.Tests/ContainerOrdinalContractTests.fs, which pins DiffWire.toWire's per-child
 * ordinals (Markdig AST, via childTexts) against the SAME container-ordinals.json fixture's `changes`.
 * Here we assert the other two parsers agree with that fixture's `childLines`/`childMarkers`:
 * markdown-it's `childLineStarts` (md-blocks.ts, splitTopLevelBlocks) and the ProseMirror schema's
 * container children (pm-markdown.ts, resolved through FormattedEditor's line↔node mapping). A
 * markdown-it/ProseMirror disagreement on child ORDER/COUNT for a container document — a nested list
 * inside an item, a loose/tight list, a table with an empty header row, a multi-paragraph item — fails
 * here rather than silently shifting the highlight to the wrong row/item at runtime.
 */

import { describe, expect, it } from "vitest";
import { FormattedEditor } from "../../src/editors/formatted.js";
import { splitTopLevelBlocks } from "../../src/editors/md-blocks.js";
import scenarios from "./container-ordinals.json" with { type: "json" };

interface Scenario {
  name: string;
  base: string;
  head: string;
  childLines: number[];
  childMarkers: string[];
  changes: { index: number; marker: string }[];
}

function mount(): { ed: FormattedEditor; host: HTMLDivElement } {
  const host = document.createElement("div");
  document.body.appendChild(host);
  const ed = new FormattedEditor(host, {
    onChange: () => {},
    onEditAttempt: () => {},
    onScroll: () => {},
    onCursor: () => {},
    onHover: () => {},
    onContentResize: () => {},
    onFocus: () => {},
    onActiveChange: () => {},
    onOpenLink: () => {},
  });
  return { ed, host };
}

describe("container child ordinals (md-blocks + PM schema vs. the shared cross-language fixture)", () => {
  for (const scenario of scenarios as Scenario[]) {
    it(`md-blocks childLineStarts match the fixture: ${scenario.name}`, () => {
      const blocks = splitTopLevelBlocks(scenario.head);
      expect(blocks).toHaveLength(1); // one top-level container per fixture scenario
      expect(blocks[0]?.childLineStarts).toEqual(scenario.childLines);
    });

    it(`the ProseMirror schema resolves each child ordinal to the fixture's marker line: ${scenario.name}`, () => {
      const { ed, host } = mount();
      ed.setText(scenario.head);

      for (let i = 0; i < scenario.childLines.length; i++) {
        const line = scenario.childLines[i];
        expect(line).toBeDefined();
        ed.setActiveLine(line ?? 0);
        const active = host.querySelectorAll(".sd-active-block");
        expect(active).toHaveLength(1);
        const text = active[0]?.textContent ?? "";

        const marker = scenario.childMarkers[i] ?? "";
        if (marker !== "") {
          expect(text).toContain(marker);
        }
        // No OTHER child's marker leaks into this one's highlighted node — the positive proof that
        // md-blocks' line and the PM schema's child-at-that-index are the SAME child, not an
        // off-by-one neighbor (which an empty marker at this position, e.g. an empty table header,
        // would not otherwise catch).
        for (let j = 0; j < scenario.childMarkers.length; j++) {
          const other = scenario.childMarkers[j];
          if (j !== i && other !== undefined && other !== "") {
            expect(text).not.toContain(other);
          }
        }
      }
    });
  }

  it("falls back to washing the whole container instead of guessing a wrong row/item when the two parsers' child counts disagree (T-083 runtime guard)", () => {
    const { ed, host } = mount();
    ed.setText("- one\n- two\n- three\n");
    // Simulate a native/webview ordinal divergence: in real documents md-blocks and pm-markdown share
    // the same tokenizer config (md-config.ts) and always agree on child count, but this guard exists
    // for the case they ever don't — corrupt the cached block map the way a real divergence would look.
    const blocks = (ed as unknown as { blocks: { childLineStarts?: number[] }[] }).blocks;
    const first = blocks[0];
    expect(first).toBeDefined();
    if (first !== undefined) {
      first.childLineStarts = [0, 1]; // 2 starts vs. the list's real 3 <li> children
    }

    ed.setActiveLine(1); // would have picked the 2nd item under the old clamp-and-guess behavior
    const active = host.querySelectorAll(".sd-active-block");
    expect(active).toHaveLength(1);
    // The whole list washes, not a single (possibly wrong) <li>.
    expect(active[0]?.tagName.toLowerCase()).toBe("ul");
    expect(active[0]?.textContent).toContain("one");
    expect(active[0]?.textContent).toContain("two");
    expect(active[0]?.textContent).toContain("three");
  });
});
