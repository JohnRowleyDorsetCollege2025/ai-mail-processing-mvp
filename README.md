# AuraMailToAIApi

Turns broker submission emails (`.eml`) for commercial insurance into the JSON payload expected by the third-party AI
processing API (see [data/sample_ai_payload.json](data/sample_ai_payload.json)), and provides tooling to generate realistic
test submissions.

```
 SampleMailGenerator (5300) ──.eml──▶ MailIngest (5100) ··JSON payload··▶ AiReceiver (5200)
   test data only                       parse + store as Pending           placeholder for AI vendor
                                              ▲
                     WorkerService ── POST /api/submissions/forward every 2 min:
                                      sends up to 5 pending, one at a time, waiting for each AI response
```

| Project | Port | Purpose |
|---|---|---|
| `src/Aura.MailIngest.Api` | 5100 | Accepts an `.eml`, parses it with MimeKit and stores it as Pending in a local SQLite database (dev/demo only). Sends pending emails to the AI receiver when `POST /api/submissions/forward` is called. |
| `src/Aura.AiReceiver.Api` | 5200 | Placeholder for the AI vendor API. Validates, acknowledges (`202`) with a stand-in AI CTP model, and keeps the last 100 submissions in memory for inspection. |
| `src/Aura.SampleMailGenerator.Api` | 5300 | Generates broker submission emails with a PDF attachment from seeded, fictitious scenarios. |
| `src/Aura.UI` (`EnterpriseSearch.csproj`) | 5400 | Blazor Server UI (Enterprise Search: cases, exceptions, reconciliation). Currently uses in-memory sample data. |
| `src/Aura.Inbox.UI` (`InboxMock.csproj`) | 5165 | Blazor WebAssembly inbox showing the emails received by the mail ingest API, updated live over SignalR. |
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
# Generate an email and store it in ingest (the worker sends it to the AI within 2 minutes)
curl -X POST localhost:5300/api/samples/email/submit -H 'content-type: application/json' -d '{}'

# Or send pending emails to the AI now, instead of waiting for the worker
curl -X POST 'localhost:5100/api/submissions/forward?max=5'
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
dotnet test                                                     # 35 tests
```

### Notes

- Health endpoints (`/health`, `/alive`) come from `Aura.ServiceDefaults` and are mapped in Development only.
  The Inbox UI is a WebAssembly app that runs in the browser, so it has no service defaults: the dashboard shows its
  dev server's console output but no traces, metrics or health checks. Its API calls appear in the ingest API's traces.
- Stop the AppHost with Ctrl+C in its terminal (or the stop button in your IDE). Killing only the outer
  `dotnet run` process leaves the APIs running in the background on their ports.
- The worker's call to `/api/submissions/forward` is the exception: it has no resilience handler (a run can take
  minutes) and is limited only by `Tasks:ForwardPending:Timeout`.
- Outgoing HTTP calls use Aspire's standard resilience handler with retries **disabled for POSTs**: a replayed
  submission could create a duplicate at the AI vendor. Timeouts allow up to 100 seconds for large attachments.
- The `http` launch profile of the AppHost sets `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`, for use when no
  HTTPS certificate is available.

[requests.http](requests.http) has ready-made requests for every endpoint. OpenAPI documents are served at
`/openapi/v1.json` on each API.

## Inbox UI

`Aura.Inbox.UI` is a Blazor WebAssembly app that runs in the browser. It loads `GET /api/inbox` from the mail ingest
API, then listens on the SignalR hub `/hubs/inbox`. New emails appear at the top as they arrive, and their status
badge changes from Pending to Processed or Failed when the worker's next forwarding run sends it to the AI. The reader pane shows the AI CTP model
(customer, broker, occupation, inception, current insurer, covers, appetite) once the AI has responded. A Live / Reconnecting / Offline indicator sits at the
bottom of the folder list. The hub reconnects indefinitely and reloads the list after reconnecting, so nothing is
missed.

Configuration:

- The UI reads the ingest URL from `src/Aura.Inbox.UI/wwwroot/appsettings.json` (`MailIngest:BaseUrl`, default
  `http://localhost:5100`). Browser apps can't receive Aspire's environment variables, so this works because the
  ingest API's port is fixed.
- The UI runs on its own origin, so the ingest API must allow it via CORS: `Inbox:AllowedOrigins` in the ingest
  API's `appsettings.json` (default `http://localhost:5165`). Under Aspire this is set from the Inbox UI's endpoint.

## Worker service

`Aura.WorkerService` hosts background tasks. Each one derives from `PeriodicTask<TOptions>`, which runs the task, waits
`Interval`, and repeats. A failed run is logged and the next run still happens. Settings are re-read before every run,
so edits to `Enabled` or `Interval` in `appsettings.json` apply without a restart.

| Task | Config section | What it does |
|---|---|---|
| `SampleSubmissionTask` | `Tasks:SampleSubmission` | POSTs to the generator's `/api/samples/email/submit` every `Interval` (default `00:00:30`), producing a steady stream of random submissions into ingest. |
| `ForwardPendingTask` | `Tasks:ForwardPending` | POSTs to ingest's `/api/submissions/forward?max=MaxPerRun` every `Interval` (default `00:02:00`, `MaxPerRun` 5), so pending emails reach the AI at a controlled rate. |

