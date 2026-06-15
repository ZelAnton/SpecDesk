# Welcome to SpecDesk

SpecDesk is a manager-friendly editor for Markdown specifications. This document loads
automatically so you can see the **live preview** and **scroll-sync** working straight away.

## What this demo shows

- A **CodeMirror** source editor on the left.
- A **rendered preview** on the right, produced natively by Markdig.
- Scrolling either pane keeps the other in step.

Type in the editor and the preview updates within a moment — even while typing quickly, you
never see a stale render.

## A few Markdown features

Inline styles: *emphasis*, **strong**, `inline code`, and a [link](https://example.com).

> Blockquotes render too. This is the experience a reviewer will eventually see.

### A table

| Feature      | Status      |
| ------------ | ----------- |
| Editor       | Working     |
| Live preview | Working     |
| Scroll-sync  | Working     |

### A code block

```fsharp
let render (text: string) : RenderResult =
    let doc = Markdown.Parse(text, Pipeline.shared)
    // ...attach line attributes, render, collect the line map...
    { Html = html; LineMap = spans }
```

## Try it

1. Edit any line above.
2. Scroll this pane — the editor follows.
3. **Paste or drag an image** into the editor — it is saved into the repo, named automatically,
   and linked here; the preview shows it straight away.
4. Use **Open…** to load another `.md`, or **Save** to write your edits back to disk.

---

That's the whole PoC-2 surface: an editor, a preview, and the line map that ties them together.
