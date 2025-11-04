#pragma once
#include <string>
#include <vector>
#include <memory>
#include <curl/curl.h>
#include <nlohmann/json.hpp>

namespace meetingai::embedding {

// HTTP 客户端封装
class HttpClient {
public:
    HttpClient(const std::string& base_url);
    std::string post(const std::string& endpoint, const nlohmann::json& data);
    
private:
    std::string base_url_;
    CURL* curl_;
    
    static size_t WriteCallback(void* contents, size_t size, size_t nmemb, void* userp);
};

// 使用 Python bge-m3 服务
class EmbeddingPythonService {
public:
    EmbeddingPythonService(const std::string& service_url = "http://127.0.0.1:8081");
    
    std::vector<float> encode(const std::string& text);
    std::vector<std::vector<float>> encodeBatch(const std::vector<std::string>& texts);
    
    size_t embedding_dim() const { return 1024; } // bge-m3 固定 1024 维
    
private:
    std::unique_ptr<HttpClient> http_client_;
};

} // namespace meetingai::embedding