```json
"Tasks": {
  "SampleSubmission": { "Enabled": true, "Interval": "00:00:30", "BaseUrl": "http://localhost:5300", "Mailbox": null },
  "ForwardPending": { "Enabled": true, "Interval": "00:02:00", "BaseUrl": "http://localhost:5100", "MaxPerRun": 5, "Timeout": "00:15:00" }
}
```

Under Aspire, each `BaseUrl` is set to the right service's endpoint. Set `Enabled` to `false` to pause a task.
`Timeout` is how long one forwarding run may take (it waits for up to `MaxPerRun` AI responses in turn).

With the defaults, about 4 emails arrive every 2 minutes and up to 5 are sent to the AI in the same time, so the
queue keeps up. If the AI is slow or down, emails wait as Pending and are sent on later runs.

To add a task: create a class deriving from `PeriodicTask<TOptions>` with its options deriving from
`PeriodicTaskOptions`, bind the options to a new `Tasks:<Name>` section, and register it with `AddHostedService` in
`Program.cs`.

## Mail ingest API

| Endpoint | Description |
|---|---|
| `POST /api/submissions` | Parse and store as `Pending`. Nothing is sent to the AI yet. Returns `202` with the submission id, or `400` for an invalid `.eml`. |
| `POST /api/submissions/forward?max=` | Send up to `max` pending submissions (default 5, capped by `Forwarding:MaxBatchSize`, default 20) to the AI receiver, oldest first, **one at a time, waiting for each AI response**. Returns counts and a result per submission. Returns `409` if a run is already in progress. |
| `POST /api/submissions/preview` | Parse only; returns the payload that would be sent. Nothing is stored. |
| `GET /api/submissions?status=&take=` | List stored submissions, most recent first. `status` is `Pending`, `Accepted` or `Failed`. |
| `GET /api/submissions/{id}` | One stored submission, including the `content` text sent to the AI. |
| `GET /api/submissions/{id}/eml` | Download the original `.eml` exactly as received. |
| `GET /api/inbox?take=` | The most recent submissions (default 100) as inbox rows (`InboxMessage`), for the Inbox UI. |
| `/hubs/inbox` (SignalR) | Pushes `MessageUpserted` with an `InboxMessage` when a submission is stored (`Pending`) and again when it's `Processed` or `Failed`. |

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

Every email posted to `POST /api/submissions` is saved to a SQLite database as `Pending`. Each row records
the sender, subject, attachment names, the `content` text, the raw `.eml`, and the forwarding outcome.

### Sending to the AI

`POST /api/submissions/forward` (called by the worker every 2 minutes) rebuilds each pending submission's payload
from its stored `.eml`, keeping the same submission id, and sends it to the AI receiver:

- **Throttled:** at most `max` per run, and only one run at a time. A second caller gets `409`, so nothing is sent twice.
- **One at a time:** each AI response is awaited and stored before the next submission is sent.
- **Accepted:** status `Accepted` (Processed in the Inbox UI), with the acknowledgement stored in `AIResponse`.
- **Rejected (4xx, except 408/429):** status `Failed` with the error. It won't be retried. The run continues.
- **Temporary failure** (AI unreachable, 5xx, timeout, 408/429): the submission **stays `Pending`** with the error
  recorded, and the **run stops**, because the rest would likely fail too. It's retried on the next run.
- If the caller disconnects mid-run, the submission in flight still completes and is recorded. No new one is started.

When the receiver accepts a submission, its acknowledgement is stored as JSON in the nullable `AIResponse` column
and returned as `aiResponse` by `GET /api/submissions/{id}`. The acknowledgement's `ctp` field is the **AI CTP
model** (`AiCtpModel`): the quotation request for our CTP APIs. It mirrors
[data/sample_ctp_payload.json](data/sample_ctp_payload.json) field for field, including its JSON names (`cytoraID`,
`agentkey`, `underWriter`). A test round-trips the sample file through the model to keep them in step.

```json
"aiResponse": {
  "submissionId": "…", "status": "accepted", "receivedAt": "…",
  "ctp": { "channel": "CSR", "product": "BMR", "user": "Aura", "customerName": "Ardmore Ltd T/As The Grand Hotel Naas",
           "agencyCode": "DN4821937", "occupationCode": "55.10", "covers": [], "coverDate": "2026-11-20",
           "brokerName": "Padraig Quinn", "brokerEmail": "padraig.quinn@leevalley-ins.example",
           "holdingInsurerName": "Insurer A", "appetite": "A = Not Declined", … }
}
```

Until the vendor is connected, the receiver fills `ctp` using `PlaceholderCtpExtractor`, which reads only the email
text:
- **Fixed values from the sample:** `channel`, `product` and `user`.
- **From the email:** `customerName`, `brokerName` and `brokerEmail` (the sender), inception and cover dates, and the
  current insurer (`UN - Unknown` for new business).
- **Stand-ins:**
  - `agencyCode` and `agentkey`: a fake code in the sample's `DN` + 7 digits shape, derived from the sender's domain.
  - `occupationCode`: a NACE code. CTP's real codes look like `BB06`.
  - `appetite`: `A = Not Declined` when the trade is recognised.
- **Left empty:** `covers` (they're in the attached proposal, which it doesn't read). All other fields are null.

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
