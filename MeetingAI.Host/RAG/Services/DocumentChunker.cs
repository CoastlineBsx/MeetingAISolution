using System;
using System.Collections.Generic;
using System.Text;
using MeetingAI.Host.RAG.Models;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// 文档分块器：将长文本分割为适合嵌入的小块
/// </summary>
public class DocumentChunker
{
    private readonly int _targetChunkSize;
    private readonly int _maxChunkSize;
    private readonly int _overlapSize;

    /// <summary>
    ///
    /// </summary>
    /// <param name="targetChunkSize">目标块大小（字符数）</param>
    /// <param name="maxChunkSize">最大块大小（字符数）</param>
    /// <param name="overlapSize">重叠大小（字符数）</param>
    public DocumentChunker(int targetChunkSize = 800, int maxChunkSize = 1200, int overlapSize = 100)
    {
        _targetChunkSize = targetChunkSize;
        _maxChunkSize = maxChunkSize;
        _overlapSize = overlapSize;
    }

    /// <summary>
    /// 将文档分块
    /// </summary>
    public List<DocumentChunk> ChunkDocument(string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<DocumentChunk>();
        }

        var chunks = new List<DocumentChunk>();

        // 先按段落分割
        var paragraphs = SplitIntoParagraphs(content);

        var currentChunk = new StringBuilder();
        int currentChunkStart = 0;
        int currentPosition = 0;

        foreach (var paragraph in paragraphs)
        {
            var paragraphLength = paragraph.Length;

            // 如果当前块 + 新段落超过目标大小，保存当前块
            if (currentChunk.Length > 0 && currentChunk.Length + paragraphLength > _targetChunkSize)
            {
                // 保存当前块
                SaveChunk(chunks, currentChunk.ToString(), source, currentChunkStart, currentPosition);

                // 开始新块，保留重叠部分
                var overlapText = GetOverlapText(currentChunk.ToString());
                currentChunk.Clear();
                currentChunk.Append(overlapText);
                currentChunkStart = currentPosition - overlapText.Length;
            }

            // 如果单个段落就超过最大大小，需要强制分割
            if (paragraphLength > _maxChunkSize)
            {
                var subChunks = SplitLongParagraph(paragraph, _maxChunkSize);
                foreach (var subChunk in subChunks)
                {
                    if (currentChunk.Length + subChunk.Length > _maxChunkSize)
                    {
                        // 保存当前块
                        if (currentChunk.Length > 0)
                        {
                            SaveChunk(chunks, currentChunk.ToString(), source, currentChunkStart, currentPosition);
                            var overlapText = GetOverlapText(currentChunk.ToString());
                            currentChunk.Clear();
                            currentChunk.Append(overlapText);
                            currentChunkStart = currentPosition - overlapText.Length;
                        }
                    }

                    currentChunk.AppendLine(subChunk);
                    currentPosition += subChunk.Length + Environment.NewLine.Length;
                }
            }
            else
            {
                // 添加段落到当前块
                currentChunk.AppendLine(paragraph);
                currentPosition += paragraphLength + Environment.NewLine.Length;
            }
        }

        // 保存最后一块
        if (currentChunk.Length > 0)
        {
            SaveChunk(chunks, currentChunk.ToString(), source, currentChunkStart, currentPosition);
        }

        // 更新 TotalChunks
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].TotalChunks = chunks.Count;
        }

        return chunks;
    }

    /// <summary>
    /// 按段落分割文本
    /// </summary>
    private List<string> SplitIntoParagraphs(string content)
    {
        var paragraphs = new List<string>();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var currentParagraph = new StringBuilder();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                // 遇到空行，结束当前段落
                if (currentParagraph.Length > 0)
                {
                    paragraphs.Add(currentParagraph.ToString().Trim());
                    currentParagraph.Clear();
                }
            }
            else
            {
                if (currentParagraph.Length > 0)
                {
                    currentParagraph.Append(' ');
                }
                currentParagraph.Append(line.Trim());
            }
        }

        // 添加最后一个段落
        if (currentParagraph.Length > 0)
        {
            paragraphs.Add(currentParagraph.ToString().Trim());
        }

        return paragraphs;
    }

    /// <summary>
    /// 分割过长的段落
    /// </summary>
    private List<string> SplitLongParagraph(string paragraph, int maxSize)
    {
        var chunks = new List<string>();
        var sentences = SplitIntoSentences(paragraph);

        var currentChunk = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (sentence.Length > maxSize)
            {
                // 单个句子太长，强制按字符分割
                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString().Trim());
                    currentChunk.Clear();
                }

                for (int i = 0; i < sentence.Length; i += maxSize)
                {
                    int length = Math.Min(maxSize, sentence.Length - i);
                    chunks.Add(sentence.Substring(i, length));
                }
            }
            else if (currentChunk.Length + sentence.Length > maxSize)
            {
                // 保存当前块，开始新块
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
                currentChunk.Append(sentence);
            }
            else
            {
                currentChunk.Append(sentence);
            }
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString().Trim());
        }

        return chunks;
    }

    /// <summary>
    /// 按句子分割文本
    /// </summary>
    private List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();
        var sentenceDelimiters = new[] { '。', '！', '？', '.', '!', '?' };

        var currentSentence = new StringBuilder();

        foreach (var ch in text)
        {
            currentSentence.Append(ch);

            if (Array.IndexOf(sentenceDelimiters, ch) >= 0)
            {
                sentences.Add(currentSentence.ToString());
                currentSentence.Clear();
            }
        }

        if (currentSentence.Length > 0)
        {
            sentences.Add(currentSentence.ToString());
        }

        return sentences;
    }

    /// <summary>
    /// 获取重叠文本（用于块之间的上下文保留）
    /// </summary>
    private string GetOverlapText(string text)
    {
        if (text.Length <= _overlapSize)
        {
            return text;
        }

        // 从末尾取 overlapSize 字符，尝试从完整句子开始
        var overlapStart = text.Length - _overlapSize;
        var overlapText = text.Substring(overlapStart);

        // 尝试找到第一个句号或换行符
        var sentenceStart = overlapText.IndexOfAny(new[] { '。', '\n', '.', '!', '?' });
        if (sentenceStart > 0 && sentenceStart < overlapText.Length - 1)
        {
            return overlapText.Substring(sentenceStart + 1).TrimStart();
        }

        return overlapText;
    }

    /// <summary>
    /// 保存块
    /// </summary>
    private void SaveChunk(List<DocumentChunk> chunks, string text, string source, int startChar, int endChar)
    {
        var trimmedText = text.Trim();
        if (string.IsNullOrEmpty(trimmedText))
        {
            return;
        }

        chunks.Add(new DocumentChunk
        {
            Text = trimmedText,
            Source = source,
            ChunkIndex = chunks.Count,
            TotalChunks = 0, // 稍后更新
            StartChar = startChar,
            EndChar = endChar
        });
    }
}
