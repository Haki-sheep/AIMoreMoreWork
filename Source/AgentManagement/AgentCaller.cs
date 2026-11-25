namespace CodexInstanceManager;

internal sealed record AgentCaller(InstanceProfile Profile, string ThreadId, string Title, string DshServerUrl = "");

internal readonly record struct RelaySessionIdentity(string InstanceId, string ThreadId)
{
    public string RootKey => InstanceId + "/" + ThreadId;
    public static bool IsExternal(string InstanceId) => InstanceId is AntigravityRelayRunner.InstanceId or DshRelayRunner.InstanceId or ClaudeRelayRunner.InstanceId or CursorRelayRunner.InstanceId;
    public static bool PullsResults(string InstanceId) => InstanceId is ClaudeRelayRunner.InstanceId or CursorRelayRunner.InstanceId;
    public static string Kind(string InstanceId) => IsExternal(InstanceId) ? InstanceId : "codex";

    public static InstanceProfile ResolveProfile(ManagerSettings Settings, string Selector)
    {
        if (Settings.InstanceList.Any(Item => IsExternal(Item.Id))) throw new InvalidOperationException("Codex 实例 ID 与保留的执行端 ID 冲突");
        return Selector switch
        {
            AntigravityRelayRunner.InstanceId => new() { Id = Selector, Name = AntigravityRelayRunner.Name, HomePath = AntigravityDesktopClient.HomePath },
            DshRelayRunner.InstanceId => new() { Id = Selector, Name = "DeepSeek Harness", HomePath = Settings.Dsh.HomePath },
            ClaudeRelayRunner.InstanceId => ClaudeRelayRunner.Profile,
            CursorRelayRunner.InstanceId => CursorRelayRunner.Profile,
            _ => AgentManagementService.ResolveInstance(Settings, Selector.StartsWith("codex:", StringComparison.Ordinal) ? Selector[6..] : Selector)
        };
    }
}

public interface IRelayCallerDesktop
{
    Task<string> RequireAntigravityAsync(string ThreadId, CancellationToken Cancellation);
    Task<(string Title, string HomePath)> RequireDshAsync(DshSettings Settings, string ThreadId, CancellationToken Cancellation);
    Task NotifyAntigravityAsync(string ThreadId, string RequestId, string Prompt, CancellationToken Cancellation);
    Task NotifyDshAsync(DshSettings Settings, string ThreadId, string RequestId, string Prompt, CancellationToken Cancellation);
}

internal sealed class RelayCallerDesktop : IRelayCallerDesktop
{
    private readonly SemaphoreSlim AntigravitySend = new(1, 1);

    public async Task<string> RequireAntigravityAsync(string ThreadId, CancellationToken Cancellation)
    {
        using var Client = AntigravityDesktopClient.Connect();
        return (await RequireAntigravitySessionAsync(Client, ThreadId, Cancellation)).Title;
    }

    public async Task<(string Title, string HomePath)> RequireDshAsync(DshSettings Settings, string ThreadId, CancellationToken Cancellation)
    {
        using var Client = await DshDesktopClient.ConnectAsync(Settings, Cancellation);
        var Session = await Client.SessionAsync(ThreadId, Cancellation);
        return (Session.Title, Client.Connection.HomePath);
    }

    public async Task NotifyAntigravityAsync(string ThreadId, string RequestId, string Prompt, CancellationToken Cancellation)
    {
        using var Client = AntigravityDesktopClient.Connect();
        var Session = await RequireAntigravitySessionAsync(Client, ThreadId, Cancellation);
        using var Wait = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        Wait.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            while (true)
            {
                var Snapshot = await Client.TrajectoryAsync(ThreadId, Wait.Token);
                if (AntigravityDesktopClient.IsIdle(Snapshot))
                {
                    int stepCount = AntigravityDesktopClient.StepCount(Snapshot);
                    var Model = await Client.ModelAsync(ThreadId, Session.LastInput, stepCount, Wait.Token);
                    await AntigravitySend.WaitAsync(Wait.Token);
                    try
                    {
                        var Current = await Client.TrajectoryAsync(ThreadId, Wait.Token);
                        if (AntigravityDesktopClient.IsIdle(Current) && AntigravityDesktopClient.StepCount(Current) == stepCount)
                        {
                            await Client.PromptAsync(ThreadId, RequestId, Prompt, Model, Cancellation);
                            return;
                        }
                    }
                    finally { AntigravitySend.Release(); }
                    Session = await RequireAntigravitySessionAsync(Client, ThreadId, Wait.Token);
                }
                await Task.Delay(500, Wait.Token);
            }
        }
        catch (OperationCanceledException) when (Wait.IsCancellationRequested && !Cancellation.IsCancellationRequested)
        {
            throw new DesktopUnavailableException("原 Antigravity 会话未在等待期内空闲 可从收件箱读取结果");
        }
    }

    public async Task NotifyDshAsync(DshSettings Settings, string ThreadId, string RequestId, string Prompt, CancellationToken Cancellation)
    {
        using var Client = await DshDesktopClient.ConnectAsync(Settings, Cancellation);
        await Client.SessionAsync(ThreadId, Cancellation);
        await Client.PromptAsync(ThreadId, RequestId, Prompt, Cancellation);
    }

    private static async Task<AntigravitySession> RequireAntigravitySessionAsync(AntigravityDesktopClient Client, string ThreadId, CancellationToken Cancellation)
        => (await Client.SessionsAsync(Cancellation)).SingleOrDefault(Item => Item.Id == ThreadId)
            ?? throw new InvalidOperationException("未找到原 Antigravity 会话 " + ThreadId);
}
