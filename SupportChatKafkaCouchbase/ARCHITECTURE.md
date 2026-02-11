# Architecture Diagram

## System Overview

```mermaid
graph TB
    subgraph "Client Layer"
        UI[Web UI<br/>Customer & Admin Dashboard]
        API_Client[External API Clients]
    end

    subgraph "API Layer"
        API[ASP.NET Core API<br/>Minimal APIs]
        CORS[CORS Middleware]
        Static[Static Files Middleware]
    end

    subgraph "Application Layer"
        Endpoints[API Endpoints<br/>Sessions, Polls, Stats]
        Workers[Background Workers]
        AW[Assignment Worker<br/>Kafka Consumer]
        IW[Inactivity Worker<br/>Timer-based Scanner]
    end

    subgraph "Domain Layer"
        Models[Domain Models<br/>ChatSession, AgentState]
        Enums[Business Enums<br/>Status, Seniority]
        Rules[Business Rules<br/>Capacity, Shifts]
    end

    subgraph "Infrastructure Layer"
        Repos[Repositories<br/>SessionRepo, AgentRepo]
        Lock[Distributed Lock<br/>CAS-based]
        KafkaProd[Kafka Producer]
        KafkaCons[Kafka Consumer Factory]
        CouchCtx[Couchbase Context<br/>Cluster Connection]
    end

    subgraph "External Services"
        Kafka[Apache Kafka<br/>Message Broker]
        PT[Primary Topic]
        OT[Overflow Topic]
        Couchbase[Couchbase Server<br/>NoSQL Database]
        Bucket[support Bucket<br/>Sessions & Agents]
    end

    UI --> API
    API_Client --> API
    API --> CORS
    CORS --> Static
    Static --> Endpoints
    
    Endpoints --> Repos
    Endpoints --> KafkaProd
    
    Workers --> AW
    Workers --> IW
    
    AW --> KafkaCons
    AW --> Repos
    AW --> Lock
    
    IW --> Repos
    IW --> CouchCtx
    
    Repos --> Models
    Repos --> CouchCtx
    
    Lock --> CouchCtx
    
    KafkaProd --> Kafka
    KafkaCons --> Kafka
    
    Kafka --> PT
    Kafka --> OT
    
    CouchCtx --> Couchbase
    Couchbase --> Bucket
    
    style UI fill:#e1f5ff
    style API fill:#fff4e1
    style Workers fill:#e8f5e9
    style Kafka fill:#f3e5f5
    style Couchbase fill:#fff3e0
```

## Component Details

### Client Layer
- **Web UI**: Modern JavaScript SPA with customer chat interface and admin dashboard
- **External Clients**: REST API consumers (mobile apps, integrations)

### API Layer
- **ASP.NET Core 8**: Minimal APIs for lightweight, high-performance endpoints
- **CORS**: Cross-Origin Resource Sharing for browser-based clients
- **Static Files**: Serves wwwroot content (HTML, CSS, JS)

### Application Layer
- **API Endpoints**: 
  - `POST /api/chat/sessions` - Create session with queue validation
  - `GET /api/chat/sessions/{id}` - Get session details
  - `GET /api/chat/sessions/{id}/poll` - Poll for assignment
  - `GET /api/chat/sessions` - List sessions (admin)
  - `GET /api/chat/agents` - List agents (admin)
  - `GET /api/chat/stats` - System statistics (admin)

- **Background Workers**:
  - **Assignment Worker**: Consumes from Kafka topics, assigns sessions to agents using round-robin with seniority priority
  - **Inactivity Worker**: Scans for sessions missing 3 polls, marks inactive and releases agent slots

### Domain Layer
- **Models**: `ChatSession`, `AgentState` with business logic
- **Enums**: `ChatSessionStatus`, `Seniority` for type safety
- **Business Rules**: Capacity calculations, shift validation, office hours

### Infrastructure Layer
- **Repositories**: Data access abstraction with CAS-based optimistic locking
- **Distributed Lock**: Agent-level locking during assignment to prevent race conditions
- **Kafka Integration**: Producer for queueing, consumer factory for workers
- **Couchbase Context**: Singleton cluster connection with collection access

