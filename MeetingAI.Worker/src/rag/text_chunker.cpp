#include "pch.h"
#include "rag/text_chunker.hpp"
#include <algorithm>

namespace meetingai::rag {

std::vector<Chunk> chunkText(const std::string& text, int max_chars) {
    std::vector<Chunk> chunks;

    // 按句号、问号、感叹号、换行符分割
    std::vector<std::string> sentences;
    std::string current;

    for (size_t i = 0; i < text.size(); i++) {
        current += text[i];

        // 中文或英文句尾标点
        if (text[i] == '。' || text[i] == '!' || text[i] == '?' ||
            text[i] == '.' || text[i] == '\n' || text[i] == '；') {
            if (!current.empty()) {
                sentences.push_back(current);
                current.clear();
            }
        }
    }
    if (!current.empty()) {
        sentences.push_back(current);
    }

    // 合并成块（每块不超过 max_chars）
    std::string chunk_text;
    int start = 0;

    for (const auto& sent : sentences) {
        if (chunk_text.size() + sent.size() > max_chars && !chunk_text.empty()) {
            // 保存当前块
            Chunk c;
            c.text = chunk_text;
            c.start_pos = start;
            c.end_pos = start + (int)chunk_text.size();
            chunks.push_back(c);

            start = c.end_pos;
            chunk_text = sent;
        } else {
            chunk_text += sent;
        }
    }

    // 最后一块
    if (!chunk_text.empty()) {
        Chunk c;
        c.text = chunk_text;
        c.start_pos = start;
        c.end_pos = start + (int)chunk_text.size();
        chunks.push_back(c);
    }

    return chunks;
}

} // namespace meetingai::rag
