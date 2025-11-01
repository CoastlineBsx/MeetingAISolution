using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAI.Host;

/// <summary>
/// 音频重采样器：任意格式 → 16kHz Mono PCM16
/// 用于统一双流音频格式，符合 Whisper 要求
/// </summary>
public class AudioResampler : IDisposable
{
    private readonly WaveFormat _targetFormat = new WaveFormat(16000, 16, 1); // 16kHz, 16-bit, Mono
    private readonly BufferedWaveProvider _inputBuffer;
    private readonly ISampleProvider _sampleProvider;

    public WaveFormat TargetFormat => _targetFormat;

    /// <summary>
    /// 创建重采样器
    /// </summary>
    /// <param name="sourceFormat">源音频格式</param>
    public AudioResampler(WaveFormat sourceFormat)
    {
        // 使用传入的格式（调用者应该已经正确创建了 WaveFormat）
        WaveFormat actualFormat = sourceFormat;

        // 创建输入缓冲区（增大缓冲区，减少欠载）
        _inputBuffer = new BufferedWaveProvider(actualFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10), // 增大到 10 秒
            ReadFully = false // 允许部分读取
        };

        // 如果源格式已经是目标格式，直接使用
        if (actualFormat.SampleRate == 16000 && actualFormat.Channels == 1 && actualFormat.BitsPerSample == 16)
        {
            _sampleProvider = _inputBuffer.ToSampleProvider();
            return;
        }

        // 否则需要转换
        ISampleProvider sampleProvider = _inputBuffer.ToSampleProvider();

        // 步骤1: 如果是多声道，先 downmix 到 Mono（在重采样前）
        if (actualFormat.Channels > 1)
        {
            sampleProvider = sampleProvider.ToMono();
        }

        // 步骤2: 转换采样率（使用高质量重采样器）
        if (actualFormat.SampleRate != 16000)
        {
            // 使用 MediaFoundationResampler（抗混叠更好，但性能稍低）
            var waveProvider = sampleProvider.ToWaveProvider();
            var resampler = new MediaFoundationResampler(waveProvider, new WaveFormat(16000, waveProvider.WaveFormat.Channels));
            resampler.ResamplerQuality = 60; // 最高质量
            sampleProvider = resampler.ToSampleProvider();
        }

        _sampleProvider = sampleProvider;
    }

    /// <summary>
    /// 添加原始音频数据
    /// </summary>
    public void AddSamples(byte[] buffer, int offset, int count)
    {
        _inputBuffer.AddSamples(buffer, offset, count);
    }

    /// <summary>
    /// 读取重采样后的 float32 数据（Worker 需要 float32 格式）
    /// </summary>
    /// <param name="targetBuffer">目标缓冲区（float32，4 bytes per sample）</param>
    /// <returns>实际读取的字节数</returns>
    public int Read(byte[] targetBuffer, int offset, int count)
    {
        // 计算需要多少 samples
        int samplesNeeded = count / 4; // float32 = 4 bytes per sample
        float[] sampleBuffer = new float[samplesNeeded];

        // 从 SampleProvider 读取
        int samplesRead = _sampleProvider.Read(sampleBuffer, 0, samplesNeeded);

        // 直接写入 float32 bytes
        Buffer.BlockCopy(sampleBuffer, 0, targetBuffer, offset, samplesRead * 4);

        return samplesRead * 4; // 返回字节数
    }

    /// <summary>
    /// 读取一帧（20ms = 320 samples = 1280 bytes for float32）
    /// </summary>
    public byte[]? ReadFrame()
    {
        const int frameSize = 1280; // 16kHz * 0.02s * 4 bytes (float32)
        byte[] frame = new byte[frameSize];
        int bytesRead = Read(frame, 0, frameSize);

        if (bytesRead == 0)
            return null;

        if (bytesRead < frameSize)
        {
            // 不足一帧，填充静音 (float32 的 0)
            Array.Fill<byte>(frame, 0, bytesRead, frameSize - bytesRead);
        }

        return frame;
    }

    /// <summary>
    /// 获取当前缓冲区可用字节数
    /// </summary>
    public int BufferedBytes => _inputBuffer.BufferedBytes;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
