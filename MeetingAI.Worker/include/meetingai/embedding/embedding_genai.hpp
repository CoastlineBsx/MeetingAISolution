#pragma once
#include <openvino/genai/rag/text_embedding_pipeline.hpp>
#include <vector>
#include <string>
#include <memory>

namespace meetingai::embedding {

class EmbeddingGenAI {
public:
    EmbeddingGenAI(const std::string& model_path, const std::string& device = "NPU");

    std::vector<float> encode(const std::string& text);

    size_t embedding_dim() const { return embedding_dim_; }

    // 计算文本的 token 数量
    size_t countTokens(const std::string& text);

private:
    std::unique_ptr<ov::genai::TextEmbeddingPipeline> pipeline_;
    std::unique_ptr<ov::genai::Tokenizer> tokenizer_;
    size_t embedding_dim_ = 0;
};

} // namespace meetingai::embedding
