using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeetingAI.Host.MeetingPreparation;

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

        var createPreparationTables = @"
            CREATE TABLE IF NOT EXISTS meeting_preparations (
                preparation_id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'draft',
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS preparation_materials (
                preparation_id INTEGER NOT NULL,
                doc_id INTEGER NOT NULL,
                page_count INTEGER NOT NULL DEFAULT 0,
                material_role TEXT NOT NULL DEFAULT 'reference',
                PRIMARY KEY (preparation_id, doc_id),
                FOREIGN KEY (preparation_id) REFERENCES meeting_preparations(preparation_id) ON DELETE CASCADE,
                FOREIGN KEY (doc_id) REFERENCES documents(doc_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS meeting_hotwords (
                hotword_id INTEGER PRIMARY KEY AUTOINCREMENT,
                preparation_id INTEGER NOT NULL,
                text TEXT NOT NULL,
                normalized_text TEXT NOT NULL,
                score REAL NOT NULL DEFAULT 2.0,
                enabled INTEGER NOT NULL DEFAULT 1,
                source_pages TEXT,
                source_kind TEXT,
                UNIQUE (preparation_id, normalized_text),
                FOREIGN KEY (preparation_id) REFERENCES meeting_preparations(preparation_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_preparation_materials_doc ON preparation_materials(doc_id);
            CREATE INDEX IF NOT EXISTS idx_hotwords_preparation ON meeting_hotwords(preparation_id);";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;" + createDocumentsTable + createChunksTable +
                          createPreparationTables + createIndexes;
        await cmd.ExecuteNonQueryAsync();

        // 数据库迁移：添加缺失的列（兼容旧版本数据库）
        await MigrateSchemaAsync();
    }

    public async Task<long> CreatePreparationAsync(string title)
    {
        EnsureInitialized();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"INSERT INTO meeting_preparations(title) VALUES (@title);
                            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@title", string.IsNullOrWhiteSpace(title) ? "未命名会议" : title.Trim());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<MeetingPreparationInfo>> GetPreparationsAsync()
    {
        EnsureInitialized();
        var result = new List<MeetingPreparationInfo>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT p.preparation_id, p.title, p.status, p.created_at, p.updated_at,
                   COUNT(DISTINCT pm.doc_id),
                   COUNT(DISTINCT CASE WHEN h.enabled=1 THEN h.hotword_id END)
            FROM meeting_preparations p
            LEFT JOIN preparation_materials pm ON pm.preparation_id=p.preparation_id
            LEFT JOIN meeting_hotwords h ON h.preparation_id=p.preparation_id
            GROUP BY p.preparation_id
            ORDER BY p.updated_at DESC, p.preparation_id DESC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new MeetingPreparationInfo
            {
                PreparationId = reader.GetInt64(0),
                Title = reader.GetString(1),
                Status = reader.GetString(2),
                CreatedAt = reader.GetDateTime(3),
                UpdatedAt = reader.GetDateTime(4),
                MaterialCount = reader.GetInt32(5),
                EnabledHotwordCount = reader.GetInt32(6)
            });
        }
        return result;
    }

    public async Task<int> GetPreparationMaterialCountAsync(long preparationId)
    {
        EnsureInitialized();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM preparation_materials WHERE preparation_id=@preparationId;";
        cmd.Parameters.AddWithValue("@preparationId", preparationId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<long>> GetPreparationDocumentIdsAsync(long preparationId)
    {
        EnsureInitialized();
        var result = new List<long>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT doc_id FROM preparation_materials WHERE preparation_id=@preparationId ORDER BY doc_id;";
        cmd.Parameters.AddWithValue("@preparationId", preparationId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task AttachDocumentToPreparationAsync(long preparationId, long docId, int pageCount)
    {
        EnsureInitialized();
        if (await GetPreparationMaterialCountAsync(preparationId) >= 5)
            throw new InvalidOperationException("一场会议最多只能绑定 5 份资料");
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"INSERT INTO preparation_materials(preparation_id, doc_id, page_count)
                            VALUES(@preparationId, @docId, @pageCount)
                            ON CONFLICT(preparation_id, doc_id) DO UPDATE SET page_count=excluded.page_count;
                            UPDATE meeting_preparations SET updated_at=CURRENT_TIMESTAMP
                            WHERE preparation_id=@preparationId;";
        cmd.Parameters.AddWithValue("@preparationId", preparationId);
        cmd.Parameters.AddWithValue("@docId", docId);
        cmd.Parameters.AddWithValue("@pageCount", pageCount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<MeetingMaterialInfo>> GetPreparationMaterialsAsync(long preparationId)
    {
        EnsureInitialized();
        var result = new List<MeetingMaterialInfo>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"SELECT d.doc_id, d.filename, d.file_type, pm.page_count,
                                   d.total_chunks, d.has_ocr
                            FROM preparation_materials pm
                            JOIN documents d ON d.doc_id=pm.doc_id
                            WHERE pm.preparation_id=@preparationId
                            ORDER BY d.upload_time;";
        cmd.Parameters.AddWithValue("@preparationId", preparationId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new MeetingMaterialInfo
            {
                DocumentId = reader.GetInt64(0),
                FileName = reader.GetString(1),
                FileType = reader.GetString(2),
                PageCount = reader.GetInt32(3),
                ChunkCount = reader.GetInt32(4),
                UsedOcr = reader.GetInt32(5) != 0
            });
        }
        return result;
    }

    public async Task SaveHotwordsAsync(long preparationId, IEnumerable<HotwordCandidate> hotwords)
    {
        EnsureInitialized();
        using var transaction = _connection!.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM meeting_hotwords WHERE preparation_id=@preparationId;";
            delete.Parameters.AddWithValue("@preparationId", preparationId);
            await delete.ExecuteNonQueryAsync();
        }
        foreach (var hotword in hotwords.Where(item => !string.IsNullOrWhiteSpace(item.Text)))
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO meeting_hotwords
                (preparation_id, text, normalized_text, score, enabled, source_pages, source_kind)
                VALUES(@preparationId, @text, @normalized, @score, @enabled, @pages, @kind);";
            insert.Parameters.AddWithValue("@preparationId", preparationId);
            insert.Parameters.AddWithValue("@text", hotword.Text.Trim());
            insert.Parameters.AddWithValue("@normalized", hotword.Text.Trim().ToLowerInvariant());
            insert.Parameters.AddWithValue("@score", hotword.Score);
            insert.Parameters.AddWithValue("@enabled", hotword.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("@pages", string.Join(",", hotword.SourcePages));
            insert.Parameters.AddWithValue("@kind", hotword.SourceKind);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<List<HotwordCandidate>> GetHotwordsAsync(long preparationId)
    {
        EnsureInitialized();
        var result = new List<HotwordCandidate>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"SELECT hotword_id, text, score, enabled, source_pages, source_kind
                            FROM meeting_hotwords WHERE preparation_id=@preparationId
                            ORDER BY score DESC, text;";
        cmd.Parameters.AddWithValue("@preparationId", preparationId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var pages = reader.IsDBNull(4) ? Array.Empty<int>() : reader.GetString(4).Split(',')
                .Select(value => int.TryParse(value, out var number) ? number : 0).Where(number => number > 0).ToArray();
            result.Add(new HotwordCandidate
            {
                HotwordId = reader.GetInt64(0),
                Text = reader.GetString(1),
                Score = reader.GetDouble(2),
                Enabled = reader.GetInt32(3) != 0,
                SourcePages = pages.ToList(),
                SourceKind = reader.IsDBNull(5) ? "rule" : reader.GetString(5)
            });
        }
        return result;
    }

    private void EnsureInitialized()
    {
        if (_connection == null) throw new InvalidOperationException("Database not initialized");
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

    public async Task<List<SearchResult>> SearchPreparationAsync(
        long preparationId,
        float[] queryVector,
        int topK = 5)
    {
        EnsureInitialized();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT c.chunk_id, c.doc_id, c.content, c.embedding, c.page_number, d.filename
            FROM document_chunks c
            JOIN preparation_materials pm ON pm.doc_id=c.doc_id
            JOIN documents d ON d.doc_id=c.doc_id
            WHERE pm.preparation_id=@preparationId;";
        cmd.Parameters.AddWithValue("@preparationId", preparationId);

        var results = new List<SearchResult>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var embedding = BlobToVector((byte[])reader[3]);
            results.Add(new SearchResult
            {
                ChunkId = reader.GetInt64(0),
                DocId = reader.GetInt64(1),
                Content = reader.GetString(2),
                Similarity = CosineSimilarity(queryVector, embedding),
                PageNumber = reader.GetInt32(4),
                Filename = reader.GetString(5)
            });
        }
        return results.OrderByDescending(item => item.Similarity).Take(topK).ToList();
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
