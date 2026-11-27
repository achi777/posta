namespace Posta.Mapi;

// Explicit refusals: never report missing functionality as an empty or successful result.
public sealed partial class NativeMapiMailService
{
    public Task IgnoreConversationAsync(string itemId, CancellationToken ct = default) => throw Unsupported();
}
