/// Maps source character offsets to 0-based line numbers. Markdig blocks expose a start `Line`
/// directly but no end line; we derive the end line from the block's source span via this index.
module SpecDesk.Markdown.Lines

/// Sorted array of the character offset at which each line begins (line 0 starts at offset 0).
type Index = int[]

/// Build the line-start offset table for a source string.
let build (text: string) : Index =
    let starts = ResizeArray<int>()
    starts.Add(0)
    for i in 0 .. text.Length - 1 do
        if text.[i] = '\n' then
            starts.Add(i + 1)
    starts.ToArray()

/// The 0-based line number containing the given character offset (the largest line whose start
/// offset is &lt;= offset). Clamped to the valid line range so a stray span never throws.
let lineOfOffset (index: Index) (offset: int) : int =
    if offset <= 0 then
        0
    else
        // Binary search for the last start <= offset.
        let mutable lo = 0
        let mutable hi = index.Length - 1
        let mutable result = 0
        while lo <= hi do
            let mid = lo + (hi - lo) / 2
            if index.[mid] <= offset then
                result <- mid
                lo <- mid + 1
            else
                hi <- mid - 1
        result
