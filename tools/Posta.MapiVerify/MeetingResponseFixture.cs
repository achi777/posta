using System.Net;
using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;
using Posta.Mapi;

internal static class MeetingResponseFixture
{
    internal static async Task VerifyAsync(NativeMapiMailService service, AccountConfig account, string? selfAlias, CancellationToken ct)
    {
        var host = account.ServerUrl.Contains("://", StringComparison.Ordinal) ? account.ServerUrl : "https://" + account.ServerUrl;
        var discovery = new Uri(new Uri(host), "/autodiscover/autodiscover.xml");
        var user = account.EffectiveUser;
        var domain = account.Domain;
        if (user.Contains('\\')) { var parts = user.Split('\\', 2); domain = parts[0]; user = parts[1]; }
        ICredentials credentials = account.UseWindowsCredentials ? CredentialCache.DefaultNetworkCredentials
            : new NetworkCredential(user, CredentialStore.Load(account.Id), domain);
        var endpoint = await MapiHttpSession.DiscoverAsync(discovery, account.Email, credentials, ct);
        if (endpoint.MailStore.GetLeftPart(UriPartial.Authority) != discovery.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("The fixture requires the configured Exchange origin.");
        if (selfAlias != null)
        {
            var aliasEndpoint = await MapiHttpSession.DiscoverAsync(discovery, selfAlias, credentials, ct);
            if (!aliasEndpoint.LegacyDn.Equals(endpoint.LegacyDn, StringComparison.OrdinalIgnoreCase) ||
                selfAlias.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reply testing requires a secondary SMTP address of the same mailbox.");
        }
        await using var session = new MapiHttpSession(endpoint.MailStore, credentials);
        await session.ConnectAsync(endpoint.LegacyDn, ct);
        var logon = await session.LogonAsync(endpoint.LegacyDn, ct);
        var calendar = (await service.GetOwnCalendarsAsync(ct)).Single(c => c.IsDefault);
        var calendarFolder = Convert.ToUInt64(calendar.FolderId[7..], 16);
        var appointmentSet = new Guid("00062002-0000-0000-c000-000000000046");
        var meetingSet = new Guid("6ed8da90-450b-101b-98da-00aa003f1305");
        var tags = await session.ResolvePropertyTagsAsync(logon,
        [
            new(appointmentSet, 0x820d, 0x40), new(appointmentSet, 0x820e, 0x40), new(appointmentSet, 0x8208, 0x1f),
            new(appointmentSet, 0x8205, 3), new(appointmentSet, 0x8217, 3), new(appointmentSet, 0x8218, 3),
            new(appointmentSet, 0x8224, 3), new(appointmentSet, 0x8201, 3), new(meetingSet, 3, 0x102),
            new(meetingSet, 0x23, 0x102), new(meetingSet, 0x26, 3)
        ], true, ct);
        var token = Guid.NewGuid();
        var global = MapiWire.Write(w =>
        {
            w.Write(Convert.FromHexString("040000008200E00074C5B7101A82E008")); w.Write(0u);
            w.Write(DateTime.UtcNow.ToFileTimeUtc()); w.Write(0ul); w.Write(16u); w.Write(token.ToByteArray());
        });
        var start = DateTime.Today.AddDays(9).AddHours(10);
        var end = start.AddMinutes(30);
        var body = Encoding.UTF8.GetBytes("<p>საცდელი მოწვევა</p>");
        var properties = new Dictionary<uint, object>
        {
            [0x001a001f] = "IPM.Appointment", [0x0037001f] = "Posta response fixture " + token.ToString("N"),
            [0x1000001f] = "საცდელი მოწვევა", [0x3fde0003] = 65001u, [0x0063000b] = false,
            [tags[0]] = start, [tags[1]] = end, [tags[2]] = "საცდელი ოთახი", [tags[3]] = 1u, [tags[4]] = 3u,
            [tags[5]] = 5u, [tags[6]] = 2u, [tags[7]] = 0u, [tags[8]] = global, [tags[9]] = global
        };
        var cleanup = new List<string>();
        string ItemId(ulong folder, ulong message) => $"mapi-m:{folder:X16}:{message:X16}";
        try
        {
            var appointment = await session.WriteMessageAsync(logon, calendarFolder, null, properties, body, [], [], [], false, ct);
            var appointmentId = ItemId(calendarFolder, appointment.Id);
            cleanup.Add(appointmentId);
            properties[0x001a001f] = "IPM.Schedule.Meeting.Request";
            properties[tags[10]] = 1u;
            var inbox = logon.FolderIds[4];
            var invitation = await session.WriteMessageAsync(logon, inbox, null, properties, body, [], [], [], false, ct);
            var invitationId = ItemId(inbox, invitation.Id);
            cleanup.Add(invitationId);
            var details = await service.GetMessageAsync(invitationId, ct);
            if (!details.IsMeetingRequest || details.MeetingStart != start || details.MeetingEnd != end || details.MeetingLocation != "საცდელი ოთახი")
                throw new InvalidOperationException("The request metadata did not roundtrip.");
            await service.RespondToMeetingAsync(invitationId, MeetingResponse.Accept, false, null, ct);
            var accepted = await service.GetEventAsync(appointmentId, ct);
            if (accepted.MyResponse != MeetingResponse.Accept || accepted.ShowAs != ShowAs.Busy)
                throw new InvalidOperationException("The incoming invitation did not update its matching calendar entry.");
            await service.RespondToMeetingAsync(appointmentId, MeetingResponse.Tentative, false, null, ct);
            var tentative = await service.GetEventAsync(appointmentId, ct);
            if (tentative.MyResponse != MeetingResponse.Tentative || tentative.ShowAs != ShowAs.Tentative)
                throw new InvalidOperationException("The tentative response did not roundtrip.");
            await service.RespondToMeetingAsync(appointmentId, MeetingResponse.Decline, false, null, ct);
            cleanup.Remove(appointmentId); // Decline moved it to Deleted Items.
            if ((await service.GetEventsAsync(calendar, start.Date, start.Date.AddDays(1), ct)).Any(e => e.Id == appointmentId))
                throw new InvalidOperationException("The declined meeting remained in the calendar.");
            Console.WriteLine("MEETING_ACCEPT_TENTATIVE_DECLINE_WITHOUT_SENDING_OK");

            properties[0x001a001f] = "IPM.Appointment";
            var replacement = await session.WriteMessageAsync(logon, calendarFolder, null, properties, body, [], [], [], false, ct);
            var replacementId = ItemId(calendarFolder, replacement.Id);
            cleanup.Add(replacementId);
            properties[0x001a001f] = "IPM.Schedule.Meeting.Canceled";
            properties[tags[4]] = 7u;
            properties[tags[7]] = 1u;
            var cancellation = await session.WriteMessageAsync(logon, inbox, null, properties, body, [], [], [], false, ct);
            var cancellationId = ItemId(inbox, cancellation.Id);
            cleanup.Add(cancellationId);
            await service.RespondToMeetingAsync(cancellationId, MeetingResponse.Decline, false, null, ct);
            cleanup.Remove(replacementId);
            if ((await service.GetEventsAsync(calendar, start.Date, start.Date.AddDays(1), ct)).Any(e => e.Id == replacementId))
                throw new InvalidOperationException("The canceled received meeting remained in the calendar.");
            Console.WriteLine("RECEIVED_MEETING_CANCELLATION_REMOVAL_OK");
            if (selfAlias == null) return;

            properties[0x001a001f] = "IPM.Appointment";
            properties[0x0063000b] = true;
            properties[0x0042001f] = "Posta verification organizer";
            properties[0x5d02001f] = selfAlias;
            properties[tags[4]] = 3u;
            var responseTarget = await session.WriteMessageAsync(logon, calendarFolder, null, properties, body, [], [], [], false, ct);
            var responseTargetId = ItemId(calendarFolder, responseTarget.Id);
            cleanup.Add(responseTargetId);
            await service.RespondToMeetingAsync(responseTargetId, MeetingResponse.Accept, true, "<p>ქართული საცდელი პასუხი</p>", ct);
            var sent = await service.GetWellKnownFolderAsync(FolderKind.SentItems, ct: ct)
                ?? throw new InvalidOperationException("Sent Items unavailable.");
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var page = await service.GetItemsAsync(new MailQuery { FolderId = sent.Id, Search = token.ToString("N"), PageSize = 10 }, ct);
                if (page.Items.FirstOrDefault(item => item.ItemClass == "IPM.Schedule.Meeting.Resp.Pos") is { } responseItem)
                {
                    cleanup.Add(responseItem.Id);
                    var reply = await service.GetMessageAsync(responseItem.Id, ct);
                    if (!reply.BodyHtml.Contains("ქართული საცდელი პასუხი", StringComparison.Ordinal))
                        throw new InvalidOperationException("The response body did not roundtrip.");
                    Console.WriteLine("MEETING_REPLY_SUBMITTED_AND_SENT_COPY_OK");
                    return;
                }
                await Task.Delay(1000, ct);
            }
            throw new InvalidOperationException("The meeting response was not observed in Sent Items.");
        }
        finally
        {
            if (cleanup.Count != 0) await service.DeleteItemsAsync(cleanup, false, ct);
        }
    }
}
