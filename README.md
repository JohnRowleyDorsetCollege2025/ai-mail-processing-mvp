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
| `src/Aura.MailIngest.Api` | 5100 | Accepts an `.eml`, parses it with MimeKit, builds the AI payload and POSTs it to the receiver. |
| `src/Aura.AiReceiver.Api` | 5200 | Placeholder for the AI vendor API. Validates and acknowledges (`202`), and keeps the last 100 submissions in memory for inspection. |
| `src/Aura.SampleMailGenerator.Api` | 5300 | Generates broker submission emails with a PDF attachment from seeded, fictitious scenarios. |
| `src/Aura.Contracts` | – | Payload contract shared by ingest and receiver. |
| `tests/Aura.MailIngest.Tests` | – | Parser, API and generator tests. |

## Running

Requires the .NET 10 SDK.

```sh
dotnet test                                                     # 18 tests
dotnet run --project src/Aura.AiReceiver.Api &
dotnet run --project src/Aura.MailIngest.Api &
dotnet run --project src/Aura.SampleMailGenerator.Api &

# Generate an email and push it through the whole chain
curl -X POST localhost:5300/api/samples/email/submit -H 'content-type: application/json' -d '{}'
```

[requests.http](requests.http) has ready-made requests for every endpoint. OpenAPI documents are served at
`/openapi/v1.json` on each API.

## Mail ingest API

| Endpoint | Description |
|---|---|
| `POST /api/submissions` | Parse and forward. Returns `202` with the submission id and the receiver's acknowledgement, `400` for an invalid `.eml`, `502` if the receiver fails. |
| `POST /api/submissions/preview` | Parse only; returns the payload that would be sent. |

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
