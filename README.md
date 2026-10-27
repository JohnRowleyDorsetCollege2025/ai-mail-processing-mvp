# AuraMailToAIApi

Turns broker submission emails (`.eml`) for commercial insurance into the JSON payload expected by the third-party AI
processing API (see [data/sample_ai_payload.json](data/sample_ai_payload.json)), and provides tooling to generate realistic
test submissions.

```
 SampleMailGenerator (5300) ──.eml──▶ MailIngest (5100) ──JSON payload──▶ AiReceiver (5200)
   test data only                       parse + forward                    placeholder for AI vendor
```

| Project | Port | Purpose |
|---|---|---|
| `src/Aura.MailIngest.Api` | 5100 | Accepts an `.eml`, parses it with MimeKit, builds the AI payload and POSTs it to the receiver. Saves each email to a local SQLite database for review (dev/demo only). |
| `src/Aura.AiReceiver.Api` | 5200 | Placeholder for the AI vendor API. Validates, acknowledges (`202`) with a stand-in AI CTP model, and keeps the last 100 submissions in memory for inspection. |
| `src/Aura.SampleMailGenerator.Api` | 5300 | Generates broker submission emails with a PDF attachment from seeded, fictitious scenarios. |
| `src/Aura.UI` (`EnterpriseSearch.csproj`) | 5400 | Blazor Server UI (Enterprise Search: cases, exceptions, reconciliation). Currently uses in-memory sample data. |
| `src/Aura.Inbox.UI` (`InboxMock.csproj`) | 5165 | Standalone Blazor WebAssembly inbox mock-up, served by the WebAssembly dev server. |
| `src/Aura.WorkerService` | – | Background worker running periodic tasks (see below). |
| `src/Aura.AppHost` | – | .NET Aspire AppHost: starts all three APIs and the Aspire dashboard. |
| `src/Aura.ServiceDefaults` | – | Shared Aspire defaults: OpenTelemetry (logs, traces, metrics), health checks and HTTP resilience. |
| `src/Aura.Contracts` | – | Contracts shared by ingest and receiver: the AI payload, the acknowledgement and the AI CTP model. |
| `tests/Aura.MailIngest.Tests` | – | Parser, API and generator tests. |

## Running

Requires the .NET 10 SDK and a trusted HTTPS development certificate (`dotnet dev-certs https --trust`).

### With Aspire (recommended)

```sh
dotnet run --project src/Aura.AppHost
```

Or set `Aura.AppHost` as the startup project in Visual Studio or Rider, or launch it from VS Code's C# Dev Kit. The
console prints a dashboard link with a login token, e.g. `https://localhost:17267/login?t=...`.

The AppHost starts both UIs and the AI receiver, then the mail ingest API, then the sample generator, then the worker, each one waiting for the
previous to report healthy. It passes each API the URL of the next, so the generator sends to the ingest API and
the ingest API to the receiver. The dashboard shows each API's state, console logs, structured logs, distributed traces
(follow one sample from the generator through ingest to the receiver) and metrics, and links to each API's OpenAPI document.

The UIs are at http://localhost:5400 (Enterprise Search) and http://localhost:5165 (Inbox). The APIs stay on ports 5100, 5200 and 5300, so [requests.http](requests.http) and the `curl` examples work unchanged.
Stop everything with Ctrl+C.

```sh
# Generate an email and push it through the whole chain
curl -X POST localhost:5300/api/samples/email/submit -H 'content-type: application/json' -d '{}'
```

### Without Aspire

Each API also runs on its own, using the URLs in its `appsettings.json`:

```sh
dotnet run --project src/Aura.AiReceiver.Api &
dotnet run --project src/Aura.MailIngest.Api &
dotnet run --project src/Aura.SampleMailGenerator.Api &
```

### Tests

```sh
dotnet test                                                     # 27 tests
```

### Notes

- Health endpoints (`/health`, `/alive`) come from `Aura.ServiceDefaults` and are mapped in Development only.
  The Inbox UI is a WebAssembly app that runs in the browser, so it has no service defaults: the dashboard shows its
  console output but no traces, metrics or health checks.
- Stop the AppHost with Ctrl+C in its terminal (or the stop button in your IDE). Killing only the outer
  `dotnet run` process leaves the APIs running in the background on their ports.
- Outgoing HTTP calls use Aspire's standard resilience handler with retries **disabled for POSTs**: a replayed
  submission could create a duplicate at the AI vendor. Timeouts allow up to 100 seconds for large attachments.
- The `http` launch profile of the AppHost sets `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`, for use when no
  HTTPS certificate is available.

[requests.http](requests.http) has ready-made requests for every endpoint. OpenAPI documents are served at
`/openapi/v1.json` on each API.

## Worker service

`Aura.WorkerService` hosts background tasks. Each one derives from `PeriodicTask<TOptions>`, which runs the task, waits
`Interval`, and repeats. A failed run is logged and the next run still happens. Settings are re-read before every run,
so edits to `Enabled` or `Interval` in `appsettings.json` apply without a restart.

| Task | Config section | What it does |
|---|---|---|
| `SampleSubmissionTask` | `Tasks:SampleSubmission` | POSTs to the generator's `/api/samples/email/submit` every `Interval` (default `00:00:30`), producing a steady stream of random submissions through ingest to the AI receiver. |

```json
"Tasks": {
  "SampleSubmission": { "Enabled": true, "Interval": "00:00:30", "BaseUrl": "http://localhost:5300", "Mailbox": null }
}
```

Under Aspire, `BaseUrl` is set to the generator's endpoint. Set `Enabled` to `false` to pause the traffic.

To add a task: create a class deriving from `PeriodicTask<TOptions>` with its options deriving from
`PeriodicTaskOptions`, bind the options to a new `Tasks:<Name>` section, and register it with `AddHostedService` in
`Program.cs`.

