﻿using ControlR.Agent.Interfaces;
using ControlR.Agent.Models;
using ControlR.Devices.Common.Services;
using ControlR.Devices.Common.Services.Interfaces;
using ControlR.Shared.Extensions;
using ControlR.Shared.Helpers;
using ControlR.Shared.Primitives;
using ControlR.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Result = ControlR.Shared.Primitives.Result;

namespace ControlR.Agent.Services.Windows;

[SupportedOSPlatform("windows")]
internal class VncSessionLauncherWindows(
    IFileSystem _fileSystem,
    IProcessManager _processInvoker,
    IEnvironmentHelper _environment,
    IElevationChecker _elevationChecker,
    ILogger<VncSessionLauncherWindows> _logger) : IVncSessionLauncher
{
    private readonly SemaphoreSlim _createSessionLock = new(1, 1);

    private readonly string _tvnResourcesDir = Path.Combine(_environment.StartupDirectory, "TightVNC");
    private readonly string _tvnServerPath = Path.Combine(_environment.StartupDirectory, "TightVNC", "tvnserver.exe");
    private readonly string _vncPasswordPath = Path.Combine(_environment.StartupDirectory, "TightVNC", "TightVncPassword_x86.exe");

    public async Task CleanupSessions()
    {
        try
        {
            StopProcesses();
            if (_elevationChecker.IsElevated())
            {
                await _processInvoker.StartAndWaitForExit(_tvnServerPath, "-stop -silent", true, TimeSpan.FromSeconds(5));
                await _processInvoker.StartAndWaitForExit(_tvnServerPath, "-remove -silent", true, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during VNC session cleanup");
        }
    }

    public async Task<Result<VncSession>> CreateSession(Guid sessionId, string password)
    {
        await _createSessionLock.WaitAsync();

        try
        {
            var resourcesResult = await EnsureTightVncResources();
            if (!resourcesResult.IsSuccess)
            {
                return Result.Fail<VncSession>(resourcesResult.Reason);
            }

            var regResult = await SetRegKeys(password);
            if (!regResult.IsSuccess)
            {
                return Result.Fail<VncSession>(regResult.Reason);
            }

            if (_elevationChecker.IsElevated())
            {
                return await RunTvnServerAsService(sessionId);
            }
            else
            {
                return RunTvnServerAsUser(sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while creating VNC session.");
            return Result.Fail<VncSession>("An error occurred while VNC control.");
        }
        finally
        {
            _createSessionLock.Release();
        }
    }

    // For debugging.
    private static Result<string> GetSolutionDir(string currentDir)
    {
        var dirInfo = new DirectoryInfo(currentDir);
        if (!dirInfo.Exists)
        {
            return Result.Fail<string>("Not found.");
        }

        if (dirInfo.GetFiles().Any(x => x.Name == "ControlR.sln"))
        {
            return Result.Ok(currentDir);
        }

        if (dirInfo.Parent is not null)
        {
            return GetSolutionDir(dirInfo.Parent.FullName);
        }

        return Result.Fail<string>("Not found.");
    }

    private async Task<Result> EnsureTightVncResources()
    {
        try
        {
            _fileSystem.CreateDirectory(_tvnResourcesDir);

            var assembly = typeof(VncSessionLauncherWindows).Assembly;
            var assemblyRoot = assembly.GetName().Name;
            var resourcesNamespace = $"{assemblyRoot}.Resources.TightVnc.";
            var resourceNames = assembly
                .GetManifestResourceNames()
                .Where(x => x.Contains(resourcesNamespace));

            foreach (var resource in resourceNames)
            {
                var fileName = resource.Replace(resourcesNamespace, string.Empty);
                var targetPath = Path.Combine(_tvnResourcesDir, fileName);
                if (!_fileSystem.FileExists(targetPath))
                {
                    _logger.LogInformation("TightVNC resource is missing.  Extracting {TightVncFileName}.", fileName);
                    using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                    Guard.IsNotNull(resourceStream);
                    using var fs = _fileSystem.CreateFileStream(targetPath, FileMode.Create);
                    await resourceStream.CopyToAsync(fs);
                }
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            var result = Result.Fail(ex, "Failed to extract TightVNC resources.");
            _logger.LogResult(result);
            return result;
        }
    }

    private async Task<Result<VncSession>> RunTvnServerAsService(Guid sessionId)
    {
        using var service = ServiceController
            .GetServices()
            .FirstOrDefault(x => x.ServiceName == "tvnserver");

        if (service?.Status != ServiceControllerStatus.Running)
        {
            await _processInvoker.StartAndWaitForExit(_tvnServerPath, "-reinstall -silent", true, TimeSpan.FromSeconds(5));
            await _processInvoker.StartAndWaitForExit(_tvnServerPath, "-start -silent", true, TimeSpan.FromSeconds(5));
            await _processInvoker.StartAndWaitForExit("sc.exe", "config start= demand", true, TimeSpan.FromSeconds(5));
        }

        var startResult = await WaitHelper.WaitForAsync(
               () =>
               {
                   return _processInvoker
                       .GetProcesses()
                       .Any(x =>
                           x.ProcessName.Equals("tvnserver", StringComparison.OrdinalIgnoreCase));
               }, TimeSpan.FromSeconds(10));

        if (!startResult)
        {
            return Result.Fail<VncSession>("VNC session failed to start.");
        }

        var session = new VncSession(sessionId);

        return Result.Ok(session);
    }

    private Result<VncSession> RunTvnServerAsUser(Guid sessionId)
    {
        var existingProcs = _processInvoker.GetProcessesByName(Path.GetFileNameWithoutExtension(_tvnServerPath));
        var process = existingProcs.FirstOrDefault();

        process ??= _processInvoker.Start(_tvnServerPath, "-run", true);

        if (process?.HasExited != false)
        {
            return Result.Fail<VncSession>("VNC session failed to start.");
        }

        var session = new VncSession(sessionId);

        return Result.Ok(session);
    }

    private async Task<Result> SetRegKeys(string password)
    {
        var outputResult = await _processInvoker.GetProcessOutput(_vncPasswordPath, $"{password}", 5_000);

        if (!outputResult.IsSuccess)
        {
            return Result.Fail("Failed to encrypt password.");
        }

        var hexPassword = outputResult.Value.Trim().Split().Last();
        var encryptedPassword = Convert.FromHexString(hexPassword);

        var hive = _elevationChecker.IsElevated() ?
            RegistryHive.LocalMachine :
            RegistryHive.CurrentUser;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
        using var serverKey = baseKey.CreateSubKey("SOFTWARE\\TightVNC\\Server");
        serverKey.SetValue("AllowLoopback", 1);
        serverKey.SetValue("LoopbackOnly", 1);
        serverKey.SetValue("UseVncAuthentication", 1);
        serverKey.SetValue("RemoveWallpaper", 0);
        serverKey.SetValue("Password", encryptedPassword, RegistryValueKind.Binary);
        return Result.Ok();
    }

    private void StopProcesses()
    {
        var processes = _processInvoker
            .GetProcesses()
            .Where(x =>
                x.ProcessName.Equals("tvnserver", StringComparison.OrdinalIgnoreCase));

        foreach (var proc in processes)
        {
            try
            {
                proc.KillAndDispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop existing TightVNC process.");
            }
        }
    }
}