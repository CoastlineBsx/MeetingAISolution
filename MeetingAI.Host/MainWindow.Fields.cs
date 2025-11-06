using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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

    private const string PipeName = "MeetingAI_Pipe";
}
