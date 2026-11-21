using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using static DeepUninstaller.Localization;

namespace DeepUninstaller.Services;

internal sealed class PortableUpdateStaging(string directory) : IDisposable
{
    public string DownloadPath => Path.Combine(directory, PortableUpdateInstaller.DownloadName);
    private bool handedOff;

    public async Task StartInstallAsync(UpdateAsset asset)
    {
        await PortableUpdateInstaller.HandOffAsync(directory, asset);
        handedOff = true;
    }

    public void Dispose()
    {
        if (!handedOff)
            PortableUpdateInstaller.RemoveStaging(directory);
    }
}

internal static class PortableUpdateInstaller
{
    internal const string DownloadName = "download.exe";
    private const string HelperName = "updater.exe";
    private const string ApplySwitch = "--apply-portable-update";
    private const string CleanupSwitch = "--finish-portable-update";
    private static string StagingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UninstallTrace", "Updates");
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(25);
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location intentionally detects the single-file package.")]
    public static bool CanInstall => typeof(PortableUpdateInstaller).Assembly.Location.Length == 0 &&
        Environment.ProcessPath is { } path && path.EndsWith(".exe", PathComparison) && !IsElevated();

    public static PortableUpdateStaging CreateStaging()
    {
        var root = StagingRoot;
        Directory.CreateDirectory(root);
        ValidateLocalPath(root);
        var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return new(staging);
    }

