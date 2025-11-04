using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// 与 C++ Worker 进程通信的服务（通过 Named Pipe）
/// </summary>
public class WorkerPipeClient : IDisposable
{
    private readonly string _workerExePath;
    private Process? _workerProcess;
    private NamedPipeClientStream? _pipeClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _isConnected;

    public bool IsConnected => _isConnected;

    public WorkerPipeClient(string workerExePath)
    {
        _workerExePath = workerExePath;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
            return true;

        if (!File.Exists(_workerExePath))
        {
            throw new FileNotFoundException($"Worker not found: {_workerExePath}");
        }

        try
        {
            // 启动 Worker 进程
            _workerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _workerExePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            _workerProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.WriteLine($"[Worker] {e.Data}");
            };

            _workerProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.WriteLine($"[Worker Error] {e.Data}");
            };

            _workerProcess.Start();
            _workerProcess.BeginOutputReadLine();
            _workerProcess.BeginErrorReadLine();

            // 等待 Worker 创建 Named Pipe
            await Task.Delay(2000, cancellationToken);

            // 连接到 Named Pipe
            _pipeClient = new NamedPipeClientStream(
                ".",
                "MeetingAI_Pipe",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await _pipeClient.ConnectAsync(5000, cancellationToken);

            _reader = new StreamReader(_pipeClient, Encoding.UTF8);
            _writer = new StreamWriter(_pipeClient, Encoding.UTF8) { AutoFlush = true };

            _isConnected = true;
            Debug.WriteLine("[WorkerPipe] Connected");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WorkerPipe] Failed to start: {ex.Message}");
            Stop();
            return false;
        }
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _writer == null || _reader == null)
            throw new InvalidOperationException("Not connected to worker");

        await _writer.WriteLineAsync(command);
        var response = await _reader.ReadLineAsync(cancellationToken);
        return response ?? string.Empty;
    }

    public void Stop()
    {
        _isConnected = false;

        _reader?.Dispose();
        _writer?.Dispose();
        _pipeClient?.Dispose();

        if (_workerProcess != null && !_workerProcess.HasExited)
        {
            try
            {
                _workerProcess.Kill(true);
                _workerProcess.WaitForExit(3000);
            }
            catch { }
        }

        _workerProcess?.Dispose();
        Debug.WriteLine("[WorkerPipe] Stopped");
    }

    public void Dispose()
    {
        Stop();
    }
}
