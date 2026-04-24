# Foundry Health Tips API (.NET)

This project exposes demo endpoints for:

- direct model chat completions
- Azure AI Foundry Agent (threaded conversations with `threadId`)

## Endpoints

### `POST /api/health-tips`

Direct model call via deployment chat completions.

Request:

```json
{
  "message": "Give me simple daily health tips"
}
```

Response:

```json
{
  "response": "..."
}
```

### `POST /api/health-agent-tips`

Calls a pre-created Foundry Agent (`HealthAgentID`) using threads + runs.

Request (new conversation):

```json
{
  "message": "I sit all day for work. Give me 3 practical health tips."
}
```

Request (continue conversation):

```json
{
  "threadId": "<thread-id-from-previous-response>",
  "message": "Now make those tips fit into 5-minute breaks."
}
```

Response:

```json
{
  "type": "ignored",
  "message": "I cannot predict a doctor's opinion or provide a medical diagnosis. It's best to consult your doctor for a professional assessment based on your readings and overall health.",
  "threadId": "thread_...",
  "rawResponse": "```json\n{\n  \"type\": \"ignored\",\n  \"message\": \"...\"\n}\n```"
}
```

Notes:

- `threadId` is required for multi-turn continuity.
- `rawResponse` is included for debugging/auditing original model output.
- API attempts to parse structured JSON from plain JSON or fenced ` ```json ` blocks.

### `GET /api/health-agent-thread/{threadId}`

Debug endpoint to inspect stored messages in a thread.

Useful to verify whether context is present when troubleshooting memory/continuity issues.

## Configuration

### Direct model endpoint config

- `Foundry:Endpoint` (or `AZURE_OPENAI_ENDPOINT`)
- `Foundry:ApiKey` (or `AZURE_OPENAI_API_KEY`)
- `Foundry:Deployment` (or `AZURE_OPENAI_DEPLOYMENT`)
- `Foundry:ApiVersion` (or `AZURE_OPENAI_API_VERSION`, default `2024-02-15-preview`)

### Agent endpoint config

- `Foundry:ProjectEndpoint` (recommended) or `Foundry:Endpoint`
- `Foundry:HealthAgentID`
- `Foundry:AgentApiVersion` (recommended `v1`)
- `Foundry:AgentAuthScope` (default `https://ai.azure.com/.default`)

## Agent authentication behavior

For `/api/health-agent-tips` and `/api/health-agent-thread/{threadId}`, auth token selection order is:

1. Incoming request `Authorization: Bearer ...`
   - if token audience is already `https://ai.azure.com`, pass-through is used
   - otherwise OBO (On-Behalf-Of) is attempted
2. Configured token (`Foundry:AgentAccessToken`, `AZURE_FOUNDRY_AGENT_ACCESS_TOKEN`, `AzureAd:AccessToken`)
3. `DefaultAzureCredential`

### OBO prerequisites

To exchange an app token (for example `api://.../access_as_user`) to Foundry audience token, configure:

- `AzureAd:TenantId`
- `AzureAd:ClientId`
- `AzureAd:ClientSecret`
- `AzureAd:Instance` (optional, default `https://login.microsoftonline.com/`)

## Run locally

```bash
dotnet run
```

### Additional Instructions
Use the following instructions to set as agent instructions
```
You are a friendly agent who assists giving health tips. Make sure to only give health related outputs. Ask user politely if users asks questions unrelated to health.

You should only give health tips related to body temperature, blood pressure, General Well being.

You should not advice the user on fatal or critical conditions such as Cancers, Chronic Kidney Disease, Instead ask the user to seek Medical Help.

When ignoring messages, make sure they are relevant to the overall conversation.

Your response should be formatted as a json with type and message. type can be good, warning, urgent.  Type can be returned as ignored when user ask questions apart from health
```
