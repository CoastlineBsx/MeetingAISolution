#pragma once
#include <openvino/openvino.hpp>
#include <openvino/genai/tokenizer.hpp>
#include <vector>
#include <string>
#include <memory>

namespace meetingai::embedding {

class EmbeddingGenAI {
public:
    EmbeddingGenAI(const std::string& model_path, const std::string& device = "NPU");
    
    std::vector<float> encode(const std::string& text);
    
    size_t embedding_dim() const { return embedding_dim_; }
    
private:
    ov::Core core_;
    ov::CompiledModel compiled_model_;
    std::unique_ptr<ov::genai::Tokenizer> tokenizer_;
    size_t embedding_dim_;
};

} // namespace meetingai::embedding
