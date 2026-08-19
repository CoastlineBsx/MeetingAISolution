#include "pch.h"
#include "embedding_genai.hpp"
#include "paths.h"

#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <system_error>

namespace fs = std::filesystem;

namespace meetingai::embedding {
namespace {

struct XmlRemovalRange {
    std::size_t begin = 0;
    std::size_t end = 0;
};

std::string ReadTextFile(const fs::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        throw std::runtime_error(
            "Unable to open embedding model XML: " +
            path.string());
    }
    return {
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
}

void WriteTextFile(
    const fs::path& path,
    const std::string& content) {
    std::ofstream output(
        path,
        std::ios::binary | std::ios::trunc);
    if (!output) {
        throw std::runtime_error(
            "Unable to create embedding model adapter: " +
            path.string());
    }
    output.write(
        content.data(),
        static_cast<std::streamsize>(content.size()));
    if (!output) {
        throw std::runtime_error(
            "Unable to write embedding model adapter: " +
            path.string());
    }
}

std::string ExtractXmlAttribute(
    const std::string& openingTag,
    const std::string& name) {
    const std::string marker = name + "=\"";
    const auto valueStart = openingTag.find(marker);
    if (valueStart == std::string::npos) {
        return {};
    }
    const auto first = valueStart + marker.size();
    const auto last = openingTag.find('"', first);
    return last == std::string::npos
        ? std::string{}
        : openingTag.substr(first, last - first);
}

std::uint64_t StablePathHash(const std::string& value) {
    std::uint64_t result = 1469598103934665603ULL;
    for (const unsigned char byte : value) {
        result ^= byte;
        result *= 1099511628211ULL;
    }
    return result;
}

fs::path PrepareTextEmbeddingPipelineModel(
    const std::string& modelPath) {
    const fs::path sourceRoot(
        meetingai::util::utf8ToW(modelPath));
    const fs::path sourceXml =
        sourceRoot / L"openvino_model.xml";
    std::string xml = ReadTextFile(sourceXml);

    struct ResultLayer {
        std::size_t begin = 0;
        std::size_t end = 0;
        std::string id;
        bool tokenEmbeddings = false;
    };
    std::vector<ResultLayer> results;
    std::size_t position = 0;
    while ((position = xml.find("<layer", position)) !=
           std::string::npos) {
        const auto openingEnd = xml.find('>', position);
        if (openingEnd == std::string::npos) {
            break;
        }
        const std::string openingTag =
            xml.substr(position, openingEnd - position + 1);
        if (openingTag.find("type=\"Result\"") ==
            std::string::npos) {
            position = openingEnd + 1;
            continue;
        }

        const auto closing = xml.find("</layer>", openingEnd);
        if (closing == std::string::npos) {
            throw std::runtime_error(
                "Malformed embedding model XML Result layer");
        }
        const auto layerEnd =
            closing + std::string("</layer>").size();
        const std::string block =
            xml.substr(position, layerEnd - position);
        results.push_back({
            position,
            layerEnd,
            ExtractXmlAttribute(openingTag, "id"),
            block.find("output_names=\"token_embeddings\"") !=
                std::string::npos});
        position = layerEnd;
    }

    // TextEmbeddingPipeline requires one model output. Current Optimum
    // exports can contain both token_embeddings and sentence_embedding.
    // Keep token_embeddings so the GenAI pipeline owns CLS pooling and
    // normalization, while reusing the original weights through hard links.
    if (results.size() <= 1) {
        return sourceRoot;
    }
    const auto keep = std::find_if(
        results.begin(),
        results.end(),
        [](const ResultLayer& result) {
            return result.tokenEmbeddings;
        });
    if (keep == results.end()) {
        throw std::runtime_error(
            "Embedding model has multiple outputs but no "
            "token_embeddings output for TextEmbeddingPipeline");
    }

    std::vector<std::string> removedResultIds;
    std::vector<XmlRemovalRange> removals;
    for (const ResultLayer& result : results) {
        if (&result == &*keep) {
            continue;
        }
        if (result.id.empty()) {
            throw std::runtime_error(
                "Embedding model Result layer has no id");
        }
        removedResultIds.push_back(result.id);
        removals.push_back({result.begin, result.end});
    }

    position = 0;
    while ((position = xml.find("<edge", position)) !=
           std::string::npos) {
        const auto edgeEndMarker = xml.find("/>", position);
        if (edgeEndMarker == std::string::npos) {
            break;
        }
        const auto edgeEnd = edgeEndMarker + 2;
        const std::string edge =
            xml.substr(position, edgeEnd - position);
        const bool targetsRemovedResult = std::any_of(
            removedResultIds.begin(),
            removedResultIds.end(),
            [&edge](const std::string& id) {
                return edge.find(
                    "to-layer=\"" + id + "\"") !=
                    std::string::npos;
            });
        if (targetsRemovedResult) {
            removals.push_back({position, edgeEnd});
        }
        position = edgeEnd;
    }

    std::sort(
        removals.begin(),
        removals.end(),
        [](const XmlRemovalRange& left,
           const XmlRemovalRange& right) {
            return left.begin < right.begin;
        });
    std::string adaptedXml;
    adaptedXml.reserve(xml.size());
    position = 0;
    for (const XmlRemovalRange& range : removals) {
        if (range.begin < position) {
            continue;
        }
        adaptedXml.append(xml, position, range.begin - position);
        position = range.end;
    }
    adaptedXml.append(xml, position, std::string::npos);
    const std::string exportedOutputName = "token_embeddings";
    const std::string pipelineOutputName = "last_hidden_state";
    position = 0;
    while ((position = adaptedXml.find(
                exportedOutputName,
                position)) != std::string::npos) {
        adaptedXml.replace(
            position,
            exportedOutputName.size(),
            pipelineOutputName);
        position += pipelineOutputName.size();
    }

    std::error_code pathError;
    const fs::path canonicalSource =
        fs::weakly_canonical(sourceRoot, pathError);
    const auto hashPath =
        (pathError ? sourceRoot : canonicalSource)
            .generic_u8string();
    const std::string hashInput(
        reinterpret_cast<const char*>(hashPath.data()),
        hashPath.size());
    std::ostringstream cacheName;
    cacheName << "embedding-genai-" << std::hex
              << StablePathHash(hashInput);
    const fs::path dataCacheRoot =
        fs::path(meetingai::util::utf8ToW(
            meetingai::util::getDataRoot())) /
        L"model_cache" /
        meetingai::util::utf8ToW(cacheName.str());

    // Windows hard links cannot cross volumes. Portable builds are often
    // extracted to D: while LOCALAPPDATA remains on C:, so keep the adapter
    // cache beside the model whenever the roots differ. If that location is
    // not writable, the copy fallback below still allows initialization.
    fs::path cacheRoot = dataCacheRoot;
    auto volumeName = [](const fs::path& path) {
        std::wstring value = path.root_name().wstring();
        std::transform(
            value.begin(), value.end(), value.begin(),
            [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); });
        return value;
    };
    if (!sourceRoot.root_name().empty() &&
        volumeName(sourceRoot) != volumeName(dataCacheRoot)) {
        const fs::path colocatedCache =
            sourceRoot.parent_path() /
            L".meetingai-cache" /
            meetingai::util::utf8ToW(cacheName.str());
        std::error_code colocatedError;
        fs::create_directories(colocatedCache, colocatedError);
        if (!colocatedError) {
            cacheRoot = colocatedCache;
        }
    }

