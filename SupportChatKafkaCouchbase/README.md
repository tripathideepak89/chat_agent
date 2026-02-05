# Support Chat Kafka Couchbase

A distributed support chat routing system built with .NET 8, Kafka, and Couchbase.

## Project Structure

```
SupportChatKafkaCouchbase/
├── Domain/              # Domain models and enums
│   ├── Enums.cs        # Seniority and ChatSessionStatus enums
│   └── Models.cs       # ChatSession and AgentState models
├── Infra/              # Infrastructure components
│   ├── CouchbaseContext.cs   # Couchbase connection management
│   ├── DistributedLock.cs    # Distributed locking implementation
│   ├── Kafka.cs              # Kafka producer/consumer
│   ├── IdGenerator.cs        # Document ID generation
│   └── Time.cs               # Clock abstraction
├── Repos/              # Repository layer
│   ├── AgentRepository.cs    # Agent data access
│   └── SessionRepository.cs  # Session data access
├── Services/           # Background workers
│   ├── AssignmentWorker.cs   # Chat assignment logic
│   └── InactivityWorker.cs   # Inactive session detection
├── Api/                # API DTOs
│   └── Dtos.cs         # Request/Response models
├── k8s/                # Kubernetes deployment files
│   ├── configmap.yaml
│   ├── secret.yaml
│   ├── deployment.yaml
│   ├── service.yaml
│   └── hpa.yaml
├── Program.cs          # Application entry point
├── appsettings.json    # Configuration
├── Dockerfile          # Container definition
└── docker-compose.yml  # Local development setup
```

## Prerequisites

- .NET 8.0 SDK
- Docker and Docker Compose (for local development)
- Kafka
- Couchbase Server

## Building the Project

```powershell
dotnet restore
dotnet build
```

## Running Locally

### Start Infrastructure Services

```powershell
docker-compose up -d
```

### Configure Couchbase

1. Open http://localhost:8091
2. Setup cluster with username: Administrator, password: password
3. Create bucket named "support"
4. Create primary index:
   ```sql
   CREATE PRIMARY INDEX ON `support`._default._default;
   ```

### Run the Application

```powershell
dotnet run
```

The API will be available at http://localhost:5000 with Swagger UI at http://localhost:5000/swagger

## API Endpoints

### Create Chat Session
```
POST /api/chat/sessions
{
  "customerReference": "customer123"
}
```

### Get Session Status
```
GET /api/chat/sessions/{id}
```

### Poll for Assignment
```
GET /api/chat/sessions/{id}/poll
```

## Configuration

Key settings in `appsettings.json`:

- **Kafka**: Bootstrap servers, topics, consumer group
- **Couchbase**: Connection string, credentials, bucket/scope/collection
- **ChatRouting**: Business rules (office hours, timeouts, etc.)

## Deployment

### Docker

```powershell
docker build -t support-chat-kafka-couchbase .
docker run -p 80:80 support-chat-kafka-couchbase
```

### Kubernetes

```powershell
kubectl apply -f k8s/
```

## Features

- **Queue Routing**: Primary and overflow queue management
- **Agent Assignment**: Smart routing based on seniority and availability
- **Inactivity Detection**: Automatic session cleanup
- **Distributed Locking**: CAS-based concurrency control
- **Scalability**: Kafka partitioning for horizontal scaling

## Build Status

✅ Build succeeded with 5 warnings (nullable reference warnings only)