## Mail ingest API

| Endpoint | Description |
|---|---|
| `POST /api/submissions` | Parse, store and forward. Returns `202` with the submission id and the receiver's acknowledgement, `400` for an invalid `.eml`, `502` if the receiver fails. |
| `POST /api/submissions/preview` | Parse only; returns the payload that would be sent. Nothing is stored. |
| `GET /api/submissions?status=&take=` | List stored submissions, most recent first. `status` is `Pending`, `Accepted` or `Failed`. |
| `GET /api/submissions/{id}` | One stored submission, including the `content` text sent to the AI. |
| `GET /api/submissions/{id}/eml` | Download the original `.eml` exactly as received. |

The `.eml` can be sent either as `multipart/form-data` (field `file`, optional field `mailbox`) or as a raw
`message/rfc822` body. `?mailbox=` overrides the default mailbox (`Ingest:DefaultMailbox`).

How the payload is built:

- **`content`**: a `SUBMISSION` header block (id, mailbox, from, to, cc, date, subject, attachment names) followed by
  `EMAIL BODY`. Uses the plain-text body, falling back to text extracted from the HTML body.
- **`context.submissionId`**: a new GUID per submission. **`receivedOn`**: the email's `Date` header in UTC
  (or the time of receipt if the header is missing).
- **`attached_documents`**: every attachment, base64-encoded. It also includes named non-image parts sent inline
  (some clients do this with PDFs) and attached emails (as `message/rfc822`). Inline images such as signature logos
  are skipped. `application/octet-stream` parts get their MIME type from the file extension.

### Stored emails (development and demo only)

Every email posted to `POST /api/submissions` is saved to a SQLite database before it's forwarded. Each row records
the sender, subject, attachment names, the `content` text, the raw `.eml`, and the forwarding outcome: `Accepted`,
or `Failed` with the error. If the receiver is down, the email is still kept for review.

When the receiver accepts a submission, its acknowledgement is stored as JSON in the nullable `AIResponse` column
and returned as `aiResponse` by `GET /api/submissions/{id}`. The acknowledgement includes the **AI CTP model**
(`ctp`): the facts the AI extracts for our CTP APIs. The fields are provisional and will change:

```json
"aiResponse": {
  "submissionId": "…", "status": "accepted", "receivedAt": "…",
  "ctp": { "brokerName": "Corrib Risk Partners", "brokerId": "BRK-3F9A1C", "occupationCode": "55.10",
           "occupationDescription": "Hotels and similar accommodation", "appetite": "Refer" }
}
```

Until the vendor is connected, the receiver fills `ctp` using `PlaceholderCtpExtractor`: naive keyword matching
for the occupation and appetite, the sender's domain for a fake broker id, and the email signature for the
broker's firm.

The database is `src/Aura.MailIngest.Api/App_Data/submissions.db` (`Storage:DatabasePath`). It's created on startup,
has no migrations, and is ignored by git. New nullable columns (such as `AIResponse`) are added to an existing
database on startup. Delete it to start afresh, or after a more drastic schema change. This store is not
intended for production.

Configuration (`appsettings.json`): `Ingest:AgentIds` (sent as `agent_ids`), `Ingest:DefaultMailbox`,
`Ingest:MaxEmlSizeBytes` (default 50 MB), `AiReceiver:BaseUrl`, `AiReceiver:SubmissionsPath` and `AiReceiver:TimeoutSeconds`.
Point `AiReceiver:BaseUrl` at the vendor once its API is available. Authentication headers will need adding in
`AiReceiverClient`.

## Sample mail generator

| Endpoint | Description |
|---|---|
| `GET /api/samples/email?seed=` | Download a random (or seeded) `.eml`. |
| `POST /api/samples/email` | Same, with overrides (see below). |
| `POST /api/samples/scenario` | The scenario's facts as JSON, with no email. |
| `POST /api/samples/email/submit` | Generate and POST to the ingest API; returns the ingest response plus the scenario. |
| `GET /api/samples/options` | Valid trades, request types and attachment types. |

Request body (all optional):

```json
{
  "scenario": { "seed": 99, "requestType": "Renewal", "trade": "Hotel", "insuredName": "Test Co Ltd", "brokerFirm": "Acme Brokers", "claimCount": 2 },
  "attachments": ["submission-pdf"],
  "mailbox": "traded"
}
```

Each email is rendered from a `SubmissionScenario`: broker, insured, trade, turnover/wageroll, covers with limits
or sums insured, and claims history. The scenario is the **ground truth** for that email. The submit endpoint returns
it, and the same seed regenerates it via `/scenario` (dates are relative to today). Later, it can be compared with the
quotation structure the AI extracts. The seed is also written to the `X-Aura-Sample-Seed` header of each email.

All names, companies and domains are fictitious (`.example` domains), so generated mail can't be mistaken for a real
broker's submission.

### Extending

- **More attachment types** (Word proposal form, claims spreadsheet, scanned PDF): implement `IAttachmentGenerator`
  with a new `Key`, register it in `Program.cs`, and request it via `"attachments": [...]`. For Word,
  `DocumentFormat.OpenXml` (MIT) is the natural choice.
- **More variety**: add trades, broker firms and claim types to the catalogues in `ScenarioGenerator`, and phrasing
  variants to `SampleEmailBuilder.BuildBody`.
- **PDFs**: `SimplePdfDocument` is a small dependency-free writer that uses the standard Helvetica fonts, so it
  behaves the same on Windows, macOS and Linux containers without font installation. If you need richer layouts
  (logos, multi-column forms), add a generator built on a full library, and check its licence: QuestPDF needs a paid
  licence for companies over $1M revenue.