    std::error_code cacheError;
    fs::create_directories(cacheRoot, cacheError);
    if (cacheError) {
        throw std::runtime_error(
            "Unable to create embedding model cache: " +
            cacheError.message());
    }

    for (const auto& entry : fs::directory_iterator(sourceRoot)) {
        if (!entry.is_regular_file() ||
            entry.path().filename() == L"openvino_model.xml") {
            continue;
        }
        const fs::path destination =
            cacheRoot / entry.path().filename();
        std::error_code equivalentError;
        if (fs::exists(destination)) {
            if (fs::equivalent(
                    entry.path(),
                    destination,
                    equivalentError) &&
                !equivalentError) {
                continue;
            }

            std::error_code sourceMetadataError;
            std::error_code destinationMetadataError;
            const auto sourceSize =
                fs::file_size(entry.path(), sourceMetadataError);
            const auto destinationSize =
                fs::file_size(destination, destinationMetadataError);
            if (!sourceMetadataError && !destinationMetadataError &&
                sourceSize == destinationSize) {
                std::error_code sourceTimeError;
                std::error_code destinationTimeError;
                const auto sourceTime =
                    fs::last_write_time(entry.path(), sourceTimeError);
                const auto destinationTime =
                    fs::last_write_time(destination, destinationTimeError);
                if (!sourceTimeError && !destinationTimeError &&
                    sourceTime == destinationTime) {
                    continue;
                }
            }
        }

        std::error_code operationError;
        fs::remove(destination, operationError);
        operationError.clear();
        fs::create_hard_link(
            entry.path(),
            destination,
            operationError);
        if (!operationError) {
            continue;
        }

        std::cerr
            << "[Embedding GenAI] Hard link unavailable for "
            << entry.path().filename().string()
            << "; copying into the model cache instead."
            << std::endl;
        operationError.clear();
        fs::copy_file(
            entry.path(),
            destination,
            fs::copy_options::overwrite_existing,
            operationError);
        if (operationError) {
            throw std::runtime_error(
                "Unable to prepare TextEmbeddingPipeline files: " +
                operationError.message());
        }

        std::error_code sourceTimeError;
        const auto sourceTime =
            fs::last_write_time(entry.path(), sourceTimeError);
        if (!sourceTimeError) {
            std::error_code destinationTimeError;
            fs::last_write_time(
                destination, sourceTime, destinationTimeError);
        }
    }

