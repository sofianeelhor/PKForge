using PKForge.Sync.Server.Accounts;

namespace PKForge.Sync.Tests.Infrastructure;

/// <summary>The test server's <see cref="IAccountWritePolicy"/>: allows every push until <see cref="DenyReason"/> is set.</summary>
public sealed class WriteSwitch : IAccountWritePolicy
{
    private volatile string? _denyReason;
    private volatile bool _denied;

    /// <summary>Refuses every account's pushes with <paramref name="reason"/> (null: the server's own neutral message).</summary>
    public void Deny(string? reason = null)
    {
        _denyReason = reason;
        _denied = true;
    }

    public void Allow() => _denied = false;

    public Task<WriteDecision> CheckAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult(_denied ? new WriteDecision(false, _denyReason) : WriteDecision.Allow);
}
