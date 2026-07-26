using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MeetingAI.Host.RAG.Models;

/// <summary>
/// 信息提取（IE）的结构化结果
/// </summary>
public class ExtractedInfo
{
    [JsonPropertyName("document_id")]
    public long DocumentId { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("extraction_time")]
    public DateTime ExtractionTime { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("entities")]
    public EntityInfo? Entities { get; set; }

    [JsonPropertyName("key_points")]
    public List<KeyPoint> KeyPoints { get; set; } = new();

    [JsonPropertyName("facts")]
    public List<Fact> Facts { get; set; } = new();

    [JsonPropertyName("document_type")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    [JsonPropertyName("metadata")]
    public MetadataInfo? Metadata { get; set; }
}

/// <summary>
/// 文档摘要信息
/// </summary>
public class SummaryInfo
{
    [JsonPropertyName("brief")]
    public string Brief { get; set; } = string.Empty;  // 50-100字简短摘要

    [JsonPropertyName("detailed")]
    public string Detailed { get; set; } = string.Empty;  // 200-300字详细摘要
}

/// <summary>
/// 实体信息
/// </summary>
public class EntityInfo
{
    [JsonPropertyName("persons")]
    public List<string> Persons { get; set; } = new();

    [JsonPropertyName("organizations")]
    public List<string> Organizations { get; set; } = new();

    [JsonPropertyName("locations")]
    public List<string> Locations { get; set; } = new();

    [JsonPropertyName("dates")]
    public List<string> Dates { get; set; } = new();

    [JsonPropertyName("numbers")]
    public List<NumberEntity> Numbers { get; set; } = new();
}

/// <summary>
/// 数字实体（数字+上下文）
/// </summary>
public class NumberEntity
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;
}

/// <summary>
/// 关键观点
/// </summary>
public class KeyPoint
{
    [JsonPropertyName("point")]
    public string Point { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = string.Empty;

    [JsonPropertyName("importance")]
    public string Importance { get; set; } = "medium";  // high/medium/low
}

/// <summary>
/// 事实信息
/// </summary>
public class Fact
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;  // 数据/事件/结论/其他

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("source_location")]
    public string SourceLocation { get; set; } = string.Empty;
}

/// <summary>
/// 元数据信息
/// </summary>
public class MetadataInfo
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh-CN";

    [JsonPropertyName("word_count")]
    public int WordCount { get; set; }

    [JsonPropertyName("confidence")]
    public float Confidence { get; set; }  // 0-1，提取置信度
}
