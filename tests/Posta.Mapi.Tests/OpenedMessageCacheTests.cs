using Posta.Core.Services;
using Xunit;

public sealed class OpenedMessageCacheTests
{
    [Fact]
    public void OfflineCopiesAreBoundToAccountAndRemovedWhenAccountOrMessageIsRemoved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"posta-cache-{Guid.NewGuid():N}.db");
        try
        {
            using var cache = new MessageCache(path);
            cache.SaveOpenedMessage("account-a", "1", [1, 2, 3]);
            cache.SaveOpenedMessage("account-b", "1", [4, 5, 6]);
            Assert.Null(cache.GetOpenedMessage("account-c", "1"));
            Assert.Equal(new byte[] { 1, 2, 3 }, cache.GetOpenedMessage("account-a", "1"));
            cache.RemoveItems("account-a", ["1"]);
            Assert.Null(cache.GetOpenedMessage("account-a", "1"));
            Assert.Equal(new byte[] { 4, 5, 6 }, cache.GetOpenedMessage("account-b", "1"));
            cache.ClearAccount("account-b");
            Assert.Null(cache.GetOpenedMessage("account-b", "1"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
}
