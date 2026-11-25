using System.Text.Json;
using System.Text;
using CodexInstanceManager;

internal static class RelayCallerTests
{
    public static async Task RunAsync()
    {
        string Root = Path.Combine(Path.GetTempPath(), "AIMoreMoreWork-Callers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        try
        {
            const string Shared = "shared-session-id";
            string CursorId = Guid.NewGuid().ToString("D");
            string ClaudeId = Guid.NewGuid().ToString("D");
            var Profiles = new List<InstanceProfile> { new() { Id = "codex-main" }, new() { Id = "codex-child" }, new() { Id = "codex-grandchild" } };
            var Settings = new ManagerSettings { InstanceList = Profiles, Dsh = new() { ServerUrl = "http://localhost:8123" } };
            var Store = new RelayStore(Root);
            string CursorRoot = Path.Combine(Root, "cursor");
            new RelayCliSessionStore(Path.Combine(CursorRoot, "sessions")).Save(new(CursorId, "Cursor fixture", Root, "", ""));
            string ClaudeHome = Path.Combine(Root, "claude");
            string TranscriptFolder = Directory.CreateDirectory(Path.Combine(ClaudeHome, "projects", "fixture")).FullName;
            File.WriteAllText(Path.Combine(TranscriptFolder, ClaudeId + ".jsonl"), JsonSerializer.Serialize(new { sessionId = ClaudeId }) + "\n");
            using var Cursor = new CursorRelayRunner(CursorRoot);
            using var Claude = new ClaudeRelayRunner(Root: Path.Combine(Root, "claude-manager"), CodeHome: ClaudeHome);
            using var Dsh = new DshRelayRunner();
            var Desktop = new FakeDesktop(Shared);
            var Bridge = new FakeBridge();
            using var Coordinator = new RelayCoordinator(Store, Bridge, Cursor, Dsh, Claude, new AntigravityRelayRunner(), Desktop);
            var Agents = new AgentManagementService(Store, Coordinator, new FakeCatalog(Shared), Environment.ProcessPath!, () => Settings);
            string PipeName = "AIMoreMoreWork-CallerTest-" + Guid.NewGuid().ToString("N");
            using var Host = new RelayHost(Coordinator, PipeName, Agents);

            Task<JsonElement> Call(string Provider, string Thread, string Command, object Arguments)
                => RelayHost.RequestAsync(new(Provider, Thread, Command, JsonSerializer.SerializeToElement(Arguments)), CancellationToken.None, PipeName);
            await RejectAsync(() => Call("unknown", Shared, "agent_instances", new { }), "实例未找到");

            foreach (var (Provider, Thread, Mode) in new[]
            {
                ("antigravity", Shared, "desktop"), ("dsh", Shared, "desktop"), ("cursor", CursorId, "inbox"), ("claude", ClaudeId, "inbox")
            })
            {
                string Missing = Provider switch
                {
                    "cursor" => "未找到 Cursor CLI 会话", "claude" => "找不到已保存的 Claude Code 会话", _ => "会话不存在"
                };
                await RejectAsync(() => Call(Provider, Guid.NewGuid().ToString("D"), "agent_instances", new { }), Missing);
                Require((await Call(Provider, Thread, "agent_instances", new { })).GetArrayLength() == 7, "调用方能读取全部实例 " + Provider);
                Require((await Call(Provider, Thread, "agent_projects", new { instance = "codex-main" })).ValueKind == JsonValueKind.Array, "调用方能读取 Codex 项目 " + Provider);
                var Sessions = await Call(Provider, Thread, "agent_sessions", new { instance = "codex-main", session = Shared });
                Require(Sessions[0].GetProperty("Id").GetString() == Shared, "调用方能读取 Codex 会话 " + Provider);
                var Request = new
                {
                    instance = "codex-main", session = Shared, request_id = "same-request", title = "read only", instructions = "read a known value",
                    acceptance = "matches value", workspace = new { access = "read_only" }, session_max = 0, cross_agent_max = 1,
                    tree_session_max = 2, max_depth = Provider == "dsh" ? 1 : 2, worker_limits = new { session_max = 0, cross_agent_max = 1, subagent_max = 0 }
                };
                var Started = await Call(Provider, Thread, "agent_start", Request);
                string TaskId = Started.GetProperty("task_id").GetString()!;
                Require((await Call(Provider, Thread, "agent_start", Request)).GetProperty("task_id").GetString() == TaskId, "请求重放保留任务 " + Provider);
                Require(Store.Get(TaskId).TreeRootId == Provider + "/" + Thread, "任务树包含执行端命名空间");
                Require((await Call(Provider, Thread, "agent_resume", new { task_id = TaskId })).GetProperty("state").GetString() == RelayLimits.StateText(eRelayState.Running), "原主会话可核对运行中的续跑请求");
                if (Provider == "antigravity")
                {
                    RelayChannel Route(InstanceProfile Main, InstanceProfile Worker) => new() { MainInstance = Main, MainThreadId = Shared, WorkerInstance = Worker, WorkerThreadId = Shared };
                    var Antigravity = new InstanceProfile { Id = Provider };
                    await RejectAsync(() => Coordinator.DispatchAsync(Profiles[0].Id, Shared, "cycle", "title", "work", "done", Route(Profiles[0], Antigravity)), "禁止形成循环");
                    await RejectAsync(() => Coordinator.DispatchAsync(Profiles[0].Id, Shared, "write", "title", "work", "done", Route(Profiles[0], Profiles[1]), SessionMax: 0, SubagentMax: 0, Workspace: new() { Access = "write", WritePathList = new() { Root } }), "只读任务");
                    var Nested = await Coordinator.DispatchAsync(Profiles[0].Id, Shared, "nested", "title", "work", "done", Route(Profiles[0], Profiles[1]), SessionMax: 0, SubagentMax: 0, Workspace: new() { Access = "read_only" });
                    Require(Nested.ParentTaskId == TaskId && Nested.TreeDepth == 2, "跨端子任务继承深度与父任务");
                    await RejectAsync(() => Coordinator.DispatchAsync(Profiles[1].Id, Shared, "beyond", "title", "work", "done", Route(Profiles[1], Profiles[2]), SessionMax: 0, SubagentMax: 0), "tree_session_max");
                    var Tree = await Call(Provider, Thread, "agent_tree", new { task_id = TaskId });
                    Require(Tree.GetArrayLength() == 1 && Tree[0].GetProperty("task_id").GetString() == Nested.Id, "外部主会话能读取子任务");
                    await Coordinator.CancelAsync(Nested.Id);
                }
                int notified = Desktop.SentList.Count;
                if (Provider == "dsh")
                {
                    var Child = new RelayChannel { MainInstance = Profiles[0], MainThreadId = Shared, WorkerInstance = Profiles[1], WorkerThreadId = Shared };
                    await RejectAsync(() => Coordinator.DispatchAsync(Profiles[0].Id, Shared, "too-deep", "title", "work", "done", Child, SessionMax: 0, SubagentMax: 0), "max_depth");
                    string Outbox = Path.Combine(Root, "outbox");
                    var Callback = new RelayRequest("codex-main", Shared, "relay_report", JsonSerializer.SerializeToElement(new { task_id = TaskId, outcome = "completed", summary = "observed", artifacts = Array.Empty<string>() }));
                    RelayOutbox.Save(Callback, Outbox);
                    Desktop.FailDshOnce = true;
                    Require(await RelayOutbox.ReplayOnceAsync(Coordinator, CancellationToken.None, Outbox) == (1, 0), "离线回报补录到原 DSH 主会话");
                    Require(Store.Get(TaskId).State == eRelayState.ReturnUncertain, "DSH 离线时保存回报和投递待核对状态");
                    await Call(Provider, Thread, "agent_retry_return", new { task_id = TaskId, confirmed = true });
                    Require(Desktop.DshRequestList.Count == 2 && Desktop.DshRequestList[0] == Desktop.DshRequestList[1], "DSH 重试使用原通知请求 ID");
                }
                var Reported = await Coordinator.ReportAsync("codex-main", Shared, TaskId, "completed", "observed", new());
                Require(Reported.State == (Mode == "inbox" ? eRelayState.PendingPull : eRelayState.Returned), "回传状态符合执行端 " + Provider);
                Require(Bridge.ReturnCount == 0, "外部主会话没有误发到 Codex");
                Require(Desktop.SentList.Count == notified + (Mode == "desktop" ? 1 : 0), "只唤醒支持桌面投递的主会话 " + Provider);
                if (Mode == "desktop") Require(Desktop.SentList[^1] == (Provider, Thread), "回传路由到原主会话");
                var Inbox = await Call(Provider, Thread, "agent_inbox", new { });
                Require(Inbox.GetProperty("items")[0].GetProperty("task_id").GetString() == TaskId, "持久结果进入原主会话收件箱");
                var Status = await Call(Provider, Thread, "agent_task", new { task_id = TaskId });
                Require(Status.GetProperty("main").GetProperty("notification").GetString() == Mode, "查询如实报告通知方式");
                Require(Status.GetProperty("main").GetProperty("instance_kind").GetString() == Provider && Status.GetProperty("worker").GetProperty("instance_kind").GetString() == "codex", "任务记录公开两端类型");
                if (Mode == "inbox")
                    Require((await Call(Provider, Thread, "agent_retry_return", new { task_id = TaskId, confirmed = true })).GetProperty("state").GetString() == RelayLimits.StateText(eRelayState.PendingPull), "拉取端重试保留原结果和状态");
                await RejectAsync(() => Call("codex-child", Shared, "relay_accept", new { task_id = TaskId }), "不属于此联调任务");
                await Call(Provider, Thread, "relay_accept", new { task_id = TaskId });
                Require(Store.Get(TaskId).State == eRelayState.Accepted, "外部主会话可验收");
            }

            using var Output = new MemoryStream();
            using var Error = new StringWriter();
            string[] Arguments = { "instances", "--caller-instance", "antigravity", "--caller-thread", Shared };
            int result = await AgentCli.RunAsync(Arguments, new MemoryStream(), Output, Error, Environment.ProcessPath!, CancellationToken.None, PipeName, () => Settings);
            Require(result == 0 && Output.Length > 0, "CLI 接受显式 Antigravity 调用方 " + Error);
            string Message(int Id, object Params) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Id, method = "tools/call", @params = Params });
            string Requests = Message(1, new { name = "agent_instances", arguments = new { } }) + "\n" +
                Message(2, new { name = "agent_instances", arguments = new { }, _meta = new { threadId = "different" } }) + "\n";
            using var McpOutput = new MemoryStream();
            await RelayMcp.RunAsync("antigravity", new MemoryStream(Encoding.UTF8.GetBytes(Requests)), McpOutput, CancellationToken.None, PipeName, BoundThreadId: Shared);
            var ResponseList = Encoding.UTF8.GetString(McpOutput.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            using var Response = JsonDocument.Parse(ResponseList[0]);
            using var Mismatch = JsonDocument.Parse(ResponseList[1]);
            Require(!Response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "MCP 使用启动时绑定的外部会话");
            Require(Mismatch.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "MCP 拒绝与宿主冲突的会话身份");
            Console.WriteLine("PASS all caller types, CLI identity, cross-provider budgets and cycles, notification routing and acceptance");
        }
        finally
        {
            if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new InvalidOperationException("测试清理路径越界");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static async Task RejectAsync(Func<Task> Call, string Message)
    {
        try { await Call(); }
        catch (InvalidOperationException Error) when (Error.Message.Contains(Message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("预期拒绝 " + Message);
    }

    private static void Require(bool Condition, string Message)
    {
        if (!Condition) throw new InvalidOperationException(Message);
    }

    private sealed class FakeDesktop(string SessionId) : IRelayCallerDesktop
    {
        public List<(string Provider, string Thread)> SentList { get; } = new();
        public List<string> DshRequestList { get; } = new();
        public bool FailDshOnce { get; set; }
        public Task<string> RequireAntigravityAsync(string ThreadId, CancellationToken Cancellation) => Task.FromResult(Title(ThreadId));
        public Task<(string Title, string HomePath)> RequireDshAsync(DshSettings Settings, string ThreadId, CancellationToken Cancellation) => Task.FromResult((Title(ThreadId), Path.GetTempPath()));
        public Task NotifyAntigravityAsync(string ThreadId, string RequestId, string Prompt, CancellationToken Cancellation) => Record("antigravity", ThreadId, Prompt);
        public Task NotifyDshAsync(DshSettings Settings, string ThreadId, string RequestId, string Prompt, CancellationToken Cancellation)
        {
            Require(Settings.ServerUrl == "http://localhost:8123", "DSH 回传使用原主会话服务配置");
            DshRequestList.Add(RequestId);
            if (FailDshOnce) { FailDshOnce = false; throw new DesktopUnavailableException("DSH fixture offline"); }
            return Record("dsh", ThreadId, Prompt);
        }
        private string Title(string ThreadId) => ThreadId == SessionId ? "external fixture" : throw new InvalidOperationException("会话不存在");
        private Task Record(string Provider, string ThreadId, string Prompt)
        {
            Require(Prompt.Contains("--caller-thread '" + ThreadId + "'"), "外部回传携带真实主会话调用参数");
            SentList.Add((Provider, ThreadId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBridge : IRelayBridge
    {
        public int ReturnCount { get; private set; }
        public Task SendAsync(InstanceProfile Instance, string ThreadId, string Prompt, string Model, CancellationToken Cancellation)
        {
            if (Prompt.Contains("联调回传")) ReturnCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCatalog(string SessionId) : IAgentCatalog
    {
        public Task<List<AgentProject>> ProjectsAsync(InstanceProfile Instance, CancellationToken Cancellation) => Task.FromResult(new List<AgentProject>());
        public List<AgentSession> Sessions(InstanceProfile Instance, AgentProject? Project, string? Selector, int Offset) => new() { Session(Instance, SessionId) };
        public AgentSession Session(InstanceProfile Instance, string Id) => Id == SessionId ? new(Id, "codex fixture", "", "") : throw new InvalidOperationException("会话不存在");
        public Task RequireIdleAsync(InstanceProfile Instance, string ThreadId, CancellationToken Cancellation) => Task.CompletedTask;
        public Task<RelayThread> CreateAsync(InstanceProfile Instance, AgentProject? Project, string Title, string Prompt, CancellationToken Cancellation) => throw new NotSupportedException();
    }
}
