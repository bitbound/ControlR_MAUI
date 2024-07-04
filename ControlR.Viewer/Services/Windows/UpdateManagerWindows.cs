﻿#if WINDOWS
using ControlR.Viewer.Services.Interfaces;
using IFileSystem = ControlR.Libraries.DevicesCommon.Services.IFileSystem;
using ControlR.Libraries.Shared.Services.Http;
using ControlR.Viewer.Extensions;

namespace ControlR.Viewer.Services.Windows;

internal class UpdateManagerWindows(
    IVersionApi _versionApi,
    IDownloadsApi _downloadsApi,
    IFileSystem _fileSystem,
    IProcessManager _processManager,
    IMessenger _messenger,
    ISettings _settings,
    IStoreIntegration _storeIntegration,
    IAppState _appState,
    ILogger<UpdateManagerWindows> _logger) : IUpdateManager
{
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public async Task<Result<bool>> CheckForUpdate()
    {
        try
        {
            var integrationResult = await _appState.GetStoreIntegrationEnabled(TimeSpan.FromSeconds(5));

            if (integrationResult is not bool integrationEnabled)
            {
                return await CheckForSelfHostedUpdate();
            }

            if (integrationEnabled)
            {
                var checkResult = await _storeIntegration.IsUpdateAvailable();

                if (checkResult.IsSuccess && checkResult.Value)
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

    private async Task<Result<bool>> CheckForSelfHostedUpdate()
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

    public async Task<Result> InstallCurrentVersion()
    {
        if (!await _installLock.WaitAsync(0))
        {
            return Result.Fail("Update already started.");
        }

        try
        {
            var integrationResult = await _appState.GetStoreIntegrationEnabled(TimeSpan.FromSeconds(5));

            if (integrationResult is not bool integrationEnabled)
            {
                return Result.Fail("Store integration has not yet been checked.");
            }

            if (integrationEnabled)
            {
                var checkResult = await _storeIntegration.IsUpdateAvailable();
                if (checkResult.IsSuccess && checkResult.Value)
                {
                    var installResult = await _storeIntegration.InstallCurrentVersion();
                    if (installResult.IsSuccess)
                    {
                        return installResult;
                    }
                }
            }

            return await InstallCurrentVersionSelfHosted();
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

    private async Task<Result> InstallCurrentVersionSelfHosted()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), AppConstants.ViewerFileName);
        if (_fileSystem.FileExists(tempPath))
        {
            _fileSystem.DeleteFile(tempPath);
        }

        var downloadResult = await _downloadsApi.DownloadFile(_settings.ViewerDownloadUri, tempPath);
        if (!downloadResult.IsSuccess)
        {
            return downloadResult;
        }

        await _processManager.StartAndWaitForExit(
            fileName: "explorer.exe",
            arguments: tempPath,
            useShellExec: true,
            timeout: TimeSpan.FromMinutes(1));

        return Result.Ok();
    }
}
#endif