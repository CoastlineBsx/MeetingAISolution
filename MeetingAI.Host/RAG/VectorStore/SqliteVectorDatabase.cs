using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeetingAI.Host.RAG.VectorStore;

public class SqliteVectorDatabase : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public SqliteVectorDatabase(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        var createDocumentsTable = @"
            CREATE TABLE IF NOT EXISTS documents (
                doc_id INTEGER PRIMARY KEY AUTOINCREMENT,
                filename TEXT NOT NULL,
                filepath TEXT,
                file_type TEXT,
                language TEXT,
                upload_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                total_chunks INTEGER DEFAULT 0,
                file_size INTEGER DEFAULT 0,
                has_ocr INTEGER DEFAULT 0
            );";

        var createChunksTable = @"
            CREATE TABLE IF NOT EXISTS document_chunks (
                chunk_id INTEGER PRIMARY KEY AUTOINCREMENT,
                doc_id INTEGER NOT NULL,
                chunk_index INTEGER,
                page_number INTEGER,
                content TEXT NOT NULL,
                embedding BLOB NOT NULL,
                FOREIGN KEY (doc_id) REFERENCES documents(doc_id) ON DELETE CASCADE
            );";

        var createIndexes = @"
            CREATE INDEX IF NOT EXISTS idx_doc_id ON document_chunks(doc_id);";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = createDocumentsTable + createChunksTable + createIndexes;
        await cmd.ExecuteNonQueryAsync();

        // 数据库迁移：添加缺失的列（兼容旧版本数据库）
        await MigrateSchemaAsync();
    }

    /// <summary>
    /// 数据库迁移：为旧表添加新列
    /// </summary>
    private async Task MigrateSchemaAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        // 检查 file_size 列是否存在
        var checkFileSizeColumn = @"
            SELECT COUNT(*) FROM pragma_table_info('documents')
            WHERE name='file_size';";

        using var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = checkFileSizeColumn;
        var fileSizeExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

        if (!fileSizeExists)
        {
            using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE documents ADD COLUMN file_size INTEGER DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync();
        }

        // 检查 has_ocr 列是否存在
        var checkHasOcrColumn = @"
            SELECT COUNT(*) FROM pragma_table_info('documents')
            WHERE name='has_ocr';";

        checkCmd.CommandText = checkHasOcrColumn;
        var hasOcrExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

        if (!hasOcrExists)
        {
            using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE documents ADD COLUMN has_ocr INTEGER DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<long> AddDocumentAsync(string filename, string filepath, string fileType, string language, long fileSize = 0, bool hasOcr = false)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO documents (filename, filepath, file_type, language, file_size, has_ocr)
            VALUES (@filename, @filepath, @fileType, @language, @fileSize, @hasOcr);
            SELECT last_insert_rowid();";

        cmd.Parameters.AddWithValue("@filename", filename);
        cmd.Parameters.AddWithValue("@filepath", filepath);
        cmd.Parameters.AddWithValue("@fileType", fileType);
        cmd.Parameters.AddWithValue("@language", language);
        cmd.Parameters.AddWithValue("@fileSize", fileSize);
        cmd.Parameters.AddWithValue("@hasOcr", hasOcr ? 1 : 0);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task AddChunkAsync(long docId, int chunkIndex, int pageNumber, string content, float[] embedding)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        var embeddingBlob = VectorToBlob(embedding);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO document_chunks (doc_id, chunk_index, page_number, content, embedding)
            VALUES (@docId, @chunkIndex, @pageNumber, @content, @embedding);";

        cmd.Parameters.AddWithValue("@docId", docId);
        cmd.Parameters.AddWithValue("@chunkIndex", chunkIndex);
        cmd.Parameters.AddWithValue("@pageNumber", pageNumber);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@embedding", embeddingBlob);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateDocumentChunkCountAsync(long docId, int totalChunks)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE documents SET total_chunks = @totalChunks WHERE doc_id = @docId;";
        cmd.Parameters.AddWithValue("@totalChunks", totalChunks);
        cmd.Parameters.AddWithValue("@docId", docId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<SearchResult>> SearchAsync(float[] queryVector, int topK = 5)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, doc_id, content, embedding, page_number FROM document_chunks;";

        var results = new List<(long ChunkId, long DocId, string Content, float[] Embedding, int PageNumber, float Similarity)>();

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var chunkId = reader.GetInt64(0);
            var docId = reader.GetInt64(1);
            var content = reader.GetString(2);
            var embeddingBlob = (byte[])reader[3];
            var pageNumber = reader.GetInt32(4);
            var embedding = BlobToVector(embeddingBlob);

            var similarity = CosineSimilarity(queryVector, embedding);
            results.Add((chunkId, docId, content, embedding, pageNumber, similarity));
        }

        var topResults = results.OrderByDescending(r => r.Similarity).Take(topK).ToList();

        var searchResults = new List<SearchResult>();
        foreach (var result in topResults)
        {
            var filename = await GetDocumentFilenameAsync(result.DocId);
            searchResults.Add(new SearchResult
            {
                ChunkId = result.ChunkId,
                DocId = result.DocId,
                Content = result.Content,
                Similarity = result.Similarity,
                PageNumber = result.PageNumber,
                Filename = filename
            });
        }

        return searchResults;
    }

    private async Task<string> GetDocumentFilenameAsync(long docId)
    {
        if (_connection == null)
            return string.Empty;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT filename FROM documents WHERE doc_id = @docId;";
        cmd.Parameters.AddWithValue("@docId", docId);

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? string.Empty;
    }

    public async Task<List<DocumentInfo>> GetAllDocumentsAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        var documents = new List<DocumentInfo>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT doc_id, filename, file_type, language, upload_time, total_chunks, file_size, has_ocr FROM documents ORDER BY upload_time DESC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            documents.Add(new DocumentInfo
            {
                DocId = reader.GetInt64(0),
                Filename = reader.GetString(1),
                FileType = reader.GetString(2),
                Language = reader.GetString(3),
                UploadTime = reader.GetDateTime(4),
                TotalChunks = reader.GetInt32(5),
                FileSize = reader.GetInt64(6),
                HasOcr = reader.GetInt32(7) == 1
            });
        }

        return documents;
    }

    public async Task<DocumentStats> GetDocumentStatsAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as total_docs,
                SUM(total_chunks) as total_chunks,
                SUM(CASE WHEN has_ocr = 1 THEN 1 ELSE 0 END) as ocr_docs
            FROM documents;";

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DocumentStats
            {
                TotalDocuments = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                TotalChunks = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                OcrDocuments = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
            };
        }

        return new DocumentStats();
    }

    public async Task DeleteAllDocumentsAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM documents;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteDocumentAsync(long docId)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE doc_id = @docId;";
        cmd.Parameters.AddWithValue("@docId", docId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static byte[] VectorToBlob(float[] vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        return blob;
    }

    private static float[] BlobToVector(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same length");

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}

public class SearchResult
{
    public long ChunkId { get; set; }
    public long DocId { get; set; }
    public string Content { get; set; } = string.Empty;
    public float Similarity { get; set; }
    public int PageNumber { get; set; }
    public string Filename { get; set; } = string.Empty;
}

public class DocumentInfo
{
    public long DocId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public DateTime UploadTime { get; set; }
    public int TotalChunks { get; set; }
    public long FileSize { get; set; }
    public bool HasOcr { get; set; }

    // UI 显示辅助属性
    public string FileSizeDisplay => FormatFileSize(FileSize);
    public string UploadTimeDisplay => UploadTime.ToString("MM-dd HH:mm");
    public string OcrBadge => HasOcr ? "✓" : "-";

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
        return $"{bytes / (1024 * 1024)}MB";
    }
}

public class DocumentStats
{
    public int TotalDocuments { get; set; }
    public int TotalChunks { get; set; }
    public int OcrDocuments { get; set; }

    public string DisplayText => $"📊 {TotalDocuments}文档 | {TotalChunks}块 | {OcrDocuments}OCR";
}
