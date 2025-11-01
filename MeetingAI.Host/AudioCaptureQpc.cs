using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MeetingAI.Host;

/// <summary>
/// WASAPI 音频采集器,支持 AEC(回声消除) + QPC 时间戳
/// 完整的 P/Invoke 实现,不依赖 NAudio 的 COM 封装
/// </summary>
public class AudioCaptureQpc : IDisposable
{
    // ==================== COM 接口定义 ====================

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr ppDevice);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(AUDCLNT_SHAREMODE ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity,
            [In] IntPtr pFormat, [In] ref Guid AudioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint pNumBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long phnsLatency);

        [PreserveSig]
        int GetCurrentPadding(out uint pNumPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, [In] IntPtr pFormat, out IntPtr ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr ppDeviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient2 : IAudioClient
    {
        // 继承 IAudioClient 的所有方法
        [PreserveSig]
        new int Initialize(AUDCLNT_SHAREMODE ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity,
            [In] IntPtr pFormat, [In] ref Guid AudioSessionGuid);

        [PreserveSig]
        new int GetBufferSize(out uint pNumBufferFrames);

        [PreserveSig]
        new int GetStreamLatency(out long phnsLatency);

        [PreserveSig]
        new int GetCurrentPadding(out uint pNumPaddingFrames);

        [PreserveSig]
        new int IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, [In] IntPtr pFormat, out IntPtr ppClosestMatch);

        [PreserveSig]
        new int GetMixFormat(out IntPtr ppDeviceFormat);

        [PreserveSig]
        new int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

        [PreserveSig]
        new int Start();

        [PreserveSig]
        new int Stop();

        [PreserveSig]
        new int Reset();

        [PreserveSig]
        new int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        new int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        // IAudioClient2 新增方法
        [PreserveSig]
        int IsOffloadCapable(AUDIO_STREAM_CATEGORY Category, out bool pbOffloadCapable);

        [PreserveSig]
        int SetClientProperties([In] ref AudioClientProperties pProperties);

        [PreserveSig]
        int GetBufferSizeLimits([In] IntPtr pFormat, bool bEventDriven, out long phnsMinBufferDuration, out long phnsMaxBufferDuration);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags,
            out long pDevicePosition, out long pQPCPosition);

        [PreserveSig]
        int ReleaseBuffer(uint NumFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    // ==================== 枚举和结构 ====================

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    private enum AUDCLNT_SHAREMODE
    {
        AUDCLNT_SHAREMODE_SHARED = 0,
        AUDCLNT_SHAREMODE_EXCLUSIVE = 1
    }

    private enum AUDIO_STREAM_CATEGORY
    {
        AudioCategory_Other = 0,
        AudioCategory_ForegroundOnlyMedia = 1,
        AudioCategory_BackgroundCapableMedia = 2,
        AudioCategory_Communications = 3,
        AudioCategory_Alerts = 4,
        AudioCategory_SoundEffects = 5,
        AudioCategory_GameEffects = 6,
        AudioCategory_GameMedia = 7,
        AudioCategory_GameChat = 8,
        AudioCategory_Speech = 9,
        AudioCategory_Movie = 10,
        AudioCategory_Media = 11
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    // Wave format tags
    private const ushort WAVE_FORMAT_PCM = 1;
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProperties
    {
        public uint cbSize;
        public bool bIsOffload;
        public AUDIO_STREAM_CATEGORY eCategory;
        public uint Options;
    }

    // ==================== 常量 ====================

    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMOPTIONS_RAW = 0x01;
    private const uint AUDCLNT_STREAMOPTIONS_MATCH_FORMAT = 0x02;

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint CLSCTX_ALL = 0x17;

    // ==================== P/Invoke ====================

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("ole32.dll")]
    private static extern int CoTaskMemFree(IntPtr pv);

    // ==================== 公共事件 ====================

    public class AudioDataEventArgs : EventArgs
    {
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public int BytesRecorded { get; init; }
        public long QpcTimestamp { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int BitsPerSample { get; init; }
    }

    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    // ==================== 字段 ====================

    private readonly bool _isLoopback;
    private readonly bool _enableAec;
    private readonly string? _deviceId;

    private IntPtr _devicePtr = IntPtr.Zero;
    private object? _audioClient;  // IAudioClient 或 IAudioClient2
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private IntPtr _waveFormat = IntPtr.Zero;

    private int _sampleRate;
    private int _channels;
    private int _bitsPerSample;
    private ushort _formatTag;

    private static long _qpcFrequency;
    
    // 音频预处理参数
    private const float HIGH_PASS_CUTOFF = 80.0f; // 高通滤波截止频率（去除低频噪音）
    private const float NOISE_GATE_THRESHOLD = -60.0f; // 噪音门限（dB）
    private float[]? _highPassState; // 高通滤波器状态

    // ==================== 公共属性 ====================

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public int BitsPerSample => _bitsPerSample;
    public bool IsIeeeFloat => _formatTag == WAVE_FORMAT_IEEE_FLOAT;

    static AudioCaptureQpc()
    {
        QueryPerformanceFrequency(out _qpcFrequency);
    }

    // ==================== 构造函数 ====================

    public AudioCaptureQpc(string? deviceId, bool isLoopback, bool enableAec)
    {
        _deviceId = deviceId;
        _isLoopback = isLoopback;
        _enableAec = enableAec && !isLoopback;

        InitializeDevice();
    }

    // ==================== 初始化 ====================

    private void InitializeDevice()
    {
        int hr;

        // 1. 创建设备枚举器
        Guid clsidMMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        Guid iidIMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");

        object enumeratorObj;
        CoCreateInstance(ref clsidMMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, ref iidIMMDeviceEnumerator, out enumeratorObj);
        var enumerator = (IMMDeviceEnumerator)enumeratorObj;

        // 2. 获取设备
        if (string.IsNullOrEmpty(_deviceId) || _deviceId == "default")
        {
            EDataFlow flow = _isLoopback ? EDataFlow.eRender : EDataFlow.eCapture;
            hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.eCommunications, out _devicePtr);
            if (hr != 0) throw new COMException($"GetDefaultAudioEndpoint failed", hr);
        }
        else
        {
            hr = enumerator.GetDevice(_deviceId, out _devicePtr);
            if (hr != 0) throw new COMException($"GetDevice failed", hr);
        }

        var device = Marshal.GetObjectForIUnknown(_devicePtr) as IMMDevice;
        if (device == null) throw new Exception("Failed to get IMMDevice");

        // 3. 尝试激活 IAudioClient2 (支持 AEC)
        Guid iidIAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        Guid iidIAudioClient2 = new Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA");

        IntPtr audioClientPtr;
        Guid audioClientGuid = _enableAec ? iidIAudioClient2 : iidIAudioClient;

        hr = device.Activate(ref audioClientGuid, CLSCTX_ALL, IntPtr.Zero, out audioClientPtr);

        if (hr != 0 && _enableAec)
        {
            // IAudioClient2 失败,降级到 IAudioClient
            audioClientGuid = iidIAudioClient;
            hr = device.Activate(ref audioClientGuid, CLSCTX_ALL, IntPtr.Zero, out audioClientPtr);

            if (hr == 0)
            {
                _audioClient = Marshal.GetObjectForIUnknown(audioClientPtr);
                throw new Exception("AEC requires IAudioClient2, but only IAudioClient is available on this system");
            }
        }

        if (hr != 0) throw new COMException($"Activate IAudioClient failed", hr);

        _audioClient = Marshal.GetObjectForIUnknown(audioClientPtr);
        Marshal.Release(audioClientPtr);

        // 4. 设置 AEC 属性 (如果使用 IAudioClient2)
        if (_enableAec && _audioClient is IAudioClient2 client2)
        {
            var props = new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                bIsOffload = false,
                eCategory = AUDIO_STREAM_CATEGORY.AudioCategory_Communications,
                Options = 0 // 移除 RAW 模式，让 Windows 音频引擎处理（AEC 才能生效）
            };

            hr = client2.SetClientProperties(ref props);
            if (hr != 0) throw new COMException($"SetClientProperties (AEC) failed", hr);
        }

        // 5. 获取混音格式
        var baseClient = (IAudioClient)_audioClient;
        hr = baseClient.GetMixFormat(out _waveFormat);
        if (hr != 0) throw new COMException($"GetMixFormat failed", hr);

        var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(_waveFormat);
        _sampleRate = (int)fmt.nSamplesPerSec;
        _channels = fmt.nChannels;
        _bitsPerSample = fmt.wBitsPerSample;
        _formatTag = fmt.wFormatTag;

        // 6. 初始化音频客户端
        uint streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if (_isLoopback)
            streamFlags |= AUDCLNT_STREAMFLAGS_LOOPBACK;

        Guid audioSessionGuid = Guid.Empty;
        hr = baseClient.Initialize(
            AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
            streamFlags,
            10000000, // 1 second buffer
            0,
            _waveFormat,
            ref audioSessionGuid);

        if (hr != 0) throw new COMException($"Initialize failed", hr);

        // 7. 获取捕获客户端
        Guid iidIAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        hr = baseClient.GetService(ref iidIAudioCaptureClient, out object captureClientObj);
        if (hr != 0) throw new COMException($"GetService (IAudioCaptureClient) failed", hr);

        _captureClient = (IAudioCaptureClient)captureClientObj;
    }

    // ==================== 启动/停止 ====================

    public void Start()
    {
        if (_captureThread != null)
            throw new InvalidOperationException("Already started");

        _cts = new CancellationTokenSource();
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "WASAPI Capture QPC" };

        var client = (IAudioClient)_audioClient!;
        int hr = client.Start();
        if (hr != 0) throw new COMException($"Start failed", hr);

        _captureThread.Start();
    }

    public void Stop()
    {
        if (_captureThread == null) return;

        _cts?.Cancel();
        _captureThread.Join(5000);

        var client = _audioClient as IAudioClient;
        client?.Stop();

        _captureThread = null;
    }

    // ==================== 采集循环 ====================

    private void CaptureLoop()
    {
        var token = _cts!.Token;

        while (!token.IsCancellationRequested)
        {
            uint packetSize = 0;
            int hr = _captureClient!.GetNextPacketSize(out packetSize);

            if (hr != 0 || packetSize == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            IntPtr pData;
            uint numFramesToRead;
            uint flags;
            long devicePosition;
            long qpcPosition;

            hr = _captureClient.GetBuffer(out pData, out numFramesToRead, out flags, out devicePosition, out qpcPosition);
            if (hr != 0)
            {
                Thread.Sleep(1);
                continue;
            }

            if (numFramesToRead > 0)
            {
                int bytesToRead = (int)(numFramesToRead * _channels * (_bitsPerSample / 8));
                byte[] buffer = new byte[bytesToRead];
                Marshal.Copy(pData, buffer, 0, bytesToRead);

                // 音频预处理
                byte[] processedBuffer = ProcessAudio(buffer, (int)numFramesToRead);

                DataAvailable?.Invoke(this, new AudioDataEventArgs
                {
                    Data = processedBuffer,
                    BytesRecorded = processedBuffer.Length,
                    QpcTimestamp = qpcPosition,
                    SampleRate = _sampleRate,
                    Channels = _channels,
                    BitsPerSample = _bitsPerSample
                });
            }

            _captureClient.ReleaseBuffer(numFramesToRead);
        }
    }

    // ==================== 音频预处理 ====================

    private byte[] ProcessAudio(byte[] rawData, int frameCount)
    {
        // 初始化滤波器状态
        if (_highPassState == null)
            _highPassState = new float[_channels];

        // 转换为 float32 进行处理
        float[] samples = ConvertToFloat32(rawData, frameCount);
        
        // 应用高通滤波（去除低频噪音）
        ApplyHighPassFilter(samples, _channels);
        
        // 应用噪音门限（去除静音段的底噪）
        ApplyNoiseGate(samples);
        
        // 归一化增益（确保音量合适）
        NormalizeGain(samples);
        
        // 转换回原始格式
        return ConvertFromFloat32(samples);
    }

    private float[] ConvertToFloat32(byte[] rawData, int frameCount)
    {
        float[] samples = new float[frameCount * _channels];
        
        if (_formatTag == WAVE_FORMAT_IEEE_FLOAT && _bitsPerSample == 32)
        {
            // 已经是 float32，直接复制
            Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);
        }
        else if (_formatTag == WAVE_FORMAT_PCM && _bitsPerSample == 16)
        {
            // 16-bit PCM 转 float32
            for (int i = 0; i < samples.Length; i++)
            {
                short sample = BitConverter.ToInt16(rawData, i * 2);
                samples[i] = sample / 32768.0f;
            }
        }
        else
        {
            // 其他格式，简单复制（可能需要扩展）
            Buffer.BlockCopy(rawData, 0, samples, 0, Math.Min(rawData.Length, samples.Length * 4));
        }
        
        return samples;
    }

    private void ApplyHighPassFilter(float[] samples, int channels)
    {
        // 简单的一阶高通滤波器（IIR）
        // y[n] = alpha * (y[n-1] + x[n] - x[n-1])
        float dt = 1.0f / _sampleRate;
        float RC = 1.0f / (2.0f * (float)Math.PI * HIGH_PASS_CUTOFF);
        float alpha = RC / (RC + dt);

        for (int ch = 0; ch < channels; ch++)
        {
            float prevInput = 0;
            float prevOutput = _highPassState![ch];

            for (int i = ch; i < samples.Length; i += channels)
            {
                float currentInput = samples[i];
                float output = alpha * (prevOutput + currentInput - prevInput);
                samples[i] = output;

                prevInput = currentInput;
                prevOutput = output;
            }

            _highPassState[ch] = prevOutput;
        }
    }

    private void ApplyNoiseGate(float[] samples)
    {
        // 噪音门限：低于阈值的信号衰减到 0
        float threshold = (float)Math.Pow(10.0, NOISE_GATE_THRESHOLD / 20.0); // dB 转线性

        for (int i = 0; i < samples.Length; i++)
        {
            float absValue = Math.Abs(samples[i]);
            if (absValue < threshold)
            {
                // 软门限：渐进衰减（避免硬切产生咔嗒声）
                float ratio = absValue / threshold;
                samples[i] *= ratio * ratio; // 平方衰减
            }
        }
    }

    private void NormalizeGain(float[] samples)
    {
        // 自动增益控制（简化版）
        // 计算 RMS 能量
        float sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumSquares += samples[i] * samples[i];
        }
        float rms = (float)Math.Sqrt(sumSquares / samples.Length);

        // 目标 RMS（-20dB）
        float targetRms = 0.1f;
        
        // 如果信号太小，适度放大（但不超过 6dB）
        if (rms > 0.0001f && rms < targetRms)
        {
            float gain = Math.Min(targetRms / rms, 2.0f); // 最大放大 6dB
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
                
                // 限幅（避免削波）
                if (samples[i] > 1.0f) samples[i] = 1.0f;
                if (samples[i] < -1.0f) samples[i] = -1.0f;
            }
        }
    }

    private byte[] ConvertFromFloat32(float[] samples)
    {
        if (_formatTag == WAVE_FORMAT_IEEE_FLOAT && _bitsPerSample == 32)
        {
            // 输出 float32
            byte[] result = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, result, 0, result.Length);
            return result;
        }
        else if (_formatTag == WAVE_FORMAT_PCM && _bitsPerSample == 16)
        {
            // 输出 16-bit PCM
            byte[] result = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short sample = (short)(samples[i] * 32767.0f);
                BitConverter.GetBytes(sample).CopyTo(result, i * 2);
            }
            return result;
        }
        else
        {
            // 默认返回 float32
            byte[] result = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, result, 0, result.Length);
            return result;
        }
    }

    // ==================== 静态工具 ====================

    public static long GetQpcFrequency() => _qpcFrequency;

    public static double QpcTicksToMilliseconds(long qpcTicks, long baseQpc = 0)
    {
        return (qpcTicks - baseQpc) / (double)_qpcFrequency * 1000.0;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter")]
    public static extern bool GetQpcTimestamp(out long lpPerformanceCount);

    // ==================== 清理 ====================

    public void Dispose()
    {
        Stop();

        if (_waveFormat != IntPtr.Zero)
        {
            CoTaskMemFree(_waveFormat);
            _waveFormat = IntPtr.Zero;
        }

        if (_captureClient != null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }

        if (_audioClient != null)
        {
            Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }

        if (_devicePtr != IntPtr.Zero)
        {
            Marshal.Release(_devicePtr);
            _devicePtr = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
