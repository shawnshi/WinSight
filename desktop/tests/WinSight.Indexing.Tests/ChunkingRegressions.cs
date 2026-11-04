using System.Text;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;

/// <summary>Chunking regressions: token-aware budget, CJK overlap and boundaries, surrogate safety, termination.</summary>
internal sealed partial class Suite
{
    private static List<string> ChunkBodies(string text) => ContentExtractor.Chunks(text, CancellationToken.None).Item1.Select(s => s.Excerpt!).ToList();

    // Offsets of each chunk inside the normalized text, found sequentially so overlap can be measured.
    private static List<(int Start, int End)> Spans(string text, List<string> chunks)
    {
        var spans = new List<(int, int)>();
        int cursor = 0;
        foreach (var chunk in chunks)
        {
            int at = text.IndexOf(chunk, Math.Max(0, cursor - 2000), StringComparison.Ordinal);
            if (at < 0) throw new Exception("Chunk is not a verbatim slice of the normalized text.");
            spans.Add((at, at + chunk.Length));
            cursor = at;
        }
        return spans;
    }

    private async Task ChunkingRegressionsAsync()
    {
        await Test("RET-H4 Chinese text without whitespace keeps overlap and a token-sized budget", () =>
        {
            var text = string.Concat(Enumerable.Range(0, 200).Select(i => $"第{i}号医保结算报销比例调整通知，各定点医疗机构应严格执行。")); // about 6000 characters, no whitespace; unique so offsets are unambiguous
            var chunks = ChunkBodies(text);
            var spans = Spans(text, chunks);
            Check(chunks.Count >= 7, "CJK chunks must be much shorter than the old 1800 characters: " + chunks.Count);
            Check(chunks.All(c => c.Length <= 800), "chunk exceeds the CJK token budget: " + chunks.Max(c => c.Length));
            for (int i = 0; i + 1 < spans.Count; i++)
            {
                int overlap = spans[i].End - spans[i + 1].Start;
                Check(overlap > 0 && overlap <= 120, $"overlap {overlap} at chunk {i}");
            }
            Check(chunks.Take(chunks.Count - 1).All(c => c.EndsWith('。') || c.EndsWith('，')), "cuts must land on Chinese punctuation, not mid-word");
            Check(spans[0].Start == 0 && spans[^1].End == text.Length, "chunks must cover the whole text");
            return Task.CompletedTask;
        });

        await Test("RET-H4 English keeps the previous 1800 character budget and whitespace cuts", () =>
        {
            var text = string.Join(" ", Enumerable.Range(0, 260).Select(i => $"reimbursement ratio {i} adjustment notice"));
            var chunks = ChunkBodies(text);
            var spans = Spans(text, chunks);
            Check(chunks.Count >= 5 && chunks.All(c => c.Length <= 1800), "English budget changed: " + string.Join(",", chunks.Select(c => c.Length)));
            for (int i = 0; i + 1 < spans.Count; i++) Check(spans[i].End - spans[i + 1].Start is > 100 and <= 260, "English overlap " + (spans[i].End - spans[i + 1].Start));
            for (int i = 0; i < spans.Count; i++)
                Check((spans[i].Start == 0 || char.IsWhiteSpace(text[spans[i].Start - 1])) && (spans[i].End == text.Length || char.IsWhiteSpace(text[spans[i].End])), "English cuts must stay on word boundaries");
            return Task.CompletedTask;
        });

        await Test("RET-H4 paragraph and sentence boundaries win over arbitrary cuts", () =>
        {
            var paragraph = string.Concat(Enumerable.Repeat("这是一个完整的句子，用来占位。", 30)); // about 420 characters
            var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 6));
            var chunks = ChunkBodies(text);
            Check(chunks.Count >= 3 && chunks.Take(chunks.Count - 1).All(c => c.EndsWith('。')), "chunks must end at sentence boundaries: " + string.Join("|", chunks.Select(c => c[^1])));
            var english = string.Join(" ", Enumerable.Range(0, 120).Select(i => $"Sentence number {i} explains the migration plan."));
            var cuts = ChunkBodies(english);
            Check(cuts.Count > 1 && cuts.Take(cuts.Count - 1).All(c => c.EndsWith('.')), "English sentences should end chunks");
            return Task.CompletedTask;
        });

        await Test("RET-H4 surrogate pairs are never split and pathological input terminates", () =>
        {
            var emoji = string.Concat(Enumerable.Repeat("文档😀资料𠮷字", 900));
            foreach (var chunk in ChunkBodies(emoji))
                for (int i = 0; i < chunk.Length; i++)
                    Check(!(char.IsHighSurrogate(chunk[i]) && (i + 1 >= chunk.Length || !char.IsLowSurrogate(chunk[i + 1]))) && !(char.IsLowSurrogate(chunk[i]) && (i == 0 || !char.IsHighSurrogate(chunk[i - 1]))), "lone surrogate in chunk");
            Check(ChunkBodies(new string('a', 20000)).Count > 5, "a single endless word must still be cut");
            Check(ChunkBodies(new string('字', 20000)).Count > 20, "an endless CJK run must still be cut");
            Check(ChunkBodies("   \n\n\n   ").Count == 0 && ChunkBodies("短").Single() == "短", "empty and one-character input");
            var (capped, truncated) = ContentExtractor.Chunks(string.Concat(Enumerable.Repeat("字", 3_000_000)), CancellationToken.None);
            Check(capped.Count == ContentExtractor.MaxSegments && truncated, "the segment cap must still mark truncation");
            return Task.CompletedTask;
        });
    }
}
