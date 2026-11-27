using Posta.Core.Models;
using Posta.Core.Services;
using Posta.Mapi;
using Xunit;

public class PreviewTests
{
    [Fact]
    public async Task UnsupportedFeaturesNeverReportFalseSuccessOrEmptyMailboxState()
    {
        await using var service = new NativeMapiMailService(new AccountConfig());
        await Assert.ThrowsAsync<MailServiceException>(() => service.GetOofAsync());
        await Assert.ThrowsAsync<MailServiceException>(() => service.GetFolderPermissionsAsync("folder"));
        await Assert.ThrowsAsync<MailServiceException>(() => service.SendAsync(new OutgoingMessage()));
        await Assert.ThrowsAsync<MailServiceException>(() => service.SaveDraftAsync(new OutgoingMessage()));
        await Assert.ThrowsAsync<MailServiceException>(() => service.GetOwnCalendarsAsync());
        await Assert.ThrowsAsync<MailServiceException>(() => service.ResolveNamesAsync("user"));
    }

    [Fact]
    public async Task PreviewRequiresAnExplicitServerBeforeAnyCredentialTransmission()
    {
        await using var service = new NativeMapiMailService(new AccountConfig { Email = "user@example.test" });
        await Assert.ThrowsAsync<MailServiceException>(() => service.ConnectAsync("test-only-value"));
        Assert.Equal(ConnectionState.Error, service.State);
        Assert.Contains("specify", service.LastError);
    }
}
