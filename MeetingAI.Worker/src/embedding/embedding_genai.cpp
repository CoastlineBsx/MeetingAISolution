#include "pch.h"
#include "embedding_genai.hpp"
#include <iostream>
#include <stdexcept>

namespace meetingai::embedding {

EmbeddingGenAI::EmbeddingGenAI(const std::string& model_path, const std::string& device) {
    try {
        // 加载 Tokenizer
        tokenizer_ = std::make_unique<ov::genai::Tokenizer>(model_path);
        
        // 加载模型
        std::string model_file = model_path + "/openvino_model.xml";
        auto model = core_.read_model(model_file);
        compiled_model_ = core_.compile_model(model, device);
        
        // bge-m3 固定维度为 1024
        embedding_dim_ = 1024;
        
        std::cout << "[Embedding GenAI] ✅ Initialized on " << device 
                  << ", dim=" << embedding_dim_ << std::endl;
    } catch (const std::exception& e) {
        std::cerr << "[Embedding GenAI] ❌ Failed: " << e.what() << std::endl;
        throw;
    }
}

std::vector<float> EmbeddingGenAI::encode(const std::string& text) {
    // 1. Tokenize (返回的 TokenizedInputs 中已经是 ov::Tensor)
    auto encoded = tokenizer_->encode(text);
    
    // 2. 推理 (直接使用返回的 Tensor，不需要再创建)
    auto infer_request = compiled_model_.create_infer_request();
    infer_request.set_tensor("input_ids", encoded.input_ids);
    infer_request.set_tensor("attention_mask", encoded.attention_mask);
    infer_request.infer();
    
    // 3. 获取输出向量 [batch_size, embedding_dim]
    // sentence-transformers 转换的模型已经做了 mean pooling 和 L2 归一化
    auto output = infer_request.get_output_tensor(0);
    std::vector<float> embedding(embedding_dim_);
    auto data = output.data<float>();
    std::copy(data, data + embedding_dim_, embedding.begin());
    
    return embedding;
}

} // namespace meetingai::embedding
