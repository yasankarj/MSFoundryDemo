# Foundry Health Tips API (.NET)

This project exposes a demo API endpoint for a health-tips agent powered by Azure Foundry / Azure OpenAI.

## Endpoint

- `POST /api/health-tips`
- Request body:

```json
{
  "message": "Give me simple daily health tips"
}
```

- Response body:

```json
{
  "response": "..."
}
```

## Required environment variables

Set these before running:

- `AZURE_OPENAI_ENDPOINT` (example: `https://<your-resource>.openai.azure.com`)
- `AZURE_OPENAI_API_KEY`
- `AZURE_OPENAI_DEPLOYMENT` (your model deployment name in Foundry/Azure OpenAI)
- `AZURE_OPENAI_API_VERSION` (optional, default: `2024-02-15-preview`)

## Run locally

```bash
dotnet run
```

OpenAPI/Swagger metadata is available in Development mode.

## Quick Postman test

- Method: `POST`
- URL: `http://localhost:5105/api/health-tips` (or the port shown by `dotnet run`)
- Headers:
  - `Content-Type: application/json`
- Body (raw JSON):

```json
{
  "message": "I want to improve my sleep. Give me 5 practical tips."
}
```
