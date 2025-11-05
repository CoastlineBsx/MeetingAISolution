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
    try {
        if (text.empty()) {
            throw std::runtime_error("Input text is empty");
        }

        std::cout << "[Embedding] Tokenizing: " << text.substr(0, 50) << "..." << std::endl;

        // 1. Tokenize (返回的 TokenizedInputs 中已经是 ov::Tensor)
        auto encoded = tokenizer_->encode(text);

        std::cout << "[Embedding] Token IDs shape: "
                  << encoded.input_ids.get_shape()[0] << " x "
                  << encoded.input_ids.get_shape()[1] << std::endl;

        // 2. 推理 (直接使用返回的 Tensor，不需要再创建)
        auto infer_request = compiled_model_.create_infer_request();
        infer_request.set_tensor("input_ids", encoded.input_ids);
        infer_request.set_tensor("attention_mask", encoded.attention_mask);

        std::cout << "[Embedding] Running inference..." << std::endl;
        infer_request.infer();

        std::cout << "[Embedding] Inference complete" << std::endl;

        // 3. 获取输出向量
        auto output = infer_request.get_output_tensor(0);
        auto shape = output.get_shape();

        std::cout << "[Embedding] Output shape: [";
        for (size_t i = 0; i < shape.size(); i++) {
            std::cout << shape[i];
            if (i < shape.size() - 1) std::cout << ", ";
        }
        std::cout << "]" << std::endl;

        // 计算总元素数
        size_t total_elements = 1;
        for (auto dim : shape) {
            total_elements *= dim;
        }

        std::cout << "[Embedding] Total elements: " << total_elements << std::endl;
        std::cout << "[Embedding] Expected embedding_dim: " << embedding_dim_ << std::endl;

        // 如果是 [batch_size, seq_len, hidden_dim]，需要做 pooling
        // 如果是 [batch_size, hidden_dim]，直接使用
        // 如果是 [batch_size, seq_len, hidden_dim]，取最后一维

        auto data = output.data<float>();
        std::vector<float> embedding;

        if (shape.size() == 2) {
            // [batch_size, embedding_dim]
            size_t actual_dim = shape[1];
            std::cout << "[Embedding] Using 2D output, dim=" << actual_dim << std::endl;
            embedding.resize(actual_dim);
            std::copy(data, data + actual_dim, embedding.begin());
        }
        else if (shape.size() == 3) {
            // [batch_size, seq_len, hidden_dim]
            // 取 [CLS] token (第一个 token)
            size_t seq_len = shape[1];
            size_t hidden_dim = shape[2];
            std::cout << "[Embedding] Using 3D output, taking [CLS] token, dim=" << hidden_dim << std::endl;
            embedding.resize(hidden_dim);
            std::copy(data, data + hidden_dim, embedding.begin());
        }
        else {
            throw std::runtime_error("Unexpected output shape");
        }

        std::cout << "[Embedding] Extracted embedding vector, size=" << embedding.size() << std::endl;
        return embedding;
    }
    catch (const std::exception& e) {
        std::cerr << "[Embedding] ❌ encode() failed: " << e.what() << std::endl;
        throw;
    }
}

} // namespace meetingai::embedding
