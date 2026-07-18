using System.Net;
using System.Text;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host;

/// <summary>
/// Renders the PoC-7 Part C "in-flight PR comparison" to the self-contained HTML the
/// <c>pr.compare.rendered</c> reply carries. It reuses the SAME structural diff as the local "Show changes"
/// overlay (<see cref="DiffProjection"/> → <c>SpecDesk.Diff</c>) — no new diff algorithm, only a new input
/// pair (a chosen base vs the PR head) and two output representations:
/// <list type="bullet">
///   <item><b>rendered</b> — the PR head rendered to HTML via the shared Markdig pipeline
///   (<see cref="Renderer"/>), with each changed/added/moved top-level block tagged <c>data-diff</c> by
///   its head line range, and removed base blocks shown as inline markers.</item>
///   <item><b>raw</b> — the PR head source, line by line, with changed head lines styled and removed base
///   lines interleaved at their anchor — the literal <c>.md</c> line diff of the mandatory toggle.</item>
/// </list>
/// Pure and stateless (the renderer is injected), so the wire-facing HTML shape is unit-testable apart from
/// the controller. Read-only by construction (v1 boundary): it never merges, only shows overlapping work.
/// </summary>
internal static class PrCompareHtml
{
	/// <summary>Build the comparison HTML for the (base → PR head) pair in the requested representation.
	/// <paramref name="baseText"/> is the chosen base (the working copy, or the <c>main</c> blob) — <c>null</c>
	/// when there is no baseline (the file doesn't exist on that base), in which case the PR head is shown
	/// plainly with no diff marks. <paramref name="headText"/> is the PR head content; <paramref name="mode"/>
	/// is one of <see cref="PrCompareModes"/>. <paramref name="render"/> is the shared Markdig renderer (kept
	/// injected so it is stubbable in tests, mirroring the host's own <c>_render</c>).</summary>
	public static string Build(
		string? baseText,
		string headText,
		string mode,
		string docDir,
		Func<string, string, Renderer.RenderResult> render)
	{
		// Normalize BOTH sides to LF before diffing and before splitting/rendering the head, so the diff's
		// 0-based head line ranges align with the head lines this code slices and with the renderer's line map.
		// (DiffProjection normalizes the base again; that is idempotent.)
		string normalizedHead = NormalizeLf(headText);
		string? normalizedBase = baseText is null ? null : NormalizeLf(baseText);

		DiffResultPayload diff = DiffProjection.Build(normalizedBase, normalizedHead);
		bool overflowed = diff.Overflow is not null;

		return mode == PrCompareModes.Raw
			? BuildRaw(normalizedHead, diff.Entries, overflowed)
			: BuildRendered(normalizedHead, docDir, diff.Entries, overflowed, render);
	}

	private static string NormalizeLf(string text) =>
		text.Contains('\r') ? text.Replace("\r\n", "\n").Replace("\r", "\n") : text;

	// The CSS class suffix (and the data-diff attribute value) for a changed-block kind.
	private static string KindClass(DiffEntryPayload entry) => entry switch
	{
		AddedDiffEntry => "added",
		MovedDiffEntry => "moved",
		ChangedDiffEntry => "changed",
		_ => "changed",
	};

	// --- raw source diff ---

	private static string BuildRaw(string headText, IReadOnlyList<DiffEntryPayload> entries, bool overflowed)
	{
		string[] lines = headText.Split('\n');
		// Per head-line kind (added/moved/changed) from the structural entries' head ranges; null → unchanged.
		string?[] lineKind = new string?[lines.Length];
		// Removed base blocks keyed by the head line they sat before (anchorLine), interleaved in order.
		Dictionary<int, List<string>> removedByAnchor = [];
		foreach (DiffEntryPayload entry in entries)
		{
			if (entry is RemovedDiffEntry removed)
			{
				(removedByAnchor.TryGetValue(removed.AnchorLine, out List<string>? bucket)
					? bucket
					: removedByAnchor[removed.AnchorLine] = []).Add(removed.RemovedText);
				continue;
			}
			(int start, int end) = LineRange(entry);
			string kind = KindClass(entry);
			for (int line = Math.Max(0, start); line <= Math.Min(lines.Length - 1, end); line++)
			{
				lineKind[line] = kind;
			}
		}

		StringBuilder html = new();
		html.Append("<div class=\"pr-compare pr-compare--raw\">");
		if (overflowed)
		{
			html.Append(OverflowBanner);
		}
		for (int i = 0; i < lines.Length; i++)
		{
			AppendRemovedRawLines(html, removedByAnchor, i);
			string cls = lineKind[i] ?? "context";
			html.Append("<div class=\"cmp-line cmp-").Append(cls).Append("\">")
				.Append(WebUtility.HtmlEncode(lines[i]))
				.Append("</div>");
		}
		// Removed blocks anchored past the last head line (deletions at the end of the file).
		foreach (int anchor in removedByAnchor.Keys.Where(a => a >= lines.Length).OrderBy(a => a))
		{
			AppendRemovedRawLines(html, removedByAnchor, anchor);
		}
		html.Append("</div>");
		return html.ToString();
	}

	private static void AppendRemovedRawLines(
		StringBuilder html, Dictionary<int, List<string>> removedByAnchor, int anchor)
	{
		if (!removedByAnchor.TryGetValue(anchor, out List<string>? blocks))
		{
			return;
		}
		foreach (string block in blocks)
		{
			foreach (string removedLine in block.Split('\n'))
			{
				html.Append("<div class=\"cmp-line cmp-removed\">")
					.Append(WebUtility.HtmlEncode(removedLine))
					.Append("</div>");
			}
		}
	}

