# ფოსტა (Posta)

Windows-ის საფოსტო კლიენტი Microsoft Exchange-ისთვის. **ავტორი: Archil Odishelidze.**

Posta is a Windows desktop mail client for Microsoft Exchange. The interface is available in Georgian (default) and English.

## ფუნქციები / Features

- ფოსტა და რამდენიმე ანგარიში / Mail, search, folders, drafts, signatures and multiple accounts
- კალენდარი და შეხვედრები / Calendar, reminders, meeting invitations and shared calendars where Exchange grants access
- კონტაქტები და ამოცანები / Contacts, address book, tasks and follow-up flags
- ფოსტის ექსპორტი და ოფლაინ კითხვა / EML export and offline reading of cached messages
- Exchange-ის კავშირი / EWS and direct MAPI over HTTPS, with an optional Outlook interoperability provider

Direct MAPI support is under active development. Shared mailbox access is read-only in the native provider. Full offline synchronization, attendee-list changes for existing meetings and recurring exceptions are pending. Automatic Replies requires a reachable Exchange Web Services endpoint.

## გაშვება / Build and run

Install the .NET 10 SDK and Microsoft Edge WebView2 Runtime on Windows. Build and start the demo without an Exchange account:

~~~powershell
dotnet build Posta.slnx -c Release
& .\src\Posta.App\bin\Release\net10.0-windows\Posta.App.exe --demo
dotnet test tests/Posta.Mapi.Tests -c Release
~~~

To connect a live mailbox, start Posta without the demo flag and choose **Add Account**. For direct MAPI, choose **MAPI/HTTP without Outlook** and enter your Exchange server and sign-in details. MAPI over HTTPS uses port 443 by default.

Passwords you choose to save are stored in Windows Credential Manager. Account configuration is stored under %AppData%\Posta. The cache is stored under %LocalAppData%\Posta. The repository uses demonstration identities and sanitized test fixtures.

## Windows Setup

Build a self-contained Windows x64 payload:

~~~powershell
dotnet publish src/Posta.App -c Release -r win-x64 --self-contained -o publish
~~~

To build the installer, pass the paths to [NSIS](https://nsis.sourceforge.io/Download) and the Microsoft-signed [WebView2 Evergreen Bootstrapper](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download-section):

~~~powershell
./installer/Build-Setup.ps1 -PublishDirectory ./publish -Makensis C:/tools/NSIS/makensis.exe `
    -WebViewBootstrapper C:/tools/MicrosoftEdgeWebview2Setup.exe
~~~

The default installer is per-user. Add `-AllUsers` to build the Program Files edition, which requires administrator rights.

## ლიცენზია / License

Posta source code is available under the [MIT License](LICENSE). See [Third-Party Notices](THIRD-PARTY-NOTICES.md) for dependency and fixture attribution.