    internal static async Task HandOffAsync(string staging, UpdateAsset asset)
    {
        if (!CanInstall || Environment.ProcessPath is not { } target)
            throw new InvalidOperationException(Text("当前运行方式不支持在应用内安装更新。", "This build cannot install updates in the app."));

        var id = Path.GetFileName(staging);
        if (!Guid.TryParseExact(id, "N", out _) || !string.Equals(staging, Path.Combine(StagingRoot, id), PathComparison))
            throw new InvalidDataException("Invalid update staging directory.");
        if (!UpdateAsset.ValidSize(asset.Size) || !UpdateAsset.ValidDigest(asset.Sha256))
            throw new InvalidDataException("Invalid update checksum or length.");

        var digest = Convert.FromHexString(asset.Sha256);
        var workFile = AdjacentStage(target, id);
        var helper = Path.Combine(staging, HelperName);
        var createdWorkFile = false;
        var createdHelper = false;
        try
        {
            await Task.Run(() =>
            {
                ValidateTarget(target);
                ValidateLocalPath(staging);
                CopyNewFile(Path.Combine(staging, DownloadName), workFile, () => createdWorkFile = true);
                VerifyFile(workFile, asset.Size, digest);
                CopyNewFile(target, helper, () => createdHelper = true);
            }).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(HandOffTimeout);
            using var pipe = new NamedPipeServerStream(PipeName(id), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var installer = StartPackage(helper, ApplySwitch, id);
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != installer.Id)
                    throw new InvalidDataException("Unexpected update helper.");

                var request = new byte[sizeof(long) + 32];
                BinaryPrimitives.WriteInt64LittleEndian(request, asset.Size);
                digest.CopyTo(request.AsSpan(sizeof(long)));
                await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                var reply = new byte[1];
                await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
                if (reply[0] != 1)
                    throw new InvalidDataException("The update helper did not accept the staged release.");
            }
            catch
            {
                if (!installer.HasExited)
                {
                    try { installer.Kill(); }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                }
                throw;
            }
        }
        catch (Exception exception)
        {
            if (createdWorkFile)
                TryDelete(workFile);
            if (createdHelper)
                TryDelete(helper);
            if (exception is OperationCanceledException)
                throw new TimeoutException(Text("更新程序启动超时，请重试。", "The update helper timed out. Try again."), exception);
            throw;
        }
    }

    public static bool TryHandleStartup(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is [CleanupSwitch, var completed] && Guid.TryParseExact(completed, "N", out _))
        {
            _ = Task.Run(async () =>
            {
                var staging = Path.Combine(StagingRoot, completed);
                for (var retry = 0; retry < 12; retry++)
                {
                    if (RemoveStaging(staging))
                        return;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            });
            return false;
        }

        if (args.Length == 0 || args[0] != ApplySwitch)
            return false;
        if (args is not [ApplySwitch, var id] || !Guid.TryParseExact(id, "N", out _))
        {
            exitCode = 2;
            return true;
        }

        exitCode = Apply(id);
        return true;
    }

    private static int Apply(string id)
    {
        var staging = Path.Combine(StagingRoot, id);
        var helper = Path.Combine(staging, HelperName);
        if (!CanInstall || !string.Equals(Environment.ProcessPath, helper, PathComparison))
            return 2;

        string? target = null;
        string? workFile = null;
        var acknowledged = false;
        var oldExited = false;
        try
        {
            ValidateLocalPath(staging);
            using var timeout = new CancellationTokenSource(HandOffTimeout);
            using var pipe = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var parentId) || parentId == Environment.ProcessId)
                throw new InvalidDataException("Unexpected update requester.");

            using var parent = Process.GetProcessById(checked((int)parentId));
            _ = parent.SafeHandle;
            target = parent.MainModule?.FileName ?? throw new InvalidDataException("The running application path is unavailable.");
            ValidateTarget(target);
            var helperSize = new FileInfo(helper).Length;
            var originalDigest = HashFile(helper);
            VerifyFile(target, helperSize, originalDigest);
            workFile = AdjacentStage(target, id);

            var request = new byte[sizeof(long) + 32];
            pipe.ReadExactlyAsync(request, timeout.Token).AsTask().GetAwaiter().GetResult();
            var size = BinaryPrimitives.ReadInt64LittleEndian(request);
            var digest = request.AsSpan(sizeof(long)).ToArray();
            if (!UpdateAsset.ValidSize(size))
                throw new InvalidDataException("Invalid update length.");
            VerifyFile(workFile, size, digest);

            pipe.WriteByte(1);
            pipe.Flush();
            acknowledged = true;
            oldExited = parent.WaitForExit(45000);
            if (!oldExited)
                throw new TimeoutException(Text("请先退出正在运行的 UninstallTrace。", "Close the running copy of UninstallTrace first."));

            ReplaceAndRestart(target, workFile, size, digest, helper, helperSize, originalDigest, path =>
            {
                using var next = StartPackage(path, CleanupSwitch, id);
            });
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException)
        {
            Debug.WriteLine(exception);
            if (acknowledged && oldExited && target is not null)
            {
                try { using var previous = StartPackage(target, CleanupSwitch, id); }
                catch (Exception restartError) when (restartError is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    Debug.WriteLine(restartError);
                }
            }
            if (acknowledged)
            {
                MessageBox.Show(Text("无法完成更新。请关闭其他 UninstallTrace 窗口后重试。", "The update could not finish. Close other UninstallTrace windows and try again.")
                    + Environment.NewLine + exception.Message, "UninstallTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return 1;
        }
        finally
        {
            if (workFile is not null)
                TryDelete(workFile);
        }
    }

    internal static void ReplaceAndRestart(string target, string staged, long size, byte[] digest, string original,
        long originalSize, byte[] originalDigest, Action<string> restart)
    {
        ValidateTarget(target);
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetDirectoryName(staged), PathComparison))
            throw new InvalidDataException("The replacement must be staged next to the application.");
        VerifyFile(target, originalSize, originalDigest);
        VerifyFile(staged, size, digest);
        // The source and destination are on the same volume. A failed move leaves the target in place.
        File.Move(staged, target, overwrite: true);
        try
        {
            VerifyFile(target, size, digest);
            restart(target);
        }
        catch
        {
            // The running helper is a verified copy of the original package.
            var restore = target + "." + Guid.NewGuid().ToString("N") + ".restore";
            try
            {
                CopyNewFile(original, restore);
                VerifyFile(restore, originalSize, originalDigest);
                File.Move(restore, target, overwrite: true);
            }
            finally { TryDelete(restore); }
            throw;
        }
    }

    private static string AdjacentStage(string exe, string id) => exe + "." + id + ".update";
    private static string PipeName(string id) => "UninstallTrace.portable-update." + id;

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Process StartPackage(string path, string command, string id)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(id);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start UninstallTrace.");
    }

    private static void ValidateTarget(string path)
    {
        ValidateLocalPath(path);
        if (!path.EndsWith(".exe", PathComparison) || File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new IOException(Text("当前程序位置无法写入。", "The current application location is not writable."));
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var protectedPath = Environment.GetFolderPath(folder);
            if (protectedPath.Length > 0 && path.StartsWith(protectedPath + Path.DirectorySeparatorChar, PathComparison))
                throw new IOException(Text("请将便携版放到用户可写目录后再更新。", "Move the portable app to a writable folder before updating."));
        }
    }

    private static void ValidateLocalPath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            !string.Equals(path, Path.GetFullPath(path), PathComparison) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("The update requires a local Windows path.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The update cannot use a redirected path.");
        }
    }

    private static void CopyNewFile(string source, string target, Action? onCreated = null)
    {
        ValidateLocalPath(source);
        ValidateLocalPath(Path.GetDirectoryName(target)!);
        using var read = File.OpenRead(source);
        using var write = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        onCreated?.Invoke();
        read.CopyTo(write);
        write.Flush(flushToDisk: true);
    }

    private static byte[] HashFile(string path)
    {
        ValidateLocalPath(path);
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }

    private static void VerifyFile(string path, long size, ReadOnlySpan<byte> digest)
    {
        ValidateLocalPath(path);
        using var file = File.OpenRead(path);
        if (file.Length != size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), digest))
            throw new InvalidDataException("The staged update checksum or file size did not match.");
    }

    internal static bool RemoveStaging(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return true;
            ValidateLocalPath(path);
            File.Delete(Path.Combine(path, DownloadName));
            File.Delete(Path.Combine(path, HelperName));
            Directory.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
