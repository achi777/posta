using System.Text;
using System.Xml;
using System.Xml.Linq;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace ExchangeMessages = "http://schemas.microsoft.com/exchange/services/2006/messages";
    private static readonly XNamespace ExchangeTypes = "http://schemas.microsoft.com/exchange/services/2006/types";

    public Task<OofSettingsInfo> GetOofAsync(CancellationToken ct = default) => Run(async () =>
    {
        var result = await AutomaticRepliesRequestAsync("GetUserOofSettings", null, ct);
        var settings = result.Element(ExchangeTypes + "OofSettings") ?? result.Element(ExchangeMessages + "OofSettings")
            ?? throw new MailServiceException("Exchange did not return Automatic Replies settings.");
        var info = new OofSettingsInfo
        {
            State = ReadOption<OofState>(settings, "OofState"),
            ExternalAudience = ReadOption<OofExternalAudience>(settings, "ExternalAudience"),
            InternalReplyHtml = (string?)settings.Element(ExchangeTypes + "InternalReply")?.Element(ExchangeTypes + "Message") ?? "",
            ExternalReplyHtml = (string?)settings.Element(ExchangeTypes + "ExternalReply")?.Element(ExchangeTypes + "Message") ?? ""
        };
        if (settings.Element(ExchangeTypes + "Duration") is { } duration)
        {
            if ((string?)duration.Element(ExchangeTypes + "StartTime") is { } start)
                info.Start = XmlConvert.ToDateTime(start, XmlDateTimeSerializationMode.Utc).ToLocalTime();
            if ((string?)duration.Element(ExchangeTypes + "EndTime") is { } end)
                info.End = XmlConvert.ToDateTime(end, XmlDateTimeSerializationMode.Utc).ToLocalTime();
        }
        return info;
    }, ct);

    private static T ReadOption<T>(XElement settings, string name) where T : struct, Enum
    {
        if (Enum.TryParse<T>((string?)settings.Element(ExchangeTypes + name), out var value) && Enum.IsDefined(value))
            return value;
        throw new MailServiceException("Exchange returned an unknown Automatic Replies option.");
    }

    public Task SetOofAsync(OofSettingsInfo settings, CancellationToken ct = default) => Run(async () =>
    {
        if (!Enum.IsDefined(settings.State) || !Enum.IsDefined(settings.ExternalAudience) ||
            (settings.State == OofState.Scheduled && settings.Start >= settings.End))
            throw new MailServiceException("Invalid Automatic Replies schedule or audience.");
        var data = new XElement(ExchangeTypes + "UserOofSettings",
            new XElement(ExchangeTypes + "OofState", settings.State.ToString()),
            new XElement(ExchangeTypes + "ExternalAudience", settings.ExternalAudience.ToString()));
        if (settings.State == OofState.Scheduled)
            data.Add(new XElement(ExchangeTypes + "Duration",
                new XElement(ExchangeTypes + "StartTime", XmlConvert.ToString(settings.Start.ToUniversalTime(), XmlDateTimeSerializationMode.Utc)),
                new XElement(ExchangeTypes + "EndTime", XmlConvert.ToString(settings.End.ToUniversalTime(), XmlDateTimeSerializationMode.Utc))));
        data.Add(new XElement(ExchangeTypes + "InternalReply", new XElement(ExchangeTypes + "Message", settings.InternalReplyHtml)),
            new XElement(ExchangeTypes + "ExternalReply", new XElement(ExchangeTypes + "Message", settings.ExternalReplyHtml)));
        await AutomaticRepliesRequestAsync("SetUserOofSettings", data, ct);
        return true;
    }, ct);

    private async Task<XElement> AutomaticRepliesRequestAsync(string operation, XElement? settings, CancellationToken ct)
    {
        if (State != ConnectionState.Connected || _discoveryUri == null || _credentials == null)
            throw new MailServiceException("Connect to Exchange before opening Automatic Replies.");
        // OOF is an Exchange web-service operation. Keep credentials on the same configured HTTPS origin.
        var endpoint = new Uri(_discoveryUri, "/EWS/Exchange.asmx");
        using var client = MapiHttpSession.CreateHttp(endpoint, _credentials);
        client.Timeout = TimeSpan.FromSeconds(8);
        var mailbox = new XElement(ExchangeTypes + "Mailbox",
            new XElement(ExchangeTypes + "Address", Account.Email),
            new XElement(ExchangeTypes + "RoutingType", "SMTP"));
        var document = new XElement(Soap + "Envelope", new XElement(Soap + "Body",
            new XElement(ExchangeMessages + (operation + "Request"), mailbox, settings)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ExchangeMessages}/{operation}\"");
        string responseXml;
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new MailServiceException($"Automatic Replies requires Exchange Web Services. The configured server returned HTTP {(int)response.StatusCode}.");
            responseXml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MailServiceException("Automatic Replies could not reach Exchange Web Services on the configured server.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MailServiceException("Exchange Web Services did not respond to Automatic Replies.", ex);
        }
        using var reader = XmlReader.Create(new StringReader(responseXml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        var answer = XDocument.Load(reader).Root?.Element(Soap + "Body")?.Element(ExchangeMessages + (operation + "Response"))
            ?? throw new MailServiceException("Exchange Web Services returned an unexpected Automatic Replies response.");
        var code = (string?)answer.Element(ExchangeMessages + "ResponseMessage")?.Element(ExchangeMessages + "ResponseCode");
        if (code != "NoError") throw new MailServiceException($"Exchange rejected Automatic Replies ({code ?? "unknown error"}).");
        return answer;
    }
}
