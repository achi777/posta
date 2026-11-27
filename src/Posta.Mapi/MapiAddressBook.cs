using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Posta.Mapi;

// NSPI address book over MAPI/HTTP (MS-OXNSPI, MS-OXCMAPIHTTP section 2.2.5). A separate HTTPS
// endpoint and session from the mailbox store; used only to read directory (GAL) entries.
// Uses Bind + paged QueryRows (the same operations the ruler tool relies on). Server-side
// ambiguous-name resolution (GetMatches) is intentionally not used: its restriction wire format is
// inconsistently documented and this Exchange build rejects the MS-OXCDATA encoding, so the client
// pages the directory and filters locally instead.
public sealed class MapiAddressBook : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _clientId = Guid.NewGuid().ToString("B");
    private int _sequence;
    private bool _bound;
    private bool _poisoned;

    // Unicode (001F) and Int32 (0003) columns only, so the row reader stays small and unambiguous.
    public static readonly uint[] PersonColumns =
    [0x3001001f, 0x39fe001f, 0x3003001f, 0x3a00001f, 0x0ffe0003, 0x39000003,
     0x3a17001f, 0x3a18001f, 0x3a16001f, 0x3a19001f, 0x3a08001f, 0x3a1c001f];

    public MapiAddressBook(Uri endpoint, ICredentials credentials)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("A credential-free HTTPS address book endpoint is required.");
        _endpoint = endpoint;
        _http = MapiHttpSession.CreateHttp(endpoint, credentials);
    }

    internal MapiAddressBook(Uri endpoint, HttpClient http) { _endpoint = endpoint; _http = http; }

    private async Task<byte[]> PostAsync(string operation, byte[] body, CancellationToken ct)
    {
        if (_poisoned) throw new MapiException("The address book session is no longer usable.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http");
        request.Headers.Add("X-RequestType", operation);
        request.Headers.Add("X-RequestId", $"{_clientId}:{Interlocked.Increment(ref _sequence)}");
        request.Headers.Add("X-ClientInfo", _clientId);
        request.Headers.Add("X-ClientApplication", "Outlook/15.00.0847.4040");
        request.Headers.UserAgent.ParseAdd("Posta/1.0");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var code = response.Headers.TryGetValues("X-ResponseCode", out var codes) ? codes.FirstOrDefault() : null;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new MapiException($"Address book {operation}: HTTP {(int)response.StatusCode}, X-ResponseCode {code ?? "none"}.");
            if (code is not (null or "0"))
                throw new MapiException($"Address book {operation}: X-ResponseCode {code}. {Diagnostic(bytes)}");
            if (mediaType != "application/mapi-http")
                throw new MapiException($"Address book {operation}: unexpected content type '{mediaType ?? "none"}' (X-ResponseCode {code ?? "none"}). {Diagnostic(bytes)}");
            return MapiWire.Unwrap(bytes).Body;
        }
        catch (Exception ex) when (ex is not MapiException) { _poisoned = true; throw; }
    }

    private static string Diagnostic(byte[] bytes)
    {
        if (bytes.Length == 0) return "(empty body)";
        var printable = new string(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200)).Where(c => c >= 0x20 && c < 0x7f).ToArray()).Trim();
        return printable.Length == 0 ? $"(binary body, {bytes.Length} bytes)" : "Body: " + printable;
    }

    // MS-OXNSPI 2.2.8 STAT. NspiBind rejects CP_WINUNICODE (1200) with MAPI_E_UNKNOWN_CPID, so the
    // code page is an 8-bit Windows page (1252); explicitly requested 001F columns still return Unicode.
    private static byte[] Stat(uint currentRec, uint numPos, uint containerId = 0) => MapiWire.Write(w =>
    {
        w.Write(0u);          // SortTypeDisplayName
        w.Write(containerId); // ContainerID (0 = default GAL)
        w.Write(currentRec);  // CurrentRec
        w.Write(0u);          // Delta
        w.Write(numPos);      // NumPos (fractional position)
        w.Write(0u);          // TotalRecs
        w.Write(1252u);       // CodePage (1200/CP_WINUNICODE is forbidden on Bind)
        w.Write(0x409u); w.Write(0x409u); // TemplateLocale, SortLocale (en-US)
    });

    private static void PropTags(BinaryWriter w, uint[] tags) { w.Write((uint)tags.Length); foreach (var t in tags) w.Write(t); }

    private async Task EnsureBoundAsync(CancellationToken ct)
    {
        if (_bound) return;
        var body = MapiWire.Write(w => { w.Write(0u); w.Write((byte)1); w.Write(Stat(0, 0)); w.Write(0u); });
        var r = new WireReader(await PostAsync("Bind", body, ct));
        MapiWire.Success(r.U32(), "AddressBook Bind status"); MapiWire.Success(r.U32(), "AddressBook Bind");
        r.Read(16); // ServerGuid
        _bound = true;
    }

    // Reads the whole default GAL, paging with QueryRows and carrying the server's STAT forward, up to a cap.
    public async Task<IReadOnlyList<Dictionary<uint, object?>>> LoadDirectoryAsync(int max, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            await EnsureBoundAsync(ct);
            var all = new List<Dictionary<uint, object?>>();
            byte[] stat = Stat(0, 0); // MID_BEGINNING
            int guard = 0;
            while (all.Count < max)
            {
                var pageSize = Math.Min(1000, max - all.Count);
                var (rows, next, hadState) = await QueryRowsPageAsync(stat, pageSize, ct);
                all.AddRange(rows);
                if (rows.Count == 0 || !hadState || ++guard > 1000) break;
                if (SamePosition(stat, next)) break; // server did not advance: end of table
                stat = next;
            }
            return all;
        }
        catch (Exception ex) when (ex is not MapiException) { _poisoned = true; throw new MapiException("AddressBook QueryRows: " + ex.Message); }
        finally { _serial.Release(); }
    }

    private async Task<(List<Dictionary<uint, object?>> Rows, byte[] NextStat, bool HadState)> QueryRowsPageAsync(byte[] stat, int rowCount, CancellationToken ct)
    {
        var body = MapiWire.Write(w =>
        {
            w.Write(0u);                       // Flags
            w.Write((byte)1); w.Write(stat);   // HasState + STAT
            w.Write(0u);                       // ExplicitTableCount = 0
            w.Write(checked((uint)rowCount));  // RowCount
            w.Write((byte)1); PropTags(w, PersonColumns);
            w.Write(0u);                       // AuxiliaryBufferSize
        });
        var r = new WireReader(await PostAsync("QueryRows", body, ct));
        MapiWire.Success(r.U32(), "QueryRows status"); MapiWire.Success(r.U32(), "QueryRows");
        bool hadState = r.U8() != 0;
        var nextStat = hadState ? r.Read(36) : stat;
        return (ReadRowSet(r), nextStat, hadState);
    }

    private static bool SamePosition(byte[] a, byte[] b)
        => a.Length == b.Length && a.AsSpan(8, 4).SequenceEqual(b.AsSpan(8, 4)) && a.AsSpan(16, 4).SequenceEqual(b.AsSpan(16, 4));

    internal async Task<IReadOnlyList<Dictionary<uint, object?>>> ReadGroupMembersAsync(string distinguishedName, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            await EnsureBoundAsync(ct);
            var idRequest = MapiWire.Write(w =>
            {
                w.Write(0u);
                w.Write((byte)1);
                w.Write(1u);
                MapiWire.AsciiZ(w, distinguishedName);
                w.Write(0u);
            });
            var reader = new WireReader(await PostAsync("DNToMId", idRequest, ct));
            MapiWire.Success(reader.U32(), "DNToMId status");
            MapiWire.Success(reader.U32(), "DNToMId");
            if (reader.U8() == 0 || reader.U32() != 1)
                throw new MapiException("The address book did not return one entry ID for the group.");
            var id = reader.U32();
            if (id == 0) throw new MapiException("Exchange could not resolve the group entry ID.");
            ReadAuxiliary(reader);

            const uint membership = 0x8009100d;
            const int maxMembers = 5000;
            uint[] columns = [0x3001001f, 0x39fe001f];
            var request = MapiWire.Write(w =>
            {
                w.Write(0u);
                w.Write((byte)1);
                w.Write(Stat(id, 0, membership));
                w.Write((byte)0); // No explicit input table.
                w.Write(0u); // Interface options.
                w.Write((byte)0); // No restriction.
                w.Write((byte)0); // No named property.
                w.Write((uint)(maxMembers + 1));
                w.Write((byte)1);
                PropTags(w, columns);
                w.Write(0u);
            });
            reader = new WireReader(await PostAsync("GetMatches", request, ct));
            MapiWire.Success(reader.U32(), "Group membership status");
            MapiWire.Success(reader.U32(), "Group membership");
            if (reader.U8() != 0) reader.Read(36);
            uint count = 0;
            if (reader.U8() != 0)
            {
                count = reader.U32();
                if (count > maxMembers) throw new MapiException("The distribution group is too large to expand safely.");
                reader.Read(checked((int)count * 4));
            }
            var rows = ReadRowSet(reader);
            ReadAuxiliary(reader);
            if (rows.Count != count || rows.Count > maxMembers)
                throw new MapiException("Exchange did not return all distribution group members.");
            return rows;
        }
        finally { _serial.Release(); }
    }

    private static void ReadAuxiliary(WireReader reader)
    {
        reader.Read(checked((int)reader.U32()));
        MapiWriteWire.End(reader);
    }

    // HasColsAndRows(1) [+ Columns] then RowCount(4) then AddressBookPropertyRow[].
    private static List<Dictionary<uint, object?>> ReadRowSet(WireReader r)
    {
        var rows = new List<Dictionary<uint, object?>>();
        if (r.U8() == 0) return rows; // HasColsAndRows == false
        var count = r.U32(); var columns = new uint[count];
        for (int i = 0; i < count; i++) columns[i] = r.U32();
        var rowCount = r.U32();
        if (rowCount > 100000) throw new MapiException("Implausible address book row count.");
        for (uint i = 0; i < rowCount; i++) rows.Add(ReadAbRow(r, columns));
        return rows;
    }

    private static Dictionary<uint, object?> ReadAbRow(WireReader r, uint[] columns)
    {
        var rowFlag = r.U8();
        if (rowFlag is not (0 or 1)) throw new MapiException("Invalid address book row flag.");
        var flagged = rowFlag == 1;
        var values = new Dictionary<uint, object?>();
        foreach (var tag in columns)
        {
            if (flagged)
            {
                var flag = r.U8();
                if (flag == 1) { values[tag] = null; continue; }
                if (flag == 0x0a) { values[tag] = new MapiCellError(r.U32()); continue; }
                if (flag != 0) throw new MapiException("Invalid address book property flag.");
            }
            // The flag wraps an AddressBookPropertyValue. Variable-length values
            // retain their presence byte inside both flagged and unflagged rows.
            values[tag] = ReadValue(r, (ushort)tag);
        }
        return values;
    }

    private static object? ReadValue(WireReader r, ushort type)
    {
        if (type is 0x001f or 0x001e or 0x0102 or 0x101f or 0x1102 && r.U8() == 0) return null;
        return type switch
        {
            0x0003 => r.U32(),
            0x000b => r.U16() != 0,
            0x0040 => DateTime.FromFileTimeUtc(checked((long)r.U64())),
            0x001f => r.Text(true),
            0x001e => r.Text(false),
            0x0102 => r.Read(checked((int)r.U32())),
            0x000a => new MapiCellError(r.U32()),
            _ => throw new MapiException($"Unsupported address book property type 0x{type:X4}.")
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try
        {
            if (_bound && !_poisoned)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await PostAsync("Unbind", MapiWire.Write(w => { w.Write(0u); w.Write(0u); }), timeout.Token); } catch { }
            }
            _bound = false; _poisoned = true; _http.Dispose();
        }
        finally { _serial.Release(); }
    }
}
