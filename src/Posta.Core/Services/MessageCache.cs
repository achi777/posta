using Microsoft.Data.Sqlite;
using Posta.Core.Models;

namespace Posta.Core.Services;

/// <summary>
/// Local SQLite cache of message headers per folder, so folders open instantly (like an offline store)
/// and are refreshed from the server in the background.
/// </summary>
public sealed class MessageCache : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    private sealed class CachedItem
    {
        public string Id { get; set; } = "";
        public string ItemClass { get; set; } = "";
        public string? ConversationId { get; set; }
        public string FromName { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string DisplayTo { get; set; } = "";
        public DateTime Received { get; set; }
        public DateTime Sent { get; set; }
        public int Size { get; set; }
        public bool HasAttachments { get; set; }
        public string Subject { get; set; } = "";
        public string Preview { get; set; } = "";
        public bool IsRead { get; set; }
        public Importance Importance { get; set; }
        public FlagState Flag { get; set; }
        public DateTime? FlagDue { get; set; }
        public List<string> Categories { get; set; } = new();
        public bool IsDraft { get; set; }
    }

    public MessageCache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS items(
                 account TEXT NOT NULL, folder TEXT NOT NULL, id TEXT NOT NULL, received INTEGER NOT NULL, json TEXT NOT NULL,
                 PRIMARY KEY(account, folder, id));");
        Exec("CREATE INDEX IF NOT EXISTS ix_items_folder ON items(account, folder, received DESC);");
        Exec(@"CREATE TABLE IF NOT EXISTS folders(account TEXT NOT NULL, mailbox TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(account, mailbox));");
        Exec(@"CREATE TABLE IF NOT EXISTS opened_messages(account TEXT NOT NULL, id TEXT NOT NULL,
                 saved INTEGER NOT NULL, encrypted BLOB NOT NULL, PRIMARY KEY(account, id));");
    }

    public byte[]? GetOpenedMessage(string accountId, string itemId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT encrypted FROM opened_messages WHERE account=$a AND id=$i";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$i", itemId);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public void SaveOpenedMessage(string accountId, string itemId, byte[] encrypted)
    {
        lock (_lock)
        {
            using var transaction = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR REPLACE INTO opened_messages(account,id,saved,encrypted) VALUES($a,$i,$t,$e);
                DELETE FROM opened_messages WHERE account=$a AND id IN
                    (SELECT id FROM opened_messages WHERE account=$a ORDER BY saved DESC, id LIMIT -1 OFFSET 100);
                """;
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$e", encrypted);
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MailItemSummary> GetItems(string accountId, string folderId, int max = 300)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM items WHERE account=$a AND folder=$f ORDER BY received DESC LIMIT $m";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$f", folderId);
            cmd.Parameters.AddWithValue("$m", max);
            var result = new List<MailItemSummary>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var c = SettingsService.Deserialize<CachedItem>(r.GetString(0));
                if (c == null) continue;
                result.Add(new MailItemSummary
                {
                    Id = c.Id, AccountId = accountId, FolderId = folderId, ItemClass = c.ItemClass, ConversationId = c.ConversationId,
                    From = new Recipient(c.FromName, c.FromAddress), DisplayTo = c.DisplayTo, Received = c.Received, Sent = c.Sent,
                    Size = c.Size, HasAttachments = c.HasAttachments, Subject = c.Subject, Preview = c.Preview, IsRead = c.IsRead,
                    Importance = c.Importance, Flag = c.Flag, FlagDue = c.FlagDue, Categories = c.Categories, IsDraft = c.IsDraft
                });
            }
            return result;
        }
    }

    /// <summary>Replaces the cached first page of a folder with fresh server data.</summary>
    public void ReplaceItems(string accountId, string folderId, IEnumerable<MailItemSummary> items)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var del = _db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM items WHERE account=$a AND folder=$f";
                del.Parameters.AddWithValue("$a", accountId);
                del.Parameters.AddWithValue("$f", folderId);
                del.ExecuteNonQuery();
            }
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO items(account, folder, id, received, json) VALUES($a,$f,$i,$r,$j)";
            var pa = ins.Parameters.Add("$a", SqliteType.Text);
            var pf = ins.Parameters.Add("$f", SqliteType.Text);
            var pi = ins.Parameters.Add("$i", SqliteType.Text);
            var pr = ins.Parameters.Add("$r", SqliteType.Integer);
            var pj = ins.Parameters.Add("$j", SqliteType.Text);
            foreach (var s in items)
            {
                pa.Value = accountId; pf.Value = folderId; pi.Value = s.Id; pr.Value = s.Received.Ticks;
                pj.Value = SettingsService.Serialize(new CachedItem
                {
                    Id = s.Id, ItemClass = s.ItemClass, ConversationId = s.ConversationId, FromName = s.From.Name, FromAddress = s.From.Address,
                    DisplayTo = s.DisplayTo, Received = s.Received, Sent = s.Sent, Size = s.Size, HasAttachments = s.HasAttachments,
                    Subject = s.Subject, Preview = s.Preview, IsRead = s.IsRead, Importance = s.Importance, Flag = s.Flag, FlagDue = s.FlagDue,
                    Categories = s.Categories, IsDraft = s.IsDraft
                });
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void RemoveItems(string accountId, IEnumerable<string> ids)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE account=$a AND id=$i; DELETE FROM opened_messages WHERE account=$a AND id=$i;";
            var pa = cmd.Parameters.Add("$a", SqliteType.Text);
            var pi = cmd.Parameters.Add("$i", SqliteType.Text);
            foreach (var id in ids) { pa.Value = accountId; pi.Value = id; cmd.ExecuteNonQuery(); }
        }
    }

    public void SaveFolders(string accountId, string mailbox, IEnumerable<MailFolderInfo> folders)
    {
        var data = folders.Select(f => new FolderRow
        {
            Id = f.Id, ParentId = f.ParentId, Kind = f.Kind, FolderClass = f.FolderClass, DisplayName = f.DisplayName,
            Unread = f.UnreadCount, Total = f.TotalCount, Children = f.ChildFolderCount, MailboxEmail = f.MailboxEmail
        }).ToList();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO folders(account, mailbox, json) VALUES($a,$m,$j)";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$m", mailbox);
            cmd.Parameters.AddWithValue("$j", SettingsService.Serialize(data));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<MailFolderInfo>? GetFolders(string accountId, string mailbox)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM folders WHERE account=$a AND mailbox=$m";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$m", mailbox);
            var json = cmd.ExecuteScalar() as string;
            if (json == null) return null;
            var rows = SettingsService.Deserialize<List<FolderRow>>(json);
            return rows?.Select(r => new MailFolderInfo
            {
                Id = r.Id, ParentId = r.ParentId, AccountId = accountId, Kind = r.Kind, FolderClass = r.FolderClass, DisplayName = r.DisplayName,
                UnreadCount = r.Unread, TotalCount = r.Total, ChildFolderCount = r.Children, MailboxEmail = r.MailboxEmail
            }).ToList();
        }
    }

    public void ClearAccount(string accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE account=$a; DELETE FROM folders WHERE account=$a; DELETE FROM opened_messages WHERE account=$a;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class FolderRow
    {
        public string Id { get; set; } = "";
        public string? ParentId { get; set; }
        public FolderKind Kind { get; set; }
        public string FolderClass { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Unread { get; set; }
        public int Total { get; set; }
        public int Children { get; set; }
        public string? MailboxEmail { get; set; }
    }

    public void Dispose() => _db.Dispose();
}
