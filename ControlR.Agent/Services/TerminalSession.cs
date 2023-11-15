﻿using ControlR.Devices.Common.Services;
using ControlR.Shared;
using ControlR.Shared.Dtos;
using ControlR.Shared.Enums;
using ControlR.Shared.Extensions;
using ControlR.Shared.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace ControlR.Agent.Services;

public interface ITerminalSession : IDisposable
{
    event EventHandler? ProcessExited;

    bool IsDisposed { get; }

    TerminalSessionKind SessionKind { get; }

    Task<Result> WriteInput(string input, TimeSpan timeout);
}

internal class TerminalSession(
    Guid _terminalId,
    string _viewerConnectionId,
    IFileSystem _fileSystem,
    IProcessManager _processManager,
    IEnvironmentHelper _environment,
    ISystemTime _systemTime,
    IAgentHubConnection _hubConnection,
    ILogger<TerminalSession> _logger) : ITerminalSession
{
    private readonly Process _shellProcess = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposedValue;

    public event EventHandler? ProcessExited;

    public bool IsDisposed => _disposedValue;
    public TerminalSessionKind SessionKind { get; private set; }
    public Guid TerminalId { get; } = _terminalId;

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async Task<Result> WriteInput(string input, TimeSpan timeout)
    {
        await _writeLock.WaitAsync();

        try
        {
            if (_shellProcess.HasExited == true)
            {
                throw new InvalidOperationException("Shell process is not running.");
            }

            using var cts = new CancellationTokenSource(timeout);
            var sb = new StringBuilder(input);
            await _shellProcess.StandardInput.WriteLineAsync(sb, cts.Token);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while writing input to command shell.");

            // Something's wrong.  Let the next command start a new session.
            Dispose();
            return Result.Fail("An error occurred.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task Initialize()
    {
        var shellProcessName = await GetShellProcessName();
        var psi = new ProcessStartInfo()
        {
            FileName = shellProcessName,
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = "RunAs",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };

        _shellProcess.StartInfo = psi;
        _shellProcess.ErrorDataReceived += ShellProcess_ErrorDataReceived;
        _shellProcess.OutputDataReceived += ShellProcess_OutputDataReceived;
        _shellProcess.Exited += ShellProcess_Exited;

        _shellProcess.Start();

        _shellProcess.BeginErrorReadLine();
        _shellProcess.BeginOutputReadLine();

        if (SessionKind is TerminalSessionKind.WindowsPowerShell or TerminalSessionKind.PowerShell)
        {
            await WriteInput("$VerbosePreference = \"Continue\";", TimeSpan.FromSeconds(5));
            await WriteInput("$DebugPreference = \"Continue\";", TimeSpan.FromSeconds(5));
            await WriteInput("$InformationPreference = \"Continue\";", TimeSpan.FromSeconds(5));
            await WriteInput("$WarningPreference = \"Continue\";", TimeSpan.FromSeconds(5));
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _shellProcess.KillAndDispose();
            }

            _disposedValue = true;
        }
    }

    private async Task<string> GetShellProcessName()
    {
        switch (_environment.Platform)
        {
            case Shared.Enums.SystemPlatform.Windows:
                var result = await TryGetPwshPath();
                if (result.IsSuccess)
                {
                    SessionKind = TerminalSessionKind.PowerShell;
                    return result.Value;
                }
                SessionKind = TerminalSessionKind.WindowsPowerShell;
                return "powershell.exe";

            case Shared.Enums.SystemPlatform.Linux:
                if (_fileSystem.FileExists("/bin/bash"))
                {
                    SessionKind = TerminalSessionKind.Bash;
                    return "/bin/bash";
                }

                if (_fileSystem.FileExists("/bin/sh"))
                {
                    SessionKind = TerminalSessionKind.Sh;
                    return "/bin/sh";
                }
                throw new FileNotFoundException("No shell found.");
            case Shared.Enums.SystemPlatform.Unknown:
            case Shared.Enums.SystemPlatform.MacOS:
            case Shared.Enums.SystemPlatform.MacCatalyst:
            case Shared.Enums.SystemPlatform.Android:
            case Shared.Enums.SystemPlatform.IOS:
            case Shared.Enums.SystemPlatform.Browser:
            default:
                throw new PlatformNotSupportedException();
        }
    }

    private async void ShellProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        try
        {
            var outputDto = new TerminalOutputDto(
                TerminalId,
                e.Data ?? string.Empty,
                TerminalOutputKind.StandardError,
                _systemTime.Now);

            await _hubConnection.SendTerminalOutputToViewer(_viewerConnectionId, outputDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending terminal output.");
        }
    }

    private void ShellProcess_Exited(object? sender, EventArgs e)
    {
        ProcessExited?.Invoke(this, e);
    }

    private async void ShellProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        try
        {
            var outputDto = new TerminalOutputDto(
                TerminalId,
                e.Data ?? string.Empty,
                TerminalOutputKind.StandardOutput,
                _systemTime.Now);

            await _hubConnection.SendTerminalOutputToViewer(_viewerConnectionId, outputDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending terminal output.");
        }
    }

    private async Task<Result<string>> TryGetPwshPath()
    {
        try
        {
            var output = await _processManager.GetProcessOutput("where.exe", "pwsh.exe");
            if (!output.IsSuccess)
            {
                _logger.LogResult(output);
                return Result.Fail<string>("Failed to find path to pwsh.exe.");
            }

            var split = output.Value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 0)
            {
                var result = Result.Fail<string>("Path to pwsh not found.");
                _logger.LogResult(result);
                return result;
            }

            return Result.Ok(split[0]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while trying to get pwsh path.");
            return Result.Fail<string>("An error occurred.");
        }
    }
}