### External Services
- **Apache Kafka**: Message broker for reliable FIFO queue processing
  - **Primary Topic**: Default queue for sessions
  - **Overflow Topic**: Office hours overflow when primary is full
  
- **Couchbase Server**: NoSQL database for session and agent state
  - **support Bucket**: Default collection stores all documents
  - **Document Types**: `session::<guid>`, `agent::<id>`, `lock::<key>`

## Data Flow

### Session Creation Flow
```mermaid
sequenceDiagram
    participant C as Client
    participant API as API Endpoint
    participant SR as SessionRepository
    participant AR as AgentRepository
    participant KP as Kafka Producer
    participant K as Kafka

    C->>API: POST /api/chat/sessions
    API->>SR: CountQueuedAsync(Primary)
    SR-->>API: queuedCount
    API->>AR: GetTeamCapacityAsync(currentTeam)
    AR-->>API: teamCapacity
    
    alt Queue has capacity
        API->>SR: CreateAsync(session)
        SR->>CB: Insert session doc
        API->>KP: ProduceAsync(topic, sessionId)
        KP->>K: Publish message
        API-->>C: 200 OK {sessionId, queue}
    else Queue full + office hours
        API->>SR: CountQueuedAsync(Overflow)
        API->>AR: GetTeamCapacityAsync(Overflow)
        alt Overflow available
            API->>SR: CreateAsync(session)
            API->>KP: ProduceAsync(overflowTopic)
            API-->>C: 200 OK {sessionId, Overflow}
        else Overflow full
            API-->>C: 200 OK {accepted: false}
        end
    else Queue full + outside hours
        API-->>C: 200 OK {accepted: false}
    end
```

### Assignment Flow
```mermaid
sequenceDiagram
    participant K as Kafka
    participant AW as Assignment Worker
    participant DL as Distributed Lock
    participant AR as AgentRepository
    participant SR as SessionRepository
    participant CB as Couchbase

    K->>AW: Consume message {sessionId, queueHint}
    AW->>SR: GetAsync(sessionId)
    SR-->>AW: session
    
    AW->>AR: GetTeamAgentsAsync(team)
    AR-->>AW: agents[]
    
    loop Round-robin by seniority
        AW->>DL: TryAcquireAsync(agentLock)
        alt Lock acquired
            DL-->>AW: true
            AW->>SR: GetAsync(sessionId) [recheck]
            AW->>AR: TryAddSessionAsync(agentId)
            AR->>CB: CAS update agent.ActiveSessionIds
            alt CAS success
                CB-->>AR: true
                AR-->>AW: true
                AW->>SR: TryMarkAssignedAsync(sessionId, agentId)
                SR->>CB: CAS update session.Status
                CB-->>SR: true
                SR-->>AW: true
                AW->>DL: ReleaseAsync(agentLock)
                AW-->>K: Commit offset
            else CAS conflict
                CB-->>AR: false
                AR-->>AW: false
                AW->>DL: ReleaseAsync(agentLock)
            end
        else Lock failed
            DL-->>AW: false
        end
    end
```

### Polling Flow
```mermaid
sequenceDiagram
    participant C as Client
    participant API as Poll Endpoint
    participant SR as SessionRepository
    participant CB as Couchbase

    loop Every 2 seconds
        C->>API: GET /sessions/{id}/poll
        API->>SR: TouchPollAsync(sessionId)
        SR->>CB: CAS update PollCount++
        CB-->>SR: success
        SR-->>API: true
        API->>SR: GetAsync(sessionId)
        SR-->>API: session
        
        alt Status = Assigned
            API-->>C: {status: ASSIGNED, agentId, team}
        else Status = Inactive/Closed
            API-->>C: {status: INACTIVE}
        else Status = Queued
            API-->>C: {status: WAIT}
        end
    end
```

