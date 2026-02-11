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

## Web UI

The application includes a modern web interface accessible at http://localhost:5000

### Customer Interface
- **Start Chat Session**: Customers can request support by entering an optional reference ID
- **Real-time Status**: View queue position and assignment status
- **Live Polling**: Automatic polling every 2 seconds to check for agent assignment
- **Assignment Notification**: Displays assigned agent ID and team when connected

### Admin Dashboard
- **System Statistics**: View total, active, queued, and assigned sessions
- **Session Monitoring**: Track all chat sessions in real-time
- **Agent Status**: Monitor agent availability and workload

### Features
- 📱 Responsive design - works on desktop and mobile
- 🎨 Modern gradient UI with smooth animations
- ⚡ Real-time updates via polling
- 🔄 Auto-refresh capabilities
- 📊 Visual status indicators

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

## Business Rules

### Queue Management

#### Queue Capacity
- **Primary Queue**: Team capacity × 1.5 (rounded down)
- **Overflow Queue**: Overflow team capacity × 1.5 (rounded down)
- Sessions are **rejected** when:
  - Primary queue is full AND outside office hours
  - Primary queue is full AND overflow queue is also full

#### Team Capacity Calculation
Each agent's capacity = `10 concurrent chats × efficiency multiplier`

**Seniority Multipliers:**
- Junior: 0.4 (4 concurrent chats)
- Mid-Level: 0.6 (6 concurrent chats)
- Senior: 0.8 (8 concurrent chats)
- Team Lead: 0.5 (5 concurrent chats)

**Example:** Team with 2 mid-levels and 1 junior:
```
Capacity = (2 × 10 × 0.6) + (1 × 10 × 0.4) = 16 concurrent chats
Max Queue = 16 × 1.5 = 24 sessions
```

### Team Configuration

**Team A (Primary Support)**
- 1× Team Lead (capacity: 5)
- 2× Mid-Level (capacity: 6 each)
- 1× Junior (capacity: 4)
- **Total Capacity:** 21 concurrent chats
- **Max Queue:** 31 sessions

**Team B (Primary Support)**
- 1× Senior (capacity: 8)
- 1× Mid-Level (capacity: 6)
- 2× Junior (capacity: 4 each)
- **Total Capacity:** 22 concurrent chats
- **Max Queue:** 33 sessions

**Team C (Night Shift)**
- 2× Mid-Level (capacity: 6 each)
- **Total Capacity:** 12 concurrent chats
- **Max Queue:** 18 sessions

**Overflow Team (Office Hours Only)**
- 6× Junior equivalent (capacity: 4 each)
- **Total Capacity:** 24 concurrent chats
- **Max Queue:** 36 sessions

### Chat Assignment Logic

#### Round-Robin with Seniority Priority
Chats are assigned in round-robin fashion, **preferring junior agents first**, then mid-level, then senior, then team lead.

**Rationale:** Keep higher seniority agents available to assist lower seniority agents.

**Example 1:** Team with 1 Senior (cap 8) + 1 Junior (cap 4)
- 5 chats arrive → 4 assigned to Junior, 1 to Senior

**Example 2:** Team with 2 Juniors + 1 Mid-Level
- 6 chats arrive → 3 to each Junior, 0 to Mid-Level

#### Shift Management
- Agents work **8-hour shifts** (3 shifts per day)
- When shift ends, agent:
  - ✅ **Finishes** current active chats
  - ❌ **Does NOT** receive new assignments

#### Inactivity Detection
- Client must poll `GET /api/chat/sessions/{id}/poll` every 1-2 seconds
- Session marked **INACTIVE** after missing **3 consecutive polls**
- Monitor checks every 800ms (configurable)

### Office Hours & Overflow
- **Office Hours:** 09:00 - 17:00 UTC (configurable)
- **During Office Hours:** Overflow team activates when primary queue is full
- **Outside Office Hours:** Sessions rejected when primary queue is full (no overflow)

## Build Status

✅ Build succeeded with 5 warnings (nullable reference warnings only)
