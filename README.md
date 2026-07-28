# 🧠 CogniLens

> **AI-Powered Call Intelligence & QA Evaluation Platform**

CogniLens is an call analysis and intelligence platform built on **.NET 10** and **Azure AI Services**. It transforms customer support call recordings and text transcripts into actionable insights through automated speech transcription, LLM-based QA rubric grading, sentiment analysis, action item extraction, and hybrid vector/semantic search.



## 🔍 Overview

Modern contact centers produce thousands of hours of audio daily, but manually reviewing calls for compliance, customer sentiment, and agent performance is slow and expensive. 

**CogniLens** automates the entire ingestion-to-insight pipeline:
1. **Audio Ingestion**: Securely issues presigned upload URLs (User-Delegation SAS) for call audio files (`.wav`, `.mp3`, `.m4a`).
2. **Speech-to-Text**: Converts audio recordings to punctuated transcripts via Azure AI Speech Batch Transcription.
3. **Structured AI Evaluation**: Uses Azure OpenAI (`gpt-5-mini`) to grade calls against customizable QA rubrics (e.g., identity verification, greeting compliance, professional tone, resolution efficacy), extract key action items, and detect customer sentiment.
4. **Vector Embedding & Search**: Chunks call transcripts, generates 1536-dimensional embeddings with `text-embedding-3-small`, and indexes them in Azure AI Search for hybrid (BM25 + HNSW vector) queries with semantic re-ranking.
5. **Interactive Dashboard**: A responsive Blazor web front-end to inspect call details, review detailed rubric scoring, and execute natural-language semantic searches.

---

## ✨ Key Features

- **Automated Audio Ingestion & Transcription**: Seamless integration with Azure Blob Storage and Azure AI Speech Batch Transcription.
- **Structured LLM QA Rubric Grading**: Enforces deterministic JSON outputs from Azure OpenAI to score agent interactions against standard compliance rubrics.
- **Hybrid Vector & Semantic Search**: Combines BM25 keyword matching with HNSW vector similarity and Azure AI Semantic Ranker to retrieve relevant transcript chunks.
- **Asynchronous Event-Driven Worker**: Queue-driven architecture using Azure Storage Queues, capable of KEDA autoscaling to zero in Azure Container Apps.
- **Blazor Web Dashboard**: Single-page interactive application for call tracking, transcript visualization, rubric analysis, and semantic search.

---

## 🛠️ Tech Stack

| Domain | Technology / Library | Description |
| :--- | :--- | :--- |
| **Framework** | .NET 10 / C# 14 | Core runtime & language features |
| **Architecture** | Clean Architecture | Decoupled Core, Infrastructure, Api, Worker, Web layers |
| **Database** | SQL Server 2022 / Azure SQL | Entity Framework Core 9 with Serverless retry resiliency |
| **Web API** | ASP.NET Core Web API / Minimal APIs | OpenAPI / Swagger enabled, ApiKey authentication & rate limiting |
| **Frontend** | Blazor Web Assembly / Server | Interactive C# SPA front-end |
| **Cloud Hosting** | Azure Container Apps (ACA) | Serverless containers with KEDA minReplicas=0 scaling |
| **AI Ingestion** | Azure AI Speech | Asynchronous batch transcription API |
| **AI LLM** | Azure OpenAI (`gpt-5-mini`) | Structured outputs for summary, sentiment, & QA rubric evaluation |
| **AI Embeddings** | Azure OpenAI (`text-embedding-3-small`) | 1536-dimensional vector generation |
| **Vector Search** | Azure AI Search | Hybrid BM25 + HNSW vector index with Semantic Re-ranker |
| **Storage & Messaging**| Azure Blob & Queue Storage | Azurite emulator for local dev, Managed Identity in Azure |
| **IaC & Security** | Azure Bicep & Managed Identity | Zero-secret deployment templates, Trivy security scanning |

---

## 🚀 Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker Engine with Docker Compose)
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (if connecting to live Azure AI resources during local dev)

---

### 1. Start Local Infrastructure (Docker)

CogniLens provides a `docker-compose.yml` file to spin up local emulators for SQL Server and Azure Storage (Azurite):

```bash
docker compose up -d mssql azurite
```

This starts:
- **MSSQL 2022**: Available on `localhost:1433` (Password: `DevPassword1!`)
- **Azurite**: Blob (`10000`), Queue (`10001`), Table (`10002`)

---

### 2. Database Setup

Apply EF Core migrations to initialize the SQL database schema:

```bash
dotnet ef database update --project src/CogniLens.Infrastructure --startup-project src/CogniLens.Api
```

---

### 3. Run the Services

#### Option A: Running via Host (.NET CLI)

For local development against real Azure AI resources (OpenAI/Search/Speech), authentication uses your local Azure CLI identity (`az login`):

```bash
# Login to Azure for DefaultAzureCredential resolution
az login

# Run the API Service (Port 8080)
dotnet run --project src/CogniLens.Api

# In a separate terminal, run the Worker Engine (Port 8081)
dotnet run --project src/CogniLens.Worker

# In a separate terminal, run the Blazor Web App
dotnet run --project src/CogniLens.Web
```

#### Option B: Full Docker Compose Loop

```bash
docker compose up --build
```

---

## 🔌 API Reference

The API is secured via the `X-Api-Key` header (`dev-local-key` in local development). Key endpoints include:

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `POST` | `/api/calls/upload-ticket` | Request a presigned Blob SAS upload URL for an audio file |
| `POST` | `/api/calls` | Submit a text transcript or complete audio upload for analysis |
| `GET` | `/api/calls` | List call recordings with pagination and filtering |
| `GET` | `/api/calls/{id}` | Retrieve detailed call analysis, summary, transcript, and QA rubric scores |
| `POST` | `/api/calls/{id}/analyze` | Trigger re-analysis of an existing call recording |
| `POST` | `/api/search` | Execute hybrid vector/semantic search across call transcript chunks |
| `GET` | `/api/rubrics` | List registered QA compliance evaluation rubrics |


