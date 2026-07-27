using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private Process? _worker;
    private NamedPipeClientStream? _pipe;   // 复用的管道连接
    private StreamReader? _reader;          // 复用的 Reader
    private Task? _readLoopTask;            // 后台读循环
    private CancellationTokenSource? _pipeCts;

    // 本次转录的“完成”信号（收到 complete/error 时置位）
    private TaskCompletionSource<bool>? _transcribeTcs;

    // 扬声器回放录音相关
    private WasapiLoopbackCapture? _loopback;
    private WaveFileWriter? _loopbackWriter;
    private string? _loopbackTempFile;
    private bool _isLoopback;

    // 麦克风录音相关
    private WasapiCapture? _microphone;
    private WaveFileWriter? _microphoneWriter;
    private string? _microphoneTempFile;
    private bool _isMicrophone;
    private string? _selectedMicrophoneId;
    private string? _selectedLoopbackDeviceId;
    private string? _selectedMeetingSpeakerId;
    private string? _selectedMeetingBetaSpeakerId;
    private string? _selectedMeetingBeta2SpeakerId;
    private string? _selectedStreamingSpeakerId;

    // 综合转录（方案B：定时器同步）相关
    private WasapiCapture? _meetingMicrophone;
    private WasapiLoopbackCapture? _meetingLoopback;
    private WaveFileWriter? _meetingMicrophoneWriter;
    private WaveFileWriter? _meetingLoopbackWriter;
    private string? _meetingMicrophoneTempFile;
    private string? _meetingLoopbackTempFile;
    private bool _isMeeting;
    private string? _selectedMeetingMicrophoneId;
    private System.Timers.Timer? _meetingSyncTimer;
    private readonly object _meetingSyncLock = new object();
    private DateTime _meetingStartTime;
    private DateTime _meetingLoopbackLastDataTime;
    private long _meetingLoopbackTotalBytes;
    private int _meetingSyncFillCount;
    private bool _meetingLoopbackHasData; // 标志：扬声器是否收到过真实数据
    private DateTime _meetingLoopbackLastActiveTime; // VAD：最近一次检测到有声的时间
    private bool _meetingLoopbackFirstVoiceLogged;   // VAD：是否已记录首段有声日志
    private float _meetingVadPeakThreshold = 0.001f; // VAD 阈值

    // 综合转录Beta（方案A：事后对齐）相关
    private WasapiCapture? _meetingBetaMicrophone;
    private WasapiLoopbackCapture? _meetingBetaLoopback;
    private WaveFileWriter? _meetingBetaMicrophoneWriter;
    private WaveFileWriter? _meetingBetaLoopbackWriter;
    private string? _meetingBetaMicrophoneTempFile;
    private string? _meetingBetaLoopbackTempFile;
    private bool _isMeetingBeta;
    private string? _selectedMeetingBetaMicrophoneId;

    // Beta2
    private WasapiCapture? _meetingBeta2Microphone;
    private WasapiLoopbackCapture? _meetingBeta2Loopback;
    private WaveFileWriter? _meetingBeta2MicrophoneWriter;
    private WaveFileWriter? _meetingBeta2LoopbackWriter;
    private string? _meetingBeta2MicrophoneTempFile;
    private string? _meetingBeta2LoopbackTempFile;
    private bool _isMeetingBeta2;
    private string? _selectedMeetingBeta2MicrophoneId;

    // ========== RAG Embedding 相关 ==========
    private TaskCompletionSource<float[]>? _embeddingTcs;
    private readonly object _embeddingLock = new();

    // ========== RAG 服务相关 ==========
    private MeetingAI.Host.RAG.VectorStore.SqliteVectorDatabase? _vectorDb;
    private MeetingAI.Host.RAG.Services.EmbeddingNPUService? _embeddingService;
    private MeetingAI.Host.RAG.Services.RAGService? _ragService;
    private bool _isRAGMode = false;  // RAG 模式开关
    private bool _isRAGInitialized = false;

    // ========== 文档管理相关 ==========
    private MeetingAI.Host.RAG.Services.DocumentProcessor? _documentProcessor;
    private MeetingAI.Host.RAG.Services.DocumentChunker? _documentChunker;
    private System.Collections.ObjectModel.ObservableCollection<MeetingAI.Host.RAG.VectorStore.DocumentInfo>? _documentList;

    // ========== 快速问答相关 ==========
    private string? _quickQADocumentContent;           // 当前加载的文档内容
    private string? _quickQADocumentName;              // 文档名称
    private long _quickQADocumentSize;                 // 文档大小（字节）
    private int _quickQATokenCount;                    // 文档token数
    private List<(string Question, string Answer)> _quickQAHistory = new();  // 对话历史（最多10轮）
    private TaskCompletionSource<int>? _tokenCountTcs; // Token计数的异步等待
    private readonly object _tokenCountLock = new();   // Token计数锁

    // ========== IE模式相关 ==========
    private string? _ieDocumentContent;                // 当前加载的文档内容
    private string? _ieDocumentName;                   // 文档名称
    private long _ieDocumentSize;                      // 文档大小（字节）
    private int _ieTokenCount;                         // 文档token数
    private string? _ieSelectedTemplateId;             // 选中的模板ID
    private string? _ieExtractedJson;                  // 提取的JSON结果
    private List<(string Question, string Answer)> _ieDialogHistory = new();  // 对话历史（最多10轮）
    private bool _isIEDialogMode = false;              // 是否在对话模式

    // IE文档类型识别相关
    private bool _isIEDetecting = false;               // 是否正在识别文档类型
    private StringBuilder? _ieDetectionBuffer;         // 识别响应收集器

    // IE信息提取相关
    private bool _isIEExtracting = false;              // 是否正在提取
    private StringBuilder? _ieExtractionBuffer;        // 提取响应收集器

    // ========== IE Chat模式相关（独立页面，与主页IE完全隔离） ==========
    private Models.ChatMessage? _ieChatStreamingMessage = null;
    private string? _ieChatDocumentContent = null;     // 当前加载的文档内容
    private string? _ieChatDocumentName = null;        // 文档名称
    private long _ieChatDocumentSize = 0;              // 文档大小（字节）
    private int _ieChatTokenCount = 0;                 // 文档token数
    private string? _ieChatSelectedTemplateId = null;  // 选中的模板ID
    private string? _ieChatExtractedJson = null;       // 提取的JSON结果
    private bool _isChatExtracting = false;            // 是否正在提取
    private bool _isChatDetecting = false;             // 是否正在识别文档类型
    private StringBuilder? _ieChatDetectionBuffer = null;   // 识别响应收集器
    private StringBuilder? _ieChatExtractionBuffer = null;  // 提取响应收集器

    // ========== RAG Chat模式相关（独立页面，与主页RAG完全隔离） ==========
    private Models.ChatMessage? _ragChatStreamingMessage = null;

    // ========== Audio Test 相关 ==========
    private WasapiCapture? _microphoneTestCapture;    // 麦克风测试捕获
    private bool _isMicrophoneTestRunning;             // 麦克风测试运行状态
    private CancellationTokenSource? _microphoneTestCancellation; // 麦克风测试取消令牌
    private WaveOutEvent? _speakerTestOutput;          // 扬声器测试输出
    private SignalGenerator? _speakerTestGenerator;    // 扬声器测试信号生成器
    private bool _isSpeakerTestRunning;                // 扬声器测试运行状态

    // ========== 模型加载状态 ==========
    private bool _isGraniteLoaded = false;
    private bool _isEmbeddingLoaded = false;
    private bool _isWhisperLoaded = false;
    private bool _isOpenVINOWhisperLoaded = false;
    private bool _isSherpaLoaded = false;
    private bool _isPunctuatorLoaded = false;
    private bool _isTranslationEnZhLoaded = false;
    private bool _isTranslationZhEnLoaded = false;
    private bool _isLLaVALoaded = false;
    private bool _isSDLoaded = false;

    // ========== OpenVINO Whisper 消息处理器 ==========
    public Action<string>? OpenVINOWhisperMessageHandler { get; set; }

    // ========== 实时流式转录消息处理器 ==========
    public Action<string>? StreamingMessageHandler { get; set; }

    // ========== 语音输入相关 ==========
    private WasapiCapture? _voiceInputCapture;          // 语音输入麦克风捕获
    private WaveFileWriter? _voiceInputWriter;          // 语音输入 WAV 写入器
    private string? _voiceInputTempFile;                // 语音输入临时文件
    private bool _isVoiceInputRecording = false;        // 是否正在录音
    private bool _isVoiceInputTranscribing = false;     // 是否正在转录
    private StringBuilder? _voiceInputTranscriptBuffer; // 转录结果缓冲区

    private const string PipeName = "MeetingAI_Pipe";
}
