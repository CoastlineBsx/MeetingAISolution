using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MeetingAI.Host.RAG.Models;

namespace MeetingAI.Host.MeetingPreparation;

public sealed partial class HotwordExtractor
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "meeting", "agenda", "summary", "introduction", "overview", "thank you",
        "会议", "议程", "总结", "介绍", "概述", "谢谢", "标题", "幻灯片", "演讲者备注"
    };

    public List<HotwordCandidate> Extract(IEnumerable<ExtractedDocumentPage> pages)
    {
        var matches = new Dictionary<string, CandidateState>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            Add(matches, page.Title, page.PageNumber, 2.2, "title");
            foreach (var rawLine in page.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = Clean(rawLine);
                if (line.Length is >= 2 and <= 40) Add(matches, line, page.PageNumber, 0.5, "short-line");
                foreach (Match match in EnglishTermRegex().Matches(line))
                    Add(matches, match.Value, page.PageNumber, 1.0, "latin-term");
                foreach (Match match in QuotedChineseRegex().Matches(line))
                    Add(matches, match.Groups[1].Value, page.PageNumber, 1.0, "quoted-term");
            }
        }

        return matches.Values
            .Where(item => item.Pages.Count > 0)
            .OrderByDescending(item => item.Score + Math.Min(item.Pages.Count, 3) * 0.25)
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select(item => new HotwordCandidate
            {
                Text = item.Text,
                Score = Math.Clamp(item.Score + Math.Min(item.Pages.Count - 1, 2) * 0.25, 1.0, 5.0),
                Enabled = true,
                SourcePages = item.Pages.OrderBy(number => number).ToList(),
                SourceKind = item.Kind
            }).ToList();
    }

    private static void Add(Dictionary<string, CandidateState> results, string? raw, int page, double score, string kind)
    {
        var text = Clean(raw);
        if (text.Length < 2 || text.Length > 60 || Stopwords.Contains(text) || text.All(char.IsDigit)) return;
        var normalized = text.ToLowerInvariant();
        if (!results.TryGetValue(normalized, out var state))
        {
            state = new CandidateState(text, kind);
            results[normalized] = state;
        }
        state.Score = Math.Max(state.Score, score);
        state.Pages.Add(page);
    }

    private static string Clean(string? value)
        => Regex.Replace(value ?? string.Empty, @"^[\s\p{P}\p{S}]+|[\s\p{P}\p{S}]+$", "").Trim();

    private sealed class CandidateState(string text, string kind)
    {
        public string Text { get; } = text;
        public string Kind { get; } = kind;
        public double Score { get; set; }
        public HashSet<int> Pages { get; } = new();
    }

    [GeneratedRegex(@"\b(?:[A-Z]{2,}(?:[-/][A-Z0-9]+)*|[A-Z][a-z]+(?:[A-Z][A-Za-z0-9]+)+|[A-Za-z]+(?:-[A-Za-z0-9]+)+|(?:OpenVINO|Granite|Sherpa-ONNX|ONNX Runtime|CTranslate2|Whisper))\b")]
    private static partial Regex EnglishTermRegex();

    [GeneratedRegex(@"[“""「『《]([^”""」』》]{2,20})[”""」』》]")]
    private static partial Regex QuotedChineseRegex();
}
