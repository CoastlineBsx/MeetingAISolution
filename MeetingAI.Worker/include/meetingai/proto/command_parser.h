#pragma once
#include <cstdint>
#include <string>
#include <vector>

namespace meetingai::proto {

	struct MeetingHotwordConfig {
		std::string text;
		float score = 2.0f;
	};

	struct MeetingContextCommand {
		std::int64_t preparationId = 0;
		std::string title;
		std::vector<std::int64_t> documentIds;
		std::vector<MeetingHotwordConfig> hotwords;

		bool HasPreparation() const { return preparationId > 0; }
	};

	std::string trim(std::string s);

	// 将字符串做 JSON 转义（仅最基本的控制字符与引号/反斜杠）
	std::string jsonEscape(const std::string& s);

	// 判断是否是 {"type":"quit"}（宽松匹配）
	bool isQuit(const std::string& s);

	// 判断是否是 {"type":"transcribe_file"}（宽松匹配）
	bool isTranscribe(const std::string& s);

	// 判断是否是 {"type":"transcribe_openvino"}（宽松匹配）
	bool isTranscribeOpenVINO(const std::string& s);

	// 从 {"path":"..."} 提取 path；失败返回空
	std::string extractPath(const std::string& json);

	// 从 {"mode":"..."} 提取 mode；失败返回 "auto"
	std::string extractMode(const std::string& json);

	// 从 {"language":"..."} 提取 language；失败返回 "auto"
	std::string extractLanguage(const std::string& json);

	// ==================== 流式转录相关（v1） ====================

	// 判断是否是 {"type":"start_stream"}（宽松匹配）
	bool isStartStream(const std::string& s);

	// 判断是否是 {"type":"stream_chunk"}（宽松匹配）
	bool isStreamChunk(const std::string& s);

	// 判断是否是 {"type":"stop_stream"}（宽松匹配）
	bool isStopStream(const std::string& s);

	// 从 {"data":"..."} 提取 base64 编码的音频数据；失败返回空
	std::string extractData(const std::string& json);

	// 从 {"sample_rate":16000} 提取采样率；失败返回 16000
	int extractSampleRate(const std::string& json);

	// ==================== 流式转录相关（v2：多流） ====================
	// 新增：{"type":"start_stream2","stream_id":"...","source":"near|far","mode":"...","language":"..."}
	bool isStartStream2(const std::string& s);
	bool isStreamChunk2(const std::string& s);
	bool isStopStream2(const std::string& s);

	// 提取 v2 扩展字段
	std::string extractStreamId(const std::string& json);
	std::string extractSource(const std::string& json); // 默认 "unknown"
	long long extractTimestampMs(const std::string& json); // 默认 -1（可选）

	// ==================== Granite GenAI 相关 ====================
	// 从 {"prompt":"..."} 提取 prompt；失败返回空
	std::string extractPrompt(const std::string& json);

	// 从 {"max_tokens":256} 提取 max_tokens；失败返回默认值
	int extractMaxTokens(const std::string& json, int defaultValue = 256);

	// 从 {"temperature":0.7} 提取 temperature；失败返回默认值
	float extractTemperature(const std::string& json, float defaultValue = 0.7f);

	// 从 {"system_message":"..."} 提取 system_message；失败返回默认值
	std::string extractSystemMessage(const std::string& json, const std::string& defaultValue = "");

	// ==================== Sherpa-ONNX 实时流式转录相关 ====================
	// {"type":"start_streaming","sample_rate":16000}
	bool isStartStreaming(const std::string& s);

	// {"type":"streaming_audio","audio_data":"base64...","sample_rate":16000,"is_end":false}
	bool isStreamingAudio(const std::string& s);

	// {"type":"stop_streaming"}
	bool isStopStreaming(const std::string& s);

	// 从 {"audio_data":"..."} 提取 base64 编码的音频数据；失败返回空
	std::string extractAudioData(const std::string& json);

	// 从 {"is_end":true/false} 提取结束标记；失败返回 false
	bool extractIsEnd(const std::string& json);

	// 从 {"translation_mode":"off|auto|to_zh|to_en"} 提取实时翻译模式；
	// 非法值和缺省值统一返回 "off"。
	std::string extractTranslationMode(const std::string& json);

	// 从 start_streaming 提取本地滚动摘要开关；缺省为开启。
	bool extractSummaryEnabled(const std::string& json);
	bool extractRagContextEnabled(const std::string& json);
	bool extractAsrHotwordsEnabled(const std::string& json);

	// 提取一次 Start 所绑定的会议资料快照。文档最多 5 份，热词最多 100 个。
	MeetingContextCommand extractMeetingContext(const std::string& json);

	// 转换为 sherpa-onnx hotwords_buf 格式（每行：词条 :分数）。
	std::string buildSherpaHotwordsBuffer(
		const MeetingContextCommand& context);

	// 生成随 meeting 固化的 JSON，历史会议不依赖之后被修改的资料配置。
	std::string buildMeetingContextSnapshotJson(
		const MeetingContextCommand& context);

	// {"type":"request_meeting_summary"}，用于 UI 手动立即生成一版。
	bool isRequestMeetingSummary(const std::string& json);

	// 会后精修失败后，从数据库中的录音路径重新执行。
	bool isRetryMeetingPostProcess(const std::string& json);
	std::int64_t extractMeetingId(const std::string& json);

} // namespace meetingai::proto
