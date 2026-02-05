sequenceDiagram
    autonumber
    participant Client
    participant API as API Pod (AKS)
    participant CB as Couchbase
    participant K as Kafka
    participant W as Worker Pod (AKS)

    %% SESSION CREATION
    Client->>API: POST /api/chat/sessions
    API->>CB: INSERT session::<id> (Queued)
    API->>K: PRODUCE message {sessionId, queueHint}
    API-->>Client: 200 OK (sessionId)

    %% POLLING
    loop Every 1 second
        Client->>API: GET /poll
        API->>CB: CAS UPDATE session (LastPollAt, PollCount++)
        API-->>Client: WAIT / ASSIGNED
    end

    %% ASSIGNMENT
    W->>K: CONSUME session message
    W->>CB: GET session::<id>

    alt Session already assigned / inactive
        W->>K: COMMIT offset (idempotent no-op)
    else Session eligible
        W->>CB: INSERT lock::agent::<agentId> (TTL)
        alt Lock acquired
            W->>CB: CAS UPDATE agent::<id> (add sessionId)
            W->>CB: CAS UPDATE session::<id> (ASSIGNED)
            W->>CB: REMOVE lock::agent::<agentId>
            W->>K: COMMIT offset
        else Lock denied
            W->>K: COMMIT offset (retry via other messages)
        end
    end

    %% INACTIVITY CLEANUP
    Note over W,CB: Inactivity Worker (separate loop)
    W->>CB: N1QL scan for stale sessions
    W->>CB: CAS UPDATE session (INACTIVE)
    W->>CB: CAS UPDATE agent (remove session)
