using System.Net;
using System.Reflection;
using MishaWeb;

namespace MishaWeb.YouTubeProbe;

internal static class Program
{
    private const string PrivateProbeArgument = "--private-probe";
    private const string PrivateChurnProbeArgument = "--private-churn-probe";
    private const string NormalProfileArgument = "--normal-profile";
    private static readonly TimeSpan ChurnStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient LoopbackClient = new(
        new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None
        })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [STAThread]
    private static void Main(string[] args)
    {
        if (!TryParseLaunchOptions(args, out var options))
        {
            Environment.ExitCode = 64;
            return;
        }

        ApplicationConfiguration.Initialize();
        using var form = CreateBrowser(options.Address.AbsoluteUri, options.UseNormalProfile);
        if (options.Churn is { } churn)
        {
            form.Shown += async (_, _) => await RunPrivateChurnContainedAsync(form, churn);
        }
        Application.Run(form);
    }

    private static bool TryParseLaunchOptions(string[] args, out LaunchOptions options)
    {
        options = null!;
        var useNormalProfile = args.Length == 2
            && args[0].Equals(NormalProfileArgument, StringComparison.Ordinal);
        var usePrivateProfile = args.Length == 2
            && args[0].Equals(PrivateProbeArgument, StringComparison.Ordinal);
        var usePrivateChurn = args.Length == 5
            && args[0].Equals(PrivateChurnProbeArgument, StringComparison.Ordinal);
        if (!useNormalProfile && !usePrivateProfile && !usePrivateChurn) return false;

        if (!Uri.TryCreate(args[1], UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https"))
        {
            return false;
        }

        PrivateChurnOptions? churn = null;
        if (usePrivateChurn)
        {
            if (!address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !address.Host.Equals("127.0.0.1", StringComparison.Ordinal)
                || !address.AbsolutePath.Equals("/probe", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(address.UserInfo)
                || !TryGetProbeToken(address, out var token)
                || !TryParseBoundedInteger(args[2], 3, 32, out var cycles)
                || !TryParseBoundedInteger(args[3], 1, 4, out var batchSize)
                || !TryParseBoundedInteger(args[4], 250, 5_000, out var dwellMs))
            {
                return false;
            }
            churn = new PrivateChurnOptions(address, token, cycles, batchSize, dwellMs);
        }

        options = new LaunchOptions(address, useNormalProfile, churn);
        return true;
    }

    private static bool TryGetProbeToken(Uri address, out string token)
    {
        token = string.Empty;
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..separator]);
            if (!key.Equals("token", StringComparison.Ordinal)) continue;
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (!Guid.TryParseExact(value, "D", out var parsed)) return false;
            token = parsed.ToString("D");
            return true;
        }
        return false;
    }

    private static bool TryParseBoundedInteger(
        string value,
        int minimum,
        int maximum,
        out int parsed) =>
        int.TryParse(value, out parsed) && parsed >= minimum && parsed <= maximum;

    private static async Task RunPrivateChurnContainedAsync(
        MainForm form,
        PrivateChurnOptions options)
    {
        try
        {
            var api = ResolvePrivateChurnApi();
            await WaitForChurnStartAsync(options);
            await VerifyThreeTabResidencyAsync(form, api, options);
            for (var cycle = 1; cycle <= options.Cycles; cycle++)
            {
                var openedTabs = new List<object>(options.BatchSize);
                try
                {
                    for (var slot = 1; slot <= options.BatchSize; slot++)
                    {
                        var address = BuildLoopbackUri(
                            options,
                            "/churn",
                            $"cycle={cycle}&slot={slot}");
                        openedTabs.Add(await OpenTabAsync(form, api.OpenTab, address.AbsoluteUri));
                    }
                    await Task.Delay(options.DwellMs);
                }
                finally
                {
                    for (var index = openedTabs.Count - 1; index >= 0; index--)
                    {
                        CloseTab(form, api.CloseTab, openedTabs[index]);
                    }
                }

                await PostControlAsync(options, "/checkpoint", cycle.ToString());
            }
            await PostControlAsync(options, "/done", options.Cycles.ToString());
        }
        catch (Exception error)
        {
            Environment.ExitCode = 70;
            await TryReportFailureAsync(options, error);
            if (!form.IsDisposed) form.Close();
        }
    }

    private static PrivateChurnApi ResolvePrivateChurnApi()
    {
        var browserTabType = typeof(MainForm).GetNestedType(
            "BrowserTab",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException("MainForm.BrowserTab was not found.");
        var openTab = typeof(MainForm).GetMethod(
            "OpenNewTabAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(string)
            ],
            modifiers: null);
        if (openTab is null
            || !openTab.ReturnType.IsGenericType
            || openTab.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)
            || openTab.ReturnType.GetGenericArguments()[0] != browserTabType)
        {
            throw new MissingMethodException(
                "MainForm.OpenNewTabAsync no longer matches the private churn contract.");
        }

        var closeTab = typeof(MainForm).GetMethod(
            "CloseTab",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [browserTabType, typeof(bool), typeof(bool)],
            modifiers: null);
        if (closeTab is null || closeTab.ReturnType != typeof(void))
        {
            throw new MissingMethodException(
                "MainForm.CloseTab no longer matches the private churn contract.");
        }
        var activateTab = typeof(MainForm).GetMethod(
            "ActivateTab",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [browserTabType, typeof(bool), typeof(bool)],
            modifiers: null);
        if (activateTab is null || activateTab.ReturnType != typeof(void))
        {
            throw new MissingMethodException(
                "MainForm.ActivateTab no longer matches the private churn contract.");
        }
        return new PrivateChurnApi(openTab, closeTab, activateTab);
    }

    private static async Task VerifyThreeTabResidencyAsync(
        MainForm form,
        PrivateChurnApi api,
        PrivateChurnOptions options)
    {
        var openTasks = Enumerable.Range(1, 3)
            .Select(slot => OpenTabAsync(
                form,
                api.OpenTab,
                BuildRetainedDocumentUri(options, slot).AbsoluteUri))
            .ToArray();
        var retainedTabs = new List<object>(3);
        try
        {
            await Task.WhenAll(openTasks);
            retainedTabs.AddRange(openTasks.Select(task => task.Result));
            await WaitForRetainedDocumentsAsync(options, retainedTabs);
            for (var round = 0; round < 3; round++)
            {
                foreach (var tab in retainedTabs)
                {
                    ActivateTab(form, api.ActivateTab, tab);
                    await Task.Delay(100);
                }
            }
            await Task.Delay(500);
            await PostControlAsync(options, "/retention-check", retainedTabs.Count.ToString());
        }
        finally
        {
            if (retainedTabs.Count == 0)
            {
                retainedTabs.AddRange(openTasks
                    .Where(task => task.IsCompletedSuccessfully)
                    .Select(task => task.Result));
            }
            for (var index = retainedTabs.Count - 1; index >= 0; index--)
            {
                CloseTab(form, api.CloseTab, retainedTabs[index]);
            }
        }
    }

    private static async Task<object> OpenTabAsync(
        MainForm form,
        MethodInfo openTab,
        string address)
    {
        object invocation;
        try
        {
            invocation = openTab.Invoke(form, [address, true, false, false, false, null])
                ?? throw new InvalidOperationException("OpenNewTabAsync returned no task.");
        }
        catch (TargetInvocationException error)
        {
            throw error.InnerException ?? error;
        }
        if (invocation is not Task task)
        {
            throw new InvalidOperationException("OpenNewTabAsync did not return a Task.");
        }
        await task;
        var tab = openTab.ReturnType.GetProperty(nameof(Task<object>.Result))?.GetValue(invocation);
        return tab ?? throw new InvalidOperationException("OpenNewTabAsync returned no tab.");
    }

    private static void CloseTab(MainForm form, MethodInfo closeTab, object tab)
    {
        try
        {
            closeTab.Invoke(form, [tab, false, false]);
        }
        catch (TargetInvocationException error)
        {
            throw error.InnerException ?? error;
        }
    }

    private static void ActivateTab(MainForm form, MethodInfo activateTab, object tab)
    {
        try
        {
            activateTab.Invoke(form, [tab, false, true]);
        }
        catch (TargetInvocationException error)
        {
            throw error.InnerException ?? error;
        }
    }

    private static async Task WaitForChurnStartAsync(PrivateChurnOptions options)
    {
        var deadline = DateTime.UtcNow + ChurnStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await LoopbackClient.GetStringAsync(BuildLoopbackUri(options, "/control"));
            var command = response.Trim();
            if (command.Equals("START", StringComparison.Ordinal)) return;
            if (!command.Equals("WAIT", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected churn control response: {command}");
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("The memory harness did not start private churn within three minutes.");
    }

    private static async Task WaitForRetainedDocumentsAsync(
        PrivateChurnOptions options,
        IReadOnlyList<object> retainedTabs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await LoopbackClient.GetStringAsync(
                BuildLoopbackUri(options, "/retention-status"));
            var status = response.Trim();
            if (status.Equals("READY", StringComparison.Ordinal)) return;
            if (!status.Equals("WAIT", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected retained-document status: {status}");
            }
            await Task.Delay(100);
        }
        var diagnostics = string.Join(" | ", retainedTabs.Select(DescribeTabForFailure));
        throw new TimeoutException(
            $"The three retained documents did not finish their first load. {diagnostics}");
    }

    private static string DescribeTabForFailure(object tab)
    {
        var type = tab.GetType();
        object? Read(string name) => type.GetProperty(name)?.GetValue(tab);
        var core = Read("Core");
        var source = core?.GetType().GetProperty("Source")?.GetValue(core);
        return $"url={Read("Url")}; discarded={Read("IsDiscarded")}; "
            + $"initializing={Read("IsInitializing")}; loading={Read("IsLoading")}; "
            + $"status={Read("StatusText")}; blocked={Read("BlockedRequestCount")}; "
            + $"core={core is not null}; source={source}";
    }

    private static async Task PostControlAsync(
        PrivateChurnOptions options,
        string path,
        string body)
    {
        using var content = new StringContent(body);
        using var response = await LoopbackClient.PostAsync(BuildLoopbackUri(options, path), content);
        var reply = (await response.Content.ReadAsStringAsync()).Trim();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{path} returned {(int)response.StatusCode} ({response.StatusCode}): {reply}");
        }
        if (!reply.Equals("OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected {path} response: {reply}");
        }
    }

    private static async Task TryReportFailureAsync(PrivateChurnOptions options, Exception error)
    {
        try
        {
            var message = error.ToString();
            if (message.Length > 4_096) message = message[..4_096];
            await PostControlAsync(options, "/failure", message);
        }
        catch
        {
            // The exact launcher exits non-zero even if the loopback harness is gone.
        }
    }

    private static Uri BuildLoopbackUri(
        PrivateChurnOptions options,
        string path,
        string extraQuery = "")
    {
        var builder = new UriBuilder(options.StartupAddress)
        {
            Path = path,
            Query = string.IsNullOrEmpty(extraQuery)
                ? $"token={options.Token}"
                : $"token={options.Token}&{extraQuery}",
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri;
    }

    private static Uri BuildRetainedDocumentUri(
        PrivateChurnOptions options,
        int slot)
    {
        var builder = new UriBuilder(options.StartupAddress)
        {
            Fragment = $"misha-retained-{slot}"
        };
        return builder.Uri;
    }

    private static MainForm CreateBrowser(string startupAddress, bool useNormalProfile)
    {
        var browserAssembly = typeof(MainForm).Assembly;
        var browserModeType = browserAssembly.GetType(
            "MishaWeb.BrowserMode",
            throwOnError: true)!;
        var browserMode = Enum.Parse(browserModeType, useNormalProfile ? "Normal" : "Private");
        var constructor = typeof(MainForm)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 4
                    && parameters[0].ParameterType == typeof(bool)
                    && parameters[2].ParameterType == typeof(string)
                    && parameters[3].ParameterType == browserModeType;
            })
            ?? throw new MissingMethodException("The MishaWeb private-window constructor was not found.");

        object? previewState = null;
        if (!useNormalProfile
            && Uri.TryCreate(startupAddress, UriKind.Absolute, out var startupUri)
            && startupUri.Host.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            // The deterministic fixture measures lifecycle behavior, not
            // redirect heuristics. Keep the full blocker compiled while making
            // only this exact loopback host a shield exception so synthetic
            // query markers cannot be mistaken for authored redirect rules.
            var browserStateType = browserAssembly.GetType(
                "MishaWeb.BrowserState",
                throwOnError: true)!;
            previewState = Activator.CreateInstance(browserStateType)
                ?? throw new InvalidOperationException("Could not create the private probe state.");
            if (browserStateType.GetProperty("AdBlockExceptionHosts")?.GetValue(previewState)
                is not IList<string> exceptionHosts)
            {
                throw new MissingMemberException(
                    "BrowserState.AdBlockExceptionHosts no longer matches the probe contract.");
            }
            exceptionHosts.Add("127.0.0.1");
        }

        var form = (MainForm)constructor.Invoke([true, previewState, startupAddress, browserMode]);
        if (previewState is not null)
        {
            var isExceptionHost = typeof(MainForm).GetMethod(
                "IsExceptionHost",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("MainForm.IsExceptionHost was not found.");
            if (isExceptionHost.Invoke(form, ["127.0.0.1"]) is not true)
            {
                form.Dispose();
                throw new InvalidOperationException(
                    "The private loopback fixture was not isolated from shield redirect rules.");
            }
        }
        return form;
    }

    private sealed record LaunchOptions(
        Uri Address,
        bool UseNormalProfile,
        PrivateChurnOptions? Churn);

    private sealed record PrivateChurnOptions(
        Uri StartupAddress,
        string Token,
        int Cycles,
        int BatchSize,
        int DwellMs);

    private sealed record PrivateChurnApi(
        MethodInfo OpenTab,
        MethodInfo CloseTab,
        MethodInfo ActivateTab);
}
