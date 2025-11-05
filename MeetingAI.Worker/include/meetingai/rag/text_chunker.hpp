#pragma once
#include <string>
#include <vector>

namespace meetingai::rag {

struct Chunk {
    std::string text;
    int start_pos;
    int end_pos;
};

// 简单分块：按句号、换行符切分，每块约 300-500 字符
std::vector<Chunk> chunkText(const std::string& text, int max_chars = 400);

} // namespace meetingai::rag
