using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace MishaWeb;

internal static class WebView2LoaderBootstrap
{
    private const string ResourceName = "MishaWeb.WebView2Loader.dll";
    private static readonly object LoaderSync = new();
    private static nint loaderHandle;

    public static void EnsureLoaded()
    {
        lock (LoaderSync)
        {
            if (loaderHandle != 0) return;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The embedded WebView2 loader is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var loaderBytes = buffer.ToArray();
            var expectedHash = SHA256.HashData(loaderBytes);
            var versionKey = Convert.ToHexString(expectedHash).ToLowerInvariant();
            var runtimeRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MishaWeb",
                "Runtime");
            var folder = Path.Combine(runtimeRoot, versionKey);
            var loaderPath = Path.Combine(folder, "WebView2Loader.dll");

            Directory.CreateDirectory(folder);
            EnsureNoReparsePoints(runtimeRoot, folder, loaderPath);
            if (!File.Exists(loaderPath) || !LoaderMatches(loaderPath, expectedHash))
            {
                WriteLoaderAtomically(loaderPath, loaderBytes);
            }
            EnsureNoReparsePoints(runtimeRoot, folder, loaderPath);
            using var verifiedLoader = OpenVerifiedLoader(loaderPath, expectedHash);
            // Keep the non-sharing verification handle open through Load so the
            // verified bytes cannot be replaced between hashing and mapping.
            loaderHandle = NativeLibrary.Load(loaderPath);
        }
    }

    internal static async Task<string> ProbeRuntimeAsync(string userDataFolder)
    {
        EnsureLoaded();
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder).ConfigureAwait(false);
        return environment.BrowserVersionString;
    }

    private static void WriteLoaderAtomically(string destination, byte[] bytes)
    {
        var temporaryPath = destination
            + "."
            + Environment.ProcessId
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, destination, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary file is harmless and can be replaced later.
            }
        }
    }

    private static bool LoaderMatches(string path, ReadOnlySpan<byte> expectedHash)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static FileStream OpenVerifiedLoader(string path, ReadOnlySpan<byte> expectedHash)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash))
            {
                throw new InvalidDataException("The extracted WebView2 loader failed integrity verification.");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureNoReparsePoints(string runtimeRoot, string folder, string loaderPath)
    {
        foreach (var path in new[] { runtimeRoot, folder })
        {
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The WebView2 runtime cache cannot use a linked directory.");
            }
        }
        if (File.Exists(loaderPath)
            && File.GetAttributes(loaderPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The WebView2 loader cache cannot use a linked file.");
        }
    }
}