### Inactivity Detection Flow
```mermaid
sequenceDiagram
    participant IW as Inactivity Worker
    participant CB as Couchbase
    participant SR as SessionRepository
    participant AR as AgentRepository

    loop Every 800ms
        IW->>CB: Query sessions with missed polls
        Note over CB: WHERE PollCount > 0<br/>AND LastPollAtUtc < cutoff
        CB-->>IW: stale_sessions[]
        
        loop For each stale session
            IW->>SR: TryMarkInactiveAsync(sessionId)
            SR->>CB: CAS update Status=Inactive
            alt CAS success
                CB-->>SR: true
                SR-->>IW: true
                IW->>SR: GetAsync(sessionId)
                SR-->>IW: session
                
                alt Has assigned agent
                    IW->>AR: RemoveSessionAsync(agentId, sessionId)
                    AR->>CB: CAS remove from ActiveSessionIds
                end
            end
        end
    end
```

## Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Runtime | .NET 8.0 | Modern, high-performance framework |
| API | ASP.NET Core Minimal APIs | Lightweight HTTP endpoints |
| Message Broker | Apache Kafka 7.5.0 | Reliable FIFO queue with partitioning |
| Database | Couchbase (latest) | NoSQL with N1QL queries and CAS |
| UI | Vanilla JavaScript + CSS | No framework dependencies |
| Containerization | Docker + Docker Compose | Local development environment |
| Orchestration | Kubernetes | Production deployment (optional) |

## Scalability Considerations

### Horizontal Scaling
- **Multiple API Instances**: Stateless endpoints with load balancer
- **Kafka Partitioning**: Partition by session ID for parallel processing
- **Kafka Consumer Groups**: Multiple worker instances share partition load
- **Couchbase Cluster**: Distributed nodes with replication

### Concurrency Control
- **CAS Operations**: Optimistic locking on all state mutations
- **Distributed Locks**: Agent-level locks during assignment
- **Idempotent Operations**: Safe retry on conflicts

### Performance Optimizations
- **In-Memory Round-Robin**: Per-worker cursors (consistency not critical)
- **Batch Operations**: Kafka commit after successful assignment
- **Query Indexes**: N1QL indexes on Status, Team, LastPollAtUtc
- **Connection Pooling**: Singleton Couchbase cluster connection

## Deployment Architecture

```mermaid
graph TB
    subgraph "Kubernetes Cluster"
        subgraph "Ingress"
            LB[Load Balancer<br/>Ingress Controller]
        end
        
        subgraph "API Pods"
            API1[API Pod 1<br/>2 replicas min]
            API2[API Pod 2]
        end
        
        subgraph "Worker Pods"
            W1[Worker Pod 1<br/>Assignment + Inactivity]
            W2[Worker Pod 2<br/>Consumer Group Member]
        end
        
        subgraph "Config"
            CM[ConfigMap<br/>appsettings.json]
            SEC[Secret<br/>Credentials]
        end
        
        subgraph "Autoscaling"
            HPA[Horizontal Pod Autoscaler<br/>CPU/Memory based]
        end
    end
    
    subgraph "External Infrastructure"
        KafkaExt[Kafka Cluster<br/>3+ brokers]
        CouchExt[Couchbase Cluster<br/>3+ nodes]
    end
    
    LB --> API1
    LB --> API2
    
    API1 --> CM
    API1 --> SEC
    API2 --> CM
    API2 --> SEC
    
    W1 --> CM
    W1 --> SEC
    W2 --> CM
    W2 --> SEC
    
    HPA -.->|scales| API1
    HPA -.->|scales| W1
    
    API1 --> KafkaExt
    API2 --> KafkaExt
    W1 --> KafkaExt
    W2 --> KafkaExt
    
    API1 --> CouchExt
    API2 --> CouchExt
    W1 --> CouchExt
    W2 --> CouchExt
    
    style LB fill:#e1f5ff
    style API1 fill:#fff4e1
    style W1 fill:#e8f5e9
    style KafkaExt fill:#f3e5f5
    style CouchExt fill:#fff3e0
```

## Security Considerations

- **CORS**: Configured for browser-based access
- **Secrets Management**: Kubernetes Secrets for credentials
- **Network Policies**: Restrict pod-to-pod communication
- **TLS/SSL**: HTTPS for external traffic (Ingress)
- **Authentication**: Not implemented (add OAuth2/JWT as needed)
- **Rate Limiting**: Not implemented (add API gateway as needed)
