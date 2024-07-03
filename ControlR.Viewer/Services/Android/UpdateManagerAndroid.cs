﻿#if ANDROID

using Android.App;
using Android.Content;
using Android.Content.PM;
using Bitbound.SimpleMessenger;
using ControlR.Libraries.Shared.Services.Http;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using ControlR.Libraries.Shared.Services;
using ControlR.Viewer.Services.Interfaces;
using ControlR.Libraries.DevicesCommon.Extensions;
using ControlR.Libraries.DevicesCommon.Messages;
using ControlR.Viewer.Extensions;

namespace ControlR.Viewer.Services.Android;

internal class UpdateManagerAndroid(
    IVersionApi _versionApi,
    IAppState _appState,
    IStoreIntegration _storeIntegration,
    IBrowser _browser,
    ISettings _settings,
    IMessenger _messenger,
    ILogger<UpdateManagerAndroid> _logger) : IUpdateManager
{
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public async Task<Result<bool>> CheckForUpdate()
    {
        try
        {
            if (ViewerConstants.IsStoreBuild)
            {
                var checkResult = await _storeIntegration.IsUpdateAvailable();

                if (!checkResult.IsSuccess)
                {
                    await _messenger.SendToast("Failed to check store for updates", MudBlazor.Severity.Error);
                }
                else if (checkResult.Value)
                {
                    return checkResult;
                }
            }

            return await CheckForSelfHostedUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking for new versions.");
            return Result.Fail<bool>("An error occurred.");
        }
    }

    public async Task<Result> InstallCurrentVersion()
    {
        if (!await _installLock.WaitAsync(0))
        {
            return Result.Fail("Update already started.");
        }

        try
        {
            if (ViewerConstants.IsStoreBuild)
            {
                var checkResult = await _storeIntegration.IsUpdateAvailable();
                if (checkResult.IsSuccess && checkResult.Value)
                {
                    var installResult = await _storeIntegration.InstallCurrentVersion();
                    if (installResult.IsSuccess)
                    {
                        return installResult;
                    }
                    await _messenger.SendToast("Failed to update from store", MudBlazor.Severity.Error);
                }
            }

            if (!await _browser.OpenAsync(_settings.ViewerDownloadUri, BrowserLaunchMode.External))
            {
                await _messenger.SendToast("Failed to launch update URL", MudBlazor.Severity.Error);
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while installing the current version.");
        }
        finally
        {
            _installLock.Release();
        }
        return Result.Fail("Installation failed.");
    }

private async Task<Result<bool>> CheckForSelfHostedUpdate()
    {
        try
        {
            var result = await _versionApi.GetCurrentViewerVersion();
            if (!result.IsSuccess)
            {
                _logger.LogResult(result);
                return Result.Fail<bool>(result.Reason);
            }

            var currentVersion = Version.Parse(VersionTracking.CurrentVersion);
            if (result.Value != currentVersion)
            {
                return Result.Ok(true);
            }

            return Result.Ok(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking for new versions.");
            return Result.Fail<bool>("An error occurred.");
        }
    }
}
#endif