	// --- rendered structural diff ---

	private static string BuildRendered(
		string headText,
		string docDir,
		IReadOnlyList<DiffEntryPayload> entries,
		bool overflowed,
		Func<string, string, Renderer.RenderResult> render)
	{
		Renderer.RenderResult rendered = render(docDir, headText);
		string body = AnnotateRendered(rendered.Html, rendered.LineMap, entries);
		StringBuilder html = new();
		html.Append("<div class=\"pr-compare pr-compare--rendered\">");
		if (overflowed)
		{
			html.Append(OverflowBanner);
		}
		html.Append(body).Append("</div>");
		return html.ToString();
	}

	// Tag each changed/added/moved top-level block with a data-diff attribute, and splice a removed-block
	// marker before the block a deleted base block sat before. The renderer stamps `data-line-start="N"` on
	// each top-level rendered block in document order, one per LineMap entry (the documented LineMap↔data-line
	// invariant), so the i-th occurrence corresponds to LineMap[i]. Positional, not a general HTML parse — but
	// deterministic over the shared renderer's own output.
	private static string AnnotateRendered(
		string html, Renderer.LineSpan[] lineMap, IReadOnlyList<DiffEntryPayload> entries)
	{
		// Head ranges that carry a kind (added/moved/changed), and the removed anchors to place.
		List<(int Start, int End, string Kind)> ranges = [];
		List<(int Anchor, string Text)> removed = [];
		foreach (DiffEntryPayload entry in entries)
		{
			if (entry is RemovedDiffEntry r)
			{
				removed.Add((r.AnchorLine, r.RemovedText));
			}
			else
			{
				(int start, int end) = LineRange(entry);
				ranges.Add((start, end, KindClass(entry)));
			}
		}
		removed.Sort((left, right) => left.Anchor.CompareTo(right.Anchor));

		const string marker = "data-line-start=\"";
		StringBuilder output = new(html.Length + 256);
		int cursor = 0;
		int occurrence = 0;
		int removedIndex = 0;
		int searchFrom = 0;
		int found;
		while ((found = html.IndexOf(marker, searchFrom, StringComparison.Ordinal)) >= 0)
		{
			int valueStart = found + marker.Length;
			int valueEnd = html.IndexOf('"', valueStart);
			if (valueEnd < 0)
			{
				break; // Malformed attribute — stop annotating and emit the remainder verbatim below.
			}

			Renderer.LineSpan span = occurrence < lineMap.Length
				? lineMap[occurrence]
				: new Renderer.LineSpan(int.MaxValue, int.MaxValue);

			// Insert any removed markers that anchor at or before this block, BEFORE the block's open tag.
			int tagStart = html.LastIndexOf('<', found);
			if (tagStart >= cursor)
			{
				while (removedIndex < removed.Count && removed[removedIndex].Anchor <= span.LineStart)
				{
					output.Append(html, cursor, tagStart - cursor);
					output.Append(RemovedMarker(removed[removedIndex].Text));
					cursor = tagStart;
					removedIndex++;
				}
			}

			string? kind = KindForSpan(span, ranges);
			if (kind is not null)
			{
				// Copy up to just after the data-line-start attribute, then splice the data-diff attribute in.
				output.Append(html, cursor, (valueEnd + 1) - cursor);
				output.Append(" data-diff=\"").Append(kind).Append('"');
				cursor = valueEnd + 1;
			}

			occurrence++;
			searchFrom = valueEnd + 1;
		}

		output.Append(html, cursor, html.Length - cursor);
		// Any removed blocks anchored past every rendered block (deletions at the end) trail the content.
		for (; removedIndex < removed.Count; removedIndex++)
		{
			output.Append(RemovedMarker(removed[removedIndex].Text));
		}
		return output.ToString();
	}

	// The kind for a block whose head line span overlaps a changed range, or null when it is unchanged. A
	// changed range wins over an added/moved one when both overlap (the block genuinely changed content).
	private static string? KindForSpan(Renderer.LineSpan span, List<(int Start, int End, string Kind)> ranges)
	{
		string? result = null;
		foreach ((int start, int end, string kind) in ranges)
		{
			if (span.LineStart <= end && span.LineEnd >= start)
			{
				if (kind == "changed")
				{
					return "changed";
				}
				result ??= kind;
			}
		}
		return result;
	}

	private static string RemovedMarker(string removedText)
	{
		// The first non-empty line of the removed block is enough to identify it in the marker.
		string label = removedText.Split('\n').FirstOrDefault(line => line.Trim().Length > 0) ?? string.Empty;
		return "<div class=\"cmp-block cmp-removed\" data-diff=\"removed\"><del>"
			+ WebUtility.HtmlEncode(label)
			+ "</del></div>";
	}

	private static (int Start, int End) LineRange(DiffEntryPayload entry) => entry switch
	{
		AddedDiffEntry a => (a.LineStart, a.LineEnd),
		MovedDiffEntry m => (m.LineStart, m.LineEnd),
		ChangedDiffEntry c => (c.LineStart, c.LineEnd),
		_ => (0, -1),
	};

	private const string OverflowBanner =
		"<div class=\"cmp-notice\">This file has too many differences to show in detail.</div>";
}
