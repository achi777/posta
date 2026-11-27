using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Posta.Mapi;

public sealed record MapiEndpoint(Uri MailStore, string LegacyDn, Uri? AddressBook = null);
public sealed record MapiConnection(string DisplayName, string? ServerVersion);
public sealed record MapiLogon(uint Handle, IReadOnlyList<ulong> FolderIds, Guid MailboxGuid = default);
public sealed record MapiRows(uint Total, IReadOnlyList<Dictionary<uint, object?>> Rows);

/// <summary>Direct HTTPS/NTLM MAPI transport. Does not load Outlook or read its profile.</summary>
public sealed partial class MapiHttpSession : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _clientId = Guid.NewGuid().ToString("B");
    private int _sequence;
    private bool _connected;
    private bool _poisoned;
    public bool IsUsable => _connected && !_poisoned;

    public MapiHttpSession(Uri endpoint, ICredentials credentials)
    {
        ValidateEndpoint(endpoint);
        _endpoint = endpoint;
        _http = CreateHttp(endpoint, credentials);
    }

    internal MapiHttpSession(Uri endpoint, HttpClient http)
    {
        ValidateEndpoint(endpoint);
        _endpoint = endpoint;
        _http = http;
    }

    internal static HttpClient CreateHttp(Uri scope, ICredentials credentials)
    {
        // Restrict credential negotiation to this explicitly selected HTTPS origin.
        var cache = new CredentialCache();
        var origin = new Uri(scope.GetLeftPart(UriPartial.Authority) + "/");
        foreach (var scheme in new[] { "Negotiate", "NTLM" })
        {
            var credential = credentials.GetCredential(origin, scheme);
            if (credential != null) cache.Add(origin, scheme, credential);
        }
        return new HttpClient(new HttpClientHandler
        {
            Credentials = cache, AllowAutoRedirect = false,
            UseCookies = true, CookieContainer = new CookieContainer(),
            MaxConnectionsPerServer = 1,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("A credential-free HTTPS endpoint is required.");
    }

    public static MapiEndpoint ParseAutodiscover(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
        });
        var doc = XDocument.Load(reader);
        string? dn = doc.Descendants().FirstOrDefault(n => n.Name.LocalName == "LegacyDN")?.Value;
        var protocol = doc.Descendants().FirstOrDefault(n => n.Name.LocalName == "Protocol" &&
            (n.Attribute("Type")?.Value == "mapiHttp" || n.Elements().Any(e => e.Name.LocalName == "Type" && e.Value == "mapiHttp")));
        static string? UrlOf(XElement? section) =>
            section?.Elements().FirstOrDefault(n => n.Name.LocalName == "ExternalUrl")?.Value is { Length: > 0 } ext ? ext
            : section?.Elements().FirstOrDefault(n => n.Name.LocalName == "InternalUrl")?.Value;
        var store = protocol?.Elements().FirstOrDefault(n => n.Name.LocalName == "MailStore");
        string? url = UrlOf(store);
        if (string.IsNullOrWhiteSpace(dn) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
            throw new MapiException("Autodiscover did not return a MAPI/HTTP MailStore URL and LegacyDN. Redirects are not followed automatically.");
        ValidateEndpoint(endpoint);
        Uri? addressBook = null;
        if (UrlOf(protocol?.Elements().FirstOrDefault(n => n.Name.LocalName == "AddressBook")) is { Length: > 0 } ab
            && Uri.TryCreate(ab, UriKind.Absolute, out var abUri) && abUri.Scheme == "https" && abUri.Fragment.Length == 0 && abUri.UserInfo.Length == 0)
            addressBook = abUri;
        return new(endpoint, dn, addressBook);
    }

    public static async Task<MapiEndpoint> DiscoverAsync(Uri autodiscover, string email, ICredentials credentials, CancellationToken ct = default)
    {
        ValidateEndpoint(autodiscover);
        XNamespace requestNs = "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006";
        var xml = new XDocument(new XElement(requestNs + "Autodiscover", new XElement(requestNs + "Request",
            new XElement(requestNs + "EMailAddress", email), new XElement(requestNs + "AcceptableResponseSchema",
            "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a"))));
        using var client = CreateHttp(autodiscover, credentials);
        using var request = new HttpRequestMessage(HttpMethod.Post, autodiscover);
        request.Headers.Add("X-MapiHttpCapability", "1");
        request.Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new MapiException($"Autodiscover returned HTTP {(int)response.StatusCode}.");
        return ParseAutodiscover(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<(byte[] body, string? server)> PostAsync(string operation, byte[] body, CancellationToken ct)
    {
        if (_poisoned) throw new MapiException("The MAPI session is no longer usable. Start a new connection.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http");
        request.Headers.Add("X-RequestType", operation);
        request.Headers.Add("X-RequestId", $"{_clientId}:{Interlocked.Increment(ref _sequence)}");
        request.Headers.Add("X-ClientInfo", _clientId);
        // MS-OXCMAPIHTTP 2.2.3.3.6 specifies Outlook/version syntax; identify this application
        // separately in User-Agent. This is protocol version negotiation, not an Office dependency.
        request.Headers.Add("X-ClientApplication", "Outlook/15.00.0847.4040");
        request.Headers.UserAgent.ParseAdd("Posta/1.0");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new MapiException($"{operation}: HTTP {(int)response.StatusCode}. No alternative credentials were attempted.");
            if (response.Content.Headers.ContentType?.MediaType != "application/mapi-http")
                throw new MapiException($"{operation}: server did not return a MAPI/HTTP response.");
            if (!response.Headers.TryGetValues("X-ResponseCode", out var codes) || codes.Single() != "0")
                throw new MapiException($"{operation}: server rejected the MAPI transport request.");
            var payload = MapiWire.Unwrap(await response.Content.ReadAsByteArrayAsync(ct));
            if (payload.Headers.TryGetValue("X-ResponseCode", out var code) && code != "0")
                throw new MapiException($"{operation}: MAPI response code {code}.");
            var server = response.Headers.TryGetValues("X-ServerApplication", out var versions) ? versions.FirstOrDefault() : null;
            return (payload.Body, server);
        }
        catch { _poisoned = true; throw; } // Do not replay a possibly processed operation.
    }

    public async Task<MapiConnection> ConnectAsync(string legacyDn, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            if (_connected) throw new MapiException("Session is already connected.");
            var reply = await PostAsync("Connect", MapiWire.Connect(legacyDn), ct);
            var result = ParseConnection(reply.body, reply.server);
            _connected = true;
            return result;
        }
        catch { _poisoned = true; throw; }
        finally { _serial.Release(); }
    }

    public static MapiConnection ParseConnection(byte[] body, string? server = null)
    {
        var r = new WireReader(body);
        MapiWire.Success(r.U32(), "Connect transport"); MapiWire.Success(r.U32(), "Connect");
        r.U32(); r.U32(); r.U32(); r.Text(false);
        var name = r.Text(true);
        r.Read(checked((int)r.U32()));
        if (r.Remaining != 0) throw new MapiException("Unexpected Connect response tail.");
        return new(name, server);
    }

    public async Task<RopPacket> ExecuteAsync(byte[] operations, uint[] handles, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            if (!_connected) throw new MapiException("Connect must succeed before Execute.");
            var reply = await PostAsync("Execute", MapiWire.Execute(MapiWire.Frame(operations, handles)), ct);
            return MapiWire.ReadExecute(reply.body);
        }
        catch { _poisoned = true; throw; }
        finally { _serial.Release(); }
    }

    public Task<MapiLogon> LogonAsync(string legacyDn, CancellationToken ct = default)
        => LogonMailboxAsync(legacyDn, 0x01000000, ct);

    internal Task<MapiLogon> LogonDelegatorAsync(string legacyDn, CancellationToken ct)
        => LogonMailboxAsync(legacyDn, 0x0100040c, ct);

    private async Task<MapiLogon> LogonMailboxAsync(string legacyDn, uint openFlags, CancellationToken ct)
    {
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 0xfe, 0, 0, 1 }); w.Write(openFlags); w.Write(0u);
            w.Write(checked((ushort)(legacyDn.Length + 1))); MapiWire.AsciiZ(w, legacyDn);
        });
        var packet = await ExecuteAsync(ops, [uint.MaxValue], ct);
        return ParseLogon(packet);
    }

    public static MapiLogon ParseLogon(RopPacket packet)
    {
        var r = new WireReader(packet.Operations);
        MapiWire.RopHeader(r, 0xfe, 0); r.U8();
        var ids = Enumerable.Range(0, 13).Select(_ => r.U64()).ToArray();
        r.U8(); var mailboxGuid = new Guid(r.Read(16)); r.Read(2 + 16 + 8 + 8 + 4);
        if (r.Remaining != 0 || packet.Handles.Length != 1 || packet.Handles[0] == uint.MaxValue)
            throw new MapiException("Invalid private mailbox logon response.");
        return new(packet.Handles[0], ids, mailboxGuid);
    }

    public static readonly uint[] HierarchyColumns = [0x67480014, 0x67490014, 0x3001001f, 0x3613001f, 0x36020003, 0x36030003];
    public static readonly uint[] ContentsColumns = [0x674a0014, 0x0037001f, 0x0c1a001f, 0x0e060040, 0x0e070003, 0x0e1b000b];

    public async Task<MapiRows> ReadTableAsync(MapiLogon logon, ulong folderId, bool hierarchy,
        uint[] columns, int maxRows = 50, CancellationToken ct = default, byte[]? restriction = null,
        IReadOnlyList<(uint Tag, bool Descending)>? sort = null, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRows, 5000);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var first = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folderId); w.Write((byte)0);
            w.Write(new byte[] { hierarchy ? (byte)4 : (byte)5, 0, 1, 2, hierarchy ? (byte)4 : (byte)0 });
            w.Write(new byte[] { 0x12, 0, 2, 0 }); w.Write(checked((ushort)columns.Length));
            foreach (var tag in columns) w.Write(tag);
            if (restriction != null)
            {
                w.Write(new byte[] { 0x14, 0, 2, 0 }); w.Write(checked((ushort)restriction.Length)); w.Write(restriction);
                w.Write(new byte[] { 0x17, 0, 2 });
            }
            if (sort is { Count: > 0 })
            {
                w.Write(new byte[] { 0x13, 0, 2, 0 });
                w.Write(checked((ushort)sort.Count)); w.Write((ushort)0); w.Write((ushort)0);
                foreach (var (tag, descending) in sort) { w.Write(tag); w.Write(descending ? (byte)1 : (byte)0); }
            }
            if (offset > 0)
            {
                w.Write(new byte[] { 0x18, 0, 2, 0 }); // Seek from the beginning of the sorted, restricted table.
                w.Write(offset); w.Write((byte)1);
            }
            Query(w, Math.Min(maxRows, 50));
            w.Write(new byte[] { 1, 0, 1 }); // RopRelease the folder; the table owns its own handle.
        });
        var packet = await ExecuteAsync(first, [logon.Handle, uint.MaxValue, uint.MaxValue], ct);
        uint[]? handles = null;
        bool completed = false;
        try
        {
            handles = packet.Handles;
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 2, 1); r.U8();
            if (r.U8() != 0) throw new MapiException("Ghosted folders need an additional server connection.");
            MapiWire.RopHeader(r, hierarchy ? (byte)4 : (byte)5, 2); uint total = r.U32();
            MapiWire.RopHeader(r, 0x12, 2);
            if (r.U8() != 0) throw new MapiException("SetColumns did not complete synchronously.");
            if (restriction != null)
            {
                MapiWire.RopHeader(r, 0x14, 2);
                if (r.U8() != 0) throw new MapiException("Search restriction did not complete synchronously.");
                MapiWire.RopHeader(r, 0x17, 2); r.U32(); total = r.U32(); // QueryPosition numerator, denominator.
            }
            if (sort is { Count: > 0 })
            {
                MapiWire.RopHeader(r, 0x13, 2);
                if (r.U8() != 0) throw new MapiException("SortTable did not complete synchronously.");
            }
            if (offset > 0)
            {
                MapiWire.RopHeader(r, 0x18, 2);
                r.U8(); r.U32(); // HasSoughtLess and RowsSought; QueryRows also handles the end bookmark.
            }
            var rows = new List<Dictionary<uint, object?>>();
            while (true)
            {
                MapiWire.RopHeader(r, 0x15, 2);
                var bookmark = r.U8(); var count = r.U16();
                for (int i = 0; i < count; i++) rows.Add(MapiWire.ReadRow(r, columns));
                if (r.Remaining != 0) throw new MapiException("Unexpected table response tail.");
                if (bookmark == 2 || count == 0 || rows.Count >= maxRows) break;
                packet = await ExecuteAsync(MapiWire.Write(w => Query(w, Math.Min(maxRows - rows.Count, 50))), handles, ct);
                handles = packet.Handles;
                r = new WireReader(packet.Operations);
            }
            completed = true;
            return new(total, rows);
        }
        catch (MapiException ex) when (ex.OperationCode.HasValue && handles is { Length: 3 })
        {
            // A valid ROP denial (for example a delegated folder without access) does not invalidate the session.
            if (handles[1] != uint.MaxValue) await ReleaseSafelyAsync(handles, 1);
            if (handles[2] != uint.MaxValue) await ReleaseSafelyAsync(handles, 2);
            completed = true;
            handles = null;
            throw;
        }
        finally
        {
            if (completed && !_poisoned && handles is { Length: 3 })
                await ExecuteAsync([1, 0, 2], handles, ct); // RopRelease table
            else if (!completed) _poisoned = true;
        }
    }

    private static void Query(BinaryWriter w, int count)
    {
        w.Write(new byte[] { 0x15, 0, 2, 0, 1 }); w.Write(checked((ushort)count));
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try
        {
            if (_connected && !_poisoned)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await PostAsync("Disconnect", new byte[4], timeout.Token); } catch { }
            }
            _connected = false; _poisoned = true; _http.Dispose();
        }
        finally { _serial.Release(); }
    }
}