    const fs::path temporaryXml =
        cacheRoot / L"openvino_model.xml.tmp";
    const fs::path adaptedModelXml =
        cacheRoot / L"openvino_model.xml";
    WriteTextFile(temporaryXml, adaptedXml);
    std::error_code replaceError;
    fs::remove(adaptedModelXml, replaceError);
    replaceError.clear();
    fs::rename(temporaryXml, adaptedModelXml, replaceError);
    if (replaceError) {
        throw std::runtime_error(
            "Unable to activate TextEmbeddingPipeline model adapter: " +
            replaceError.message());
    }
    return cacheRoot;
}

} // namespace

EmbeddingGenAI::EmbeddingGenAI(const std::string& model_path, const std::string& device) {
    try {
        // TextEmbeddingPipeline 统一处理分词、池化和 L2 归一化。旧实现直接
        // 取第一个 token，容易与模型导出配置不一致，也没有统一归一化。
        ov::genai::TextEmbeddingPipeline::Config config;
        config.pooling_type =
            ov::genai::TextEmbeddingPipeline::PoolingType::CLS;
        config.normalize = true;
        const fs::path pipelineModelPath =
            PrepareTextEmbeddingPipelineModel(model_path);
        pipeline_ =
            std::make_unique<ov::genai::TextEmbeddingPipeline>(
                pipelineModelPath,
                device,
                config);

        // token 计数仍复用同目录 tokenizer，不再自行执行模型推理。
        tokenizer_ = std::make_unique<ov::genai::Tokenizer>(model_path);
        const auto probe = pipeline_->embed_query("dimension probe");
        if (!std::holds_alternative<std::vector<float>>(probe)) {
            throw std::runtime_error(
                "Embedding pipeline returned a quantized vector; "
                "the current vector store requires float32");
        }
        embedding_dim_ = std::get<std::vector<float>>(probe).size();
        if (embedding_dim_ == 0) {
            throw std::runtime_error("Embedding pipeline returned an empty vector");
        }

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
        if (!pipeline_) {
            throw std::runtime_error("TextEmbeddingPipeline is not initialized");
        }

        auto result = pipeline_->embed_query(text);
        if (!std::holds_alternative<std::vector<float>>(result)) {
            throw std::runtime_error(
                "Embedding pipeline returned a non-float vector");
        }
        auto embedding =
            std::get<std::vector<float>>(std::move(result));
        if (embedding.size() != embedding_dim_) {
            throw std::runtime_error(
                "Embedding dimension changed from " +
                std::to_string(embedding_dim_) + " to " +
                std::to_string(embedding.size()));
        }
        return embedding;
    }
    catch (const std::exception& e) {
        std::cerr << "[Embedding] ❌ encode() failed: " << e.what() << std::endl;
        throw;
    }
}

size_t EmbeddingGenAI::countTokens(const std::string& text) {
    if (!tokenizer_) {
        throw std::runtime_error("Tokenizer not initialized");
    }

    if (text.empty()) {
        return 0;
    }

    try {
        // 使用 tokenizer 进行编码并获取 token 数量
        auto encoded = tokenizer_->encode(text);
        return encoded.input_ids.get_shape()[1];  // shape 是 [batch_size, sequence_length]
    }
    catch (const std::exception& e) {
        std::cerr << "[Embedding] ❌ countTokens() failed: " << e.what() << std::endl;
        throw;
    }
}

} // namespace meetingai::embedding
