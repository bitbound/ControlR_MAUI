﻿using Bitbound.SimpleMessenger;
using ControlR.Agent.Interfaces;
using ControlR.Agent.Models;
using ControlR.Devices.Common.Native.Windows;
using ControlR.Devices.Common.Services;
using ControlR.Devices.Common.Services.Interfaces;
using ControlR.Shared;
using ControlR.Shared.Dtos;
using ControlR.Shared.Enums;
using ControlR.Shared.Extensions;
using ControlR.Shared.Interfaces.HubClients;
using ControlR.Shared.Models;
using ControlR.Shared.Services;
using MessagePack;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace ControlR.Agent.Services;

internal interface IAgentHubConnection : IHubConnectionBase
{
    Task SendDeviceHeartbeat();

    Task SendTerminalOutputToViewer(string viewerConnectionId, TerminalOutputDto outputDto);
}

internal class AgentHubConnection(
     IHostApplicationLifetime _appLifetime,
     IServiceScopeFactory _scopeFactory,
     IDeviceDataGenerator _deviceCreator,
     IEnvironmentHelper _environmentHelper,
     IOptionsMonitor<AppOptions> _appOptions,
     ICpuUtilizationSampler _cpuSampler,
     IKeyProvider _keyProvider,
     IVncSessionLauncher _vncSessionLauncher,
     IAgentUpdater _updater,
     ILocalProxy _localProxy,
     IMessenger _messenger,
     ITerminalStore _terminalStore,
     ILogger<AgentHubConnection> _logger)
        : HubConnectionBase(_scopeFactory, _messenger, _logger), IHostedService, IAgentHubConnection, IAgentHubClient
{
    public async Task<Result<TerminalSessionRequestResult>> CreateTerminalSession(SignedPayloadDto requestDto)
    {
        try
        {
            if (!VerifySignedDto<TerminalSessionRequest>(requestDto, out var payload))
            {
                return Result.Fail<TerminalSessionRequestResult>("Signature verification failed.");
            }

            return await _terminalStore.CreateSession(payload.TerminalId, payload.ViewerConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while creating terminal session.");
            return Result.Fail<TerminalSessionRequestResult>("An error occurred.");
        }
    }

    public async Task<VncSessionRequestResult> GetVncSession(SignedPayloadDto signedDto)
    {
        try
        {
            if (!VerifySignedDto<VncSessionRequest>(signedDto, out var dto))
            {
                return new(false);
            }

            if (_appOptions.CurrentValue.AutoRunVnc != true)
            {
                var session = new VncSession(dto.SessionId, () => Task.CompletedTask);
                _localProxy
                    .HandleVncSession(session)
                    .AndForget();
                return new(true);
            }

            var result = await _vncSessionLauncher
                .CreateSession(dto.SessionId, dto.VncPassword)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.LogError("Failed to get streaming session.  Reason: {reason}", result.Reason);
                return new(false);
            }

            _localProxy
                .HandleVncSession(result.Value)
                .AndForget();

            return new(true, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while creating streaming session.");
            return new(false);
        }
    }

    [SupportedOSPlatform("windows6.0.6000")]
    public Task<WindowsSession[]> GetWindowsSessions(SignedPayloadDto signedDto)
    {
        if (!VerifySignedDto(signedDto))
        {
            return Array.Empty<WindowsSession>().AsTaskResult();
        }

        if (_environmentHelper.Platform != SystemPlatform.Windows)
        {
            return Array.Empty<WindowsSession>().AsTaskResult();
        }

        return Win32.GetActiveSessions().ToArray().AsTaskResult();
    }

    public async Task SendDeviceHeartbeat()
    {
        try
        {
            using var _ = _logger.BeginMemberScope();

            if (ConnectionState != HubConnectionState.Connected)
            {
                _logger.LogWarning("Not connected to hub when trying to send device update.");
                return;
            }

            if (_appOptions.CurrentValue.AuthorizedKeys.Count == 0)
            {
                _logger.LogWarning("There are no authorized keys in appsettings. Aborting heartbeat.");
                return;
            }

            var device = await _deviceCreator.CreateDevice(
                _cpuSampler.CurrentUtilization,
                _appOptions.CurrentValue.AuthorizedKeys,
                _appOptions.CurrentValue.DeviceId);

            var result = device.TryCloneAs<Device, DeviceDto>();

            if (!result.IsSuccess)
            {
                _logger.LogResult(result);
                return;
            }

            await Connection.InvokeAsync("UpdateDevice", result.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending device update.");
        }
    }

    public async Task SendTerminalOutputToViewer(string viewerConnectionId, TerminalOutputDto outputDto)
    {
        try
        {
            await Connection.InvokeAsync("SendTerminalOutputToViewer", viewerConnectionId, outputDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending VNC stream.");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartImpl();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopConnection(cancellationToken);
    }

    private void ConfigureConnection(HubConnection hubConnection)
    {
        hubConnection.Reconnected += HubConnection_Reconnected;

        if (OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            hubConnection.On<SignedPayloadDto, WindowsSession[]>(nameof(GetWindowsSessions), GetWindowsSessions);
        }

        hubConnection.On<SignedPayloadDto, VncSessionRequestResult>(nameof(GetVncSession), GetVncSession);
        hubConnection.On<SignedPayloadDto, Result<TerminalSessionRequestResult>>(nameof(CreateTerminalSession), CreateTerminalSession);
    }

    private void ConfigureHttpOptions(HttpConnectionOptions options)
    {
    }

    private async Task HubConnection_Reconnected(string? arg)
    {
        await SendDeviceHeartbeat();
        await _updater.CheckForUpdate();
    }

    private async Task StartImpl()
    {
        await Connect(
            $"{AppConstants.ServerUri}/hubs/agent",
            ConfigureConnection,
            ConfigureHttpOptions,
             _appLifetime.ApplicationStopping);

        await SendDeviceHeartbeat();
    }

    private bool VerifySignedDto(SignedPayloadDto signedDto)
    {
        if (!_keyProvider.Verify(signedDto))
        {
            _logger.LogCritical("Verification failed for payload with public key: {key}", signedDto.PublicKey);
            return false;
        }

        if (!_appOptions.CurrentValue.AuthorizedKeys.Contains(signedDto.PublicKeyBase64))
        {
            _logger.LogCritical("Public key does not exist in authorized keys list: {key}", signedDto.PublicKey);
            return false;
        }
        return true;
    }

    private bool VerifySignedDto<TPayload>(
      SignedPayloadDto signedDto,
      [NotNullWhen(true)] out TPayload? payload)
    {
        payload = default;

        if (!VerifySignedDto(signedDto))
        {
            return false;
        }

        payload = MessagePackSerializer.Deserialize<TPayload>(signedDto.Payload);
        return payload is not null;
    }

    private class RetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var waitSeconds = Math.Min(30, Math.Pow(retryContext.PreviousRetryCount, 2));
            return TimeSpan.FromSeconds(waitSeconds);
        }
    }
}