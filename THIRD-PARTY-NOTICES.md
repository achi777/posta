# MAPI development references and test data

`src/Posta.Mapi` is a C# MAPI/HTTP transport and experimental mailbox provider. Its wire
layouts follow Microsoft Open Specifications MS-OXCMAPIHTTP, MS-OXCROPS,
MS-OXCSTOR, MS-OXCFOLD, MS-OXCTABL and MS-OXCDATA.

The implementation was cross-checked against the open-source `mapi-client-rs`
project. The sanitized Exchange SE request/response bodies in `tests/Posta.Mapi.Tests/Fixtures` are copied from that project:

- Source: https://github.com/allodia-eu/mapi-client-rs
- Revision: `7a7cf7ebe415bfd612faef6bcfb1d78e701483fa`
- Original paths: `fixtures/exchange-se/session-en-us`, `fixtures/exchange-se/items-en-us`, `fixtures/exchange-se/writes-en-us`
- License elected: MIT; full notice is in `third-party/mapi-client-rs/LICENSE-MIT.txt`.
- Copyright (c) 2026 Dennis Ameling - Allodia and the mapi-client-rs contributors.

The repository includes replay bodies from the upstream lab fixtures. HTTP capture metadata is excluded. Passing these replay tests demonstrates compatibility
with the selected recorded exchanges, not with every Exchange build or with
the user's server. The Rust runtime and Outlook are not required by Posta.Mapi.

Posta uses [MimeKit.Core](https://github.com/jstedfast/MimeKit) for EML export.
Version 5.0.0 is distributed under the MIT license. The license is included in
`third-party/mimekit/LICENSE-MIT.txt` and in the Windows package.
