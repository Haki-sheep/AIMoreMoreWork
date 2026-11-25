using System.Text.Json;
using CodexInstanceManager;

internal static class RelayCallerLiveTests
{
    public static int RunCli(string Root, string PipeName, string[] Arguments)
    {
        if (!ManagerPaths.IsUnder(Root, Path.GetTempPath()) || !Path.GetFileName(Root).StartsWith("AIMoreMoreWork-LiveCaller-", StringComparison.Ordinal) || Arguments.Length == 0)
            throw new InvalidOperationException("联调测试 CLI 必须使用有效的隔离测试目录");
        using var Output = Console.OpenStandardOutput();
        int exitCode = AgentCli.RunAsync(Arguments, Console.OpenStandardInput(), Output, Console.Error, Environment.ProcessPath!, CancellationToken.None,
            PipeName, OutboxRoot: Path.Combine(Root, "outbox")).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(Root, "cli-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new { command = Arguments[0], exitCode }));
        return exitCode;
    }

    public static async Task RunAsync()
    {
        string Root = Path.Combine(Path.GetTempPath(), "AIMoreMoreWork-LiveCaller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        try
        {
            using var Timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var Cancellation = Timeout.Token;
            var Settings = SettingsStore.Load();
            var Worker = Settings.InstanceList.Single(Item => Item.IsPrimary);
            var Store = new RelayStore(Path.Combine(Root, "relay"));
            string PipeName = "AIMoreMoreWork-LiveCaller-" + Guid.NewGuid().ToString("N");
            string CallerId = Guid.NewGuid().ToString("D");
            string RequestId = "read-only-live-" + Guid.NewGuid().ToString("N");
            string ScriptPath = Path.Combine(Root, "aimoremorework.ps1");
            string Cli = "& " + Quote(ScriptPath);
            string CallerOptions = " --caller-instance antigravity --caller-thread " + Quote(CallerId);
            string AssemblyPath = typeof(RelayCallerLiveTests).Assembly.Location;
            File.WriteAllText(ScriptPath, $$"""
                if ($args[0] -in @('progress','report') -and $args -notcontains '--caller-instance') {
                    $args += @('--caller-instance', {{Quote(Worker.Id)}}, '--caller-thread', $env:CODEX_THREAD_ID)
                }
                & dotnet {{Quote(AssemblyPath)}} --relay-caller-test-cli {{Quote(Root)}} {{Quote(PipeName)}} @args
                exit $LASTEXITCODE
                """);
            string FixturePath = Path.Combine(Root, "read-only.txt");
            string Marker = "AIMMW-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(FixturePath, Marker);
            string RequestPath = Path.Combine(Root, "start.json");
            File.WriteAllText(RequestPath, JsonSerializer.Serialize(new
            {
                request_id = RequestId, instance = Worker.Id, create_title = "AIMoreMoreWork 多端主会话只读验收", title = "读取一行测试标记",
                instructions = $"只读取 UTF-8 文件 {FixturePath} 并把完整的一行内容作为结果摘要 不访问其他数据。只允许在 {Root} 下创建 CLI 回报和进度临时 JSON，测试脚本稍后会清理。按联调提示词用本次指定的 PowerShell CLI 上报。",
                acceptance = "摘要精确等于文件中的单行标记 无业务文件写入", workspace = new { access = "read_only" }, subagent_max = 0,
                worker_limits = new { subagent_max = 0, session_max = 0, cross_agent_max = 0 }, timeout_minutes = 3
            }));
            using var Bridge = new DesktopRelayBridge();
            using var Coordinator = new RelayCoordinator(Store, Bridge, Antigravity: new AntigravityRelayRunner());
            var Agents = new AgentManagementService(Store, Coordinator, new AgentCatalog(), ScriptPath, () => Settings);
            using var Host = new RelayHost(Coordinator, PipeName, Agents);
            using var Client = AntigravityDesktopClient.Connect();
            if (!await Client.SignedInAsync(Cancellation)) throw new InvalidOperationException("Antigravity 未登录");
            await Client.CreateAsync(CallerId, "AIMoreMoreWork 多端调用方真实测试", "", Cancellation);
            string CallerPrompt = $"""
                这是用户授权的一次只读联调测试。你是正在运行这条消息的真实 Antigravity 主会话。请直接执行下面三条 PowerShell 命令，最后一条只运行一次。
                {Cli} instances{CallerOptions}
                {Cli} sessions --instance {Quote(Worker.Id)}{CallerOptions}
                {Cli} start --json-file {Quote(RequestPath)}{CallerOptions}
                脚本使用隔离测试管理器。保留其返回的任务 ID，完成派发后结束本轮，等待本会话收到联调回传。不要轮询，不要新派任务。
                回传唤醒后，用同一脚本和调用方选项执行 inbox，再用 status --task-id <实际任务ID> 核对 summary。只有 summary 精确等于 {Marker} 才用 accept --task-id <实际任务ID> 验收。不要写入项目文件。
                """;
            var Model = await Client.ModelAsync(CallerId, -1, 0, Cancellation);
            await Client.PromptAsync(CallerId, Guid.NewGuid().ToString("D"), CallerPrompt, Model, Cancellation);
            Console.WriteLine($"LIVE Antigravity caller {CallerId}; real prompt submitted");
            string LastState = "";
            string Key = "antigravity/" + CallerId + "/" + RequestId;
            while (true)
            {
                var Item = Store.FindRequest(Key);
                if (Item is not null)
                {
                    string State = Item.State.ToString();
                    if (State != LastState) Console.WriteLine($"LIVE task {Item.Id} Codex {Item.Channel.WorkerThreadId} {State}");
                    LastState = State;
                    if (Item.State == eRelayState.Accepted)
                    {
                        if (Item.Result != Marker || Item.Channel.MainThreadId != CallerId ||
                            !Item.EventList.Any(Event => Event.Contains("原主会话已接收通知")))
                            throw new InvalidOperationException("真实任务未验证结果和回传归属");
                        var CommandHashList = Directory.EnumerateFiles(Root, "cli-*.json").Select(File.ReadAllText)
                            .Select(Text => JsonSerializer.Deserialize<CommandReceipt>(Text)).Where(Item => Item?.exitCode == 0).Select(Item => Item!.command).ToHashSet();
                        if (!new[] { "instances", "sessions", "start", "inbox", "status", "accept" }.All(CommandHashList.Contains))
                        {
                            await Task.Delay(250, Cancellation);
                            continue;
                        }
                        Console.WriteLine("PASS Antigravity issued instances sessions start and accepted the verified Codex result after desktop wakeup");
                        return;
                    }
                    if (Item.State is eRelayState.Cancelled or eRelayState.TimedOut || Item.Outcome is "failed" or "needs_decision")
                        throw new InvalidOperationException("真实任务未完成 " + Item.Id + " " + Item.Result);
                }
                await Task.Delay(750, Cancellation);
            }
        }
        finally
        {
            if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new InvalidOperationException("真实测试清理路径越界");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static string Quote(string Value) => "'" + Value.Replace("'", "''") + "'";

    private sealed record CommandReceipt(string command, int exitCode);
}
