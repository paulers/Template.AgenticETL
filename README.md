# Template.AgenticETL

A .NET 10 template for turning PDF documents into classified, structured records. Upload a PDF through an HTTP API, process it in a Redis-backed background queue, and retrieve its classification, extracted fields, and processing status.

The example domain is customer and invoice processing. It classifies documents as `Invoice`, `Receipt`, `Correspondence`, or `Other`, then extracts customer details and an invoice number using OpenAI models through Microsoft Agent Framework.

This is a starting point for a document-processing service. See [Known limitations](#known-limitations) for deployment and processing considerations.

## How it works

1. **Upload.** The API accepts a multipart PDF upload and optional JSON metadata. PdfPig checks that the PDF can be opened without a supplied password and contains at least one page before the API creates a document record and enqueues the PDF as a Base64 payload.
2. **Parse.** PdfPig extracts text from every page locally. Documents with no non-whitespace text fail with an OCR guidance message before any model call. `PageMerger` joins the pages with numbered separators.
3. **Classify.** An AI agent returns a category and confidence score. The application attaches the original parsed text to the classification result for the next stage.
4. **Extract.** A second AI agent receives the classification and document text. It returns extracted fields and a one-sentence description.
5. **Persist and poll.** Workflow events update the Redis record at each stage. Clients poll the status endpoint for progress and results.

The API and five background job instances run in the same application process. The workflow executes in process, while the queue and document records live in Redis database `0`.

The current "load" destination is the Redis document repository. There is no downstream database, warehouse, or export integration.

## Stack

| Component | Implementation |
| --- | --- |
| HTTP API | ASP.NET Core minimal APIs on .NET 10 |
| API versioning | Asp.Versioning.Http, URL version `v1` |
| Agent execution | Microsoft Agent Framework, typed executors and streaming workflow events |
| Model provider | OpenAI, separate classification and extraction models |
| PDF text extraction | PdfPig |
| Queue and repository | Foundatio with Redis, using a shared StackExchange.Redis connection multiplexer |
| Logging | Serilog console and HTTP request logging |

Package versions are pinned in [Template.AgenticETL.csproj](Template.AgenticETL/Template.AgenticETL.csproj).

## Getting started

### Prerequisites

- .NET 10 SDK.
- Redis reachable by the application. The checked-in configuration uses `localhost:6379`, no password, and no TLS.
- An OpenAI API key and access to models suitable for the two JSON-response agent calls.
- A PDF with an embedded text layer. Image-only scans need OCR, which this template does not implement.

Run these commands from the solution root.

### Configure OpenAI

The project already has a `UserSecretsId`. Store the API key outside the repository:

```shell
dotnet user-secrets set "OpenAI:ApiKey" "YOUR_OPENAI_API_KEY" --project Template.AgenticETL
```

Only the API key needs to be secret. Set the model IDs in [appsettings.json](Template.AgenticETL/appsettings.json), which includes these defaults from `OpenAIOptions`:

```json
"OpenAI": {
  "ApiKey": "",
  "ClassificationModel": "gpt-5.6-terra",
  "ExtractionModel": "gpt-6-luna"
}
```

Leave `ApiKey` empty in the checked-in file. Change the model IDs there if you need different models available to your account.

The `http` launch profile uses the `Development` environment, which loads local user secrets. For deployed environments, supply configuration through your host's secret management or environment variables, such as `OpenAI__ApiKey`. ASP.NET Core maps `__` to `:` for nested configuration keys. See Microsoft's [configuration documentation](https://github.com/dotnet/aspnetcore.docs/blob/main/aspnetcore/fundamentals/configuration/index.md) and [user secrets documentation](https://github.com/dotnet/aspnetcore.docs/blob/main/aspnetcore/security/app-secrets.md).

### Build and run

With Redis running and the configuration set:

```shell
dotnet restore Template.AgenticETL.slnx
dotnet build Template.AgenticETL.slnx --no-restore
dotnet run --project Template.AgenticETL --launch-profile http
```

The launch profile listens on `http://localhost:5174`.

```shell
curl http://localhost:5174/health
```

`/health` is a basic application health endpoint. No Redis or OpenAI dependency checks are registered.

## API

All document routes use the `/api/v1/documents` prefix.

| Method | Route | Behavior |
| --- | --- | --- |
| `POST` | `/api/v1/documents/upload` | Accept a PDF and queue it for processing. Returns `202 Accepted` with `recordId`, `statusUrl`, and a `Location` header. |
| `GET` | `/api/v1/documents/{recordId}/status` | Return the full document record, including available results, or `404` if it is missing. |
| `DELETE` | `/api/v1/documents/{recordId}` | Remove the stored record and its filename index entry. Returns `204`, including when the record does not exist. |
| `GET` | `/health` | Basic application health. |

### Upload a document

```shell
curl -X POST "http://localhost:5174/api/v1/documents/upload" -F "file=@invoice.pdf;type=application/pdf"
```

Example acceptance response:

```json
{
  "recordId": "6d88b790-43f1-49e7-bc24-f22a7fbe5869",
  "statusUrl": "/api/v1/documents/6d88b790-43f1-49e7-bc24-f22a7fbe5869/status"
}
```

The optional `metadata` form field accepts a JSON object with `fileName`, `type`, and `uploadedAt`. The server replaces `fileName` with the uploaded filename without its extension and sets `uploadedAt` to the current UTC time. A supplied `type` only seeds the initial record; the worker clears it at the start of processing and still runs classification.

Duplicate detection uses the filename without its extension. An existing filename returns `409 Conflict`. Add `?reprocess=true` to bypass that check and create a new record and work item. This does not update an existing record.

Missing or empty files, unsupported file types, malformed metadata, unreadable PDFs, PDFs requiring a password, and PDFs with no pages return `400`. PDF validation happens before record creation and enqueueing. Record creation and enqueue failures return `500`.

Opening validation does not read every page. Page-level parsing failures are reported during background processing. If the entire document has no extractable non-whitespace text, processing fails with an error explaining that scanned or image-only documents require OCR. A document with both blank and text-bearing pages can proceed. These processing errors appear in the document's status record, and the existing queue retry policy still applies.

Successful enqueueing preserves the document record for the worker and status endpoint. If enqueueing throws, the API removes the pending record and its filename index entry before returning the error response.

### Read progress and results

```shell
curl "http://localhost:5174/api/v1/documents/RECORD_ID/status"
```

The normal status sequence is:

```text
Created -> Processing -> Parsed -> Classified -> Extracted -> Completed
```

A failed attempt can set `Errored`. A later retry starts again at `Processing`, clears prior stage timestamps and model output, and preserves upload time.

The response includes:

- Record ID, document name, and current status as a string.
- UTC timestamps for creation, parsing, classification, extraction, completion, and errors.
- Classification and model-reported confidence.
- A generated description.
- An `extractedData` dictionary containing `customerName`, `customerNumber`, and `invoiceNumber` when returned by the model, plus the preserved `UploadedAt` value.
- An error message when an attempt fails.

The extraction prompt asks for an eight-digit customer number and an invoice number consisting of a letter followed by six digits. It asks the model to return `null` for missing values. These are prompt instructions; the application does not independently validate those business rules.

### Remove a record

```shell
curl -X DELETE "http://localhost:5174/api/v1/documents/RECORD_ID"
```

Deleting a record does not cancel queued or running work.

## Architecture and design choices

### Separate HTTP handling from model execution

Uploads enqueue work and return an acceptance response. They do not wait for model calls. The `DocumentWorkItem` carries the document payload and record ID, so background workers do not depend on the original HTTP request or a local file remaining available.

### Use typed workflow stages

`DocumentParserExecutor` converts Base64 PDF content into page text. `ClassifierExecutor` converts page text into a `ClassificationResult`. `DataExtractorExecutor` converts that result into a `DataExtractionResult`. The explicit types define what each stage passes to the next.

The classifier asks the model only for `ClassificationResponse`, containing category and confidence. Application code carries the document text forward separately. This avoids asking the model to reproduce the entire document in its response.

Classification and extraction have separate model settings so each task can use a different model. Both agents request JSON responses and deserialize them into typed results.

### Save progress from workflow events

`DocumentProcessingWorkflow` listens for executor completion events and writes stage results through `IDocumentRepository`. Executor and workflow errors propagate to the queue job, which records the failure. A new workflow and agent sessions are created for each processing attempt.

### Renew the queue lease during long operations

The queue job runs a timer alongside processing to renew its queue lease, including while a model call is pending. Renewal calls run sequentially. If renewal fails, the job requests cooperative processing cancellation and reports the attempt as failed.

The default lease timeout is ten minutes, with renewal every minute. The timeout is a queue lease duration, not a hard limit on total processing time.

### Keep retry results consistent

Each attempt clears stale classification, extracted fields, descriptions, errors, and stage timestamps before processing. Extraction replaces prior model output rather than merging it. A successful retry clears the previous error and its tracking-list entry.

### Keep infrastructure small

Redis holds both the queue and document records. `IDocumentRepository` isolates record access from the workflow and endpoints. The current implementation uses Foundatio's cache abstraction without an explicit record TTL. Retention and durability therefore need deliberate Redis configuration, including persistence, memory capacity, and eviction policy.

## Configuration

Settings come from the standard ASP.NET Core configuration providers. Checked-in defaults are in [appsettings.json](Template.AgenticETL/appsettings.json), with matching model defaults in [OpenAIOptions.cs](Template.AgenticETL/Models/ConfigBindings/OpenAIOptions.cs).

| Key | Default | Purpose |
| --- | --- | --- |
| `Redis:Host` | `localhost` | Redis hostname. |
| `Redis:Port` | `6379` | Redis port. |
| `Redis:Password` | Empty | Redis password. |
| `Redis:Ssl` | `false` | Enable TLS for the connection. |
| `OpenAI:ApiKey` | Empty | OpenAI API key; supply through secrets or environment configuration. |
| `OpenAI:ClassificationModel` | `gpt-5.6-terra` | Model used by the classifier. |
| `OpenAI:ExtractionModel` | `gpt-6-luna` | Model used by the extractor. |
| `DocumentProcessing:WorkItemTimeout` | `00:10:00` | Queue lease duration. Must be positive. |
| `DocumentProcessing:LockRenewalInterval` | `00:01:00` | Lease renewal interval. Must be positive, supported by `PeriodicTimer`, and shorter than the lease. |

The following settings currently live in [ConfigureServices.cs](Template.AgenticETL/ConfigureServices.cs):

- Queue name `document-work-items`, Redis database `0` for queue and cache.
- Five background job instances per application process.
- Five retries, with a configured retry delay of 30 seconds, before dead-letter handling by Foundatio.
- Multipart body limit of 100 MiB and form value/header limits of 512 KiB. Server and proxy request limits can impose lower effective upload limits.

## Project layout

```text
Template.AgenticETL.slnx
Template.AgenticETL/
  Program.cs                       Application entry point
  ConfigureServices.cs             Dependency injection, Redis, queue, jobs, options
  ConfigureApp.cs                  Middleware and route registration
  Endpoints/                       Upload, status, and delete routes
  Models/                          Stored records and queued work items
    AgentResults/                  Typed model responses and workflow messages
    ConfigBindings/                OpenAI, Redis, and processing options
  Services/
    DocumentProcessingQueueJob.cs  Queue consumer and lease renewal
    DocumentProcessingWorkflow.cs  Workflow composition and event persistence
    IDocumentRepository.cs        Repository contract and Redis implementation
    PageMerger.cs                  Page text formatting
  Workflow/                        PDF parsing, classification, and extraction executors
```

## Adapting the template

- Change categories in [ClassifierExecutor.cs](Template.AgenticETL/Workflow/ClassifierExecutor.cs).
- Change extraction instructions in [DataExtractorExecutor.cs](Template.AgenticETL/Workflow/DataExtractorExecutor.cs) and response types in `Models/AgentResults/`.
- Add or reorder stages in [DocumentProcessingWorkflow.cs](Template.AgenticETL/Services/DocumentProcessingWorkflow.cs).
- Implement `IDocumentRepository` for another storage destination, or add a workflow stage that loads results into your target system.
- Replace filename-based duplicate detection with a document ID or content hash if filenames are not unique in your domain.
- For larger documents, store PDFs in object storage and queue a reference instead of the full Base64 payload.

## Known limitations

- The API has no authentication or authorization. Upload antiforgery checks are disabled, and HTTPS redirection is commented out. Add access controls and transport security before exposing it publicly. In a production environment, some kind of authentication will need to be present, but HTTPS is not necessarily required, especially if the job is sitting in Kubernetes behind a reverse proxy. YMMV.
- Upload validation accepts either a `.pdf` extension or an `application/pdf` content type, then uses PdfPig to check opening and page count. This is a readability check, not formal PDF compliance validation. Individual pages can still fail during background extraction.
- The PDF is buffered in memory and stored as Base64 in the queue. This increases memory and Redis usage. There is no separate original-document archive. In production, you'd likely use a real queue (like Azure Service Bus) and the documents would be stored in durable storage when completed.
- There is no OCR. In production, you'd likely want to use something like Azure Document Intelligence or another more robust PDF parsing product.
- There is no document chunking, or token-budget management. All parsed text goes to both agents, so long documents can exceed model context limits or incur substantial token usage. Parsed document text is sent to OpenAI. In a production scenario, classification would extract each page type, then only send the pages that matter for data extraction.
- Duplicate checks and record creation are separate operations. Concurrent uploads can pass the same filename check. There is no transactional outbox covering record creation and queue publication. In production, there'd likely be a webhook call or another notification system letting dependent systems known the PDF has finished parsing.
- Workflow execution has no persisted stage checkpoints. A queue retry repeats parsing and both model calls. An `Errored` status describes an attempt and does not establish that retries are exhausted; no API exposes retry counts or dead-letter state. This is slated for a future improvement. Checkpointing is an important feature!
- Deleting a record leaves any queued payload in place. Repository deletion also does not remove an existing entry from the errored-record tracking list. This could definitely be an improvement.
- The solution currently has no automated test project.

## License

This project is licensed under the [MIT License](LICENSE).
