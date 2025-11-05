#include "pch.h"
#include "rag/text_chunker.hpp"
#include <algorithm>
#include <cctype>

namespace meetingai::rag {

// 辅助函数：判断是否是句子结束符
static bool isSentenceEnd(char c, size_t pos, const std::string& text) {
    // 中文标点
    if (c == '\xE3' && pos + 2 < text.size()) {
        // 检查中文句号 。 (E3 80 82)
        if (text[pos + 1] == '\x80' && text[pos + 2] == '\x82') return true;
        // 检查中文问号 ？ (E3 80 81) 或感叹号 ！ (EF BC 81)
    }

    // 英文标点（需要后面跟空格或结尾）
    if (c == '.' || c == '!' || c == '?') {
        if (pos + 1 >= text.size() || text[pos + 1] == ' ' || text[pos + 1] == '\n') {
            return true;
        }
    }

    // 换行也算句子结束
    if (c == '\n') return true;

    return false;
}

// 改进的分块函数：滑动窗口 + 句子边界
std::vector<Chunk> chunkText(const std::string& text, int max_chars) {
    std::vector<Chunk> chunks;

    if (text.empty()) return chunks;

    // 参数调整
    const int min_chars = max_chars / 2;  // 最小块大小
    const int overlap = max_chars / 4;     // 重叠大小（滑动窗口）

    // 1. 首先按句子分割
    std::vector<std::string> sentences;
    std::string current;

    for (size_t i = 0; i < text.size(); i++) {
        current += text[i];

        if (isSentenceEnd(text[i], i, text)) {
            // 移除前后 ASCII 空白（避免破坏 UTF-8 多字节字符）
            while (!current.empty() && current.front() <= ' ') {
                current.erase(0, 1);
            }
            while (!current.empty() && current.back() <= ' ') {
                current.pop_back();
            }

            if (!current.empty()) {
                sentences.push_back(current);
                current.clear();
            }
        }
    }

    // 剩余部分
    if (!current.empty()) {
        // 移除前后 ASCII 空白（避免破坏 UTF-8 多字节字符）
        while (!current.empty() && current.front() <= ' ') {
            current.erase(0, 1);
        }
        while (!current.empty() && current.back() <= ' ') {
            current.pop_back();
        }
        if (!current.empty()) {
            sentences.push_back(current);
        }
    }

    if (sentences.empty()) return chunks;

    // 2. 滑动窗口合并句子
    size_t sent_idx = 0;
    int global_pos = 0;

    while (sent_idx < sentences.size()) {
        std::string chunk_text;
        int chunk_start = global_pos;
        size_t start_sent_idx = sent_idx;

        // 累积句子直到达到 max_chars
        while (sent_idx < sentences.size()) {
            const auto& sent = sentences[sent_idx];

            // 如果加上这句话超过最大长度，且已经有内容了
            if (!chunk_text.empty() && chunk_text.size() + sent.size() + 1 > max_chars) {
                break;
            }

            if (!chunk_text.empty()) {
                chunk_text += " ";
            }
            chunk_text += sent;
            sent_idx++;

            // 如果已经达到最小长度，可以考虑结束
            if (chunk_text.size() >= min_chars) {
                break;
            }
        }

        // 创建块
        if (!chunk_text.empty()) {
            Chunk c;
            c.text = chunk_text;
            c.start_pos = chunk_start;
            c.end_pos = chunk_start + (int)chunk_text.size();
            chunks.push_back(c);

            global_pos = c.end_pos;
        }

        // 滑动窗口：回退一些句子以创建重叠
        if (sent_idx < sentences.size()) {
            // 计算回退多少句子以达到 overlap 重叠
            int overlap_chars = 0;
            size_t backtrack = 0;

            while (backtrack < (sent_idx - start_sent_idx) && overlap_chars < overlap) {
                backtrack++;
                overlap_chars += sentences[sent_idx - backtrack].size();
            }

            if (backtrack > 0) {
                sent_idx -= backtrack;
                global_pos -= overlap_chars;
            }
        }
    }

    return chunks;
}

} // namespace meetingai::rag
