# Workflow Diagrams

## Complete Chat Session Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Created: Customer requests chat
    Created --> QueuedPrimary: Queue validation passed
    Created --> QueuedOverflow: Primary full, office hours
    Created --> Refused: All queues full
    
    QueuedPrimary --> Assigned: Agent available
    QueuedOverflow --> Assigned: Agent available
    
    Assigned --> Active: Customer polls
    
    Active --> Inactive: 3 missed polls
    Active --> Closed: Session ends normally
    
    QueuedPrimary --> Inactive: Never polled + timeout
    QueuedOverflow --> Inactive: Never polled + timeout
    
    Inactive --> [*]
    Closed --> [*]
    Refused --> [*]
    
    note right of Created
        Customer submits request
        System checks queue capacity
    end note
    
    note right of QueuedPrimary
        In Kafka primary topic
        Waiting for assignment
    end note
    
    note right of QueuedOverflow
        In Kafka overflow topic
        Office hours only
    end note
    
    note right of Assigned
        Agent selected
        Customer notified
    end note
    
    note right of Active
        Customer polling
        Chat in progress
    end note
    
    note right of Inactive
        Missed 3 polls
        Agent slot released
    end note
```

## Session Creation & Queue Validation Workflow

```mermaid
flowchart TD
    Start([Customer Starts Chat]) --> GetTime[Get Current UTC Time]
    GetTime --> DetermineTeam[Determine Current Team<br/>TeamA/B/C based on time]
    DetermineTeam --> CountPrimary[Count Primary Queue Sessions]
    CountPrimary --> GetCapacity[Get Team Capacity]
    GetCapacity --> CalcMax[Calculate Max Queue<br/>capacity × 1.5]
    
    CalcMax --> CheckPrimary{Primary Queue<br/>< Max?}
    
    CheckPrimary -->|Yes| CreatePrimary[Create Session<br/>Status: QueuedPrimary]
    CheckPrimary -->|No| CheckOfficeHours{Office Hours<br/>09:00-17:00 UTC?}
    
    CheckOfficeHours -->|No| RefuseOutside[Refuse Session<br/>Outside office hours]
    CheckOfficeHours -->|Yes| CountOverflow[Count Overflow Queue]
    
    CountOverflow --> GetOverflowCap[Get Overflow Capacity<br/>6 agents × 4 = 24]
    GetOverflowCap --> CalcOverflowMax[Calculate Max Overflow<br/>24 × 1.5 = 36]
    
    CalcOverflowMax --> CheckOverflow{Overflow Queue<br/>< Max?}
    
    CheckOverflow -->|Yes| CreateOverflow[Create Session<br/>Status: QueuedOverflow]
    CheckOverflow -->|No| RefuseFull[Refuse Session<br/>All queues full]
    
    CreatePrimary --> PublishPrimary[Publish to Kafka<br/>primary topic]
    CreateOverflow --> PublishOverflow[Publish to Kafka<br/>overflow topic]
    
    PublishPrimary --> Success([Return SessionId<br/>Accepted: true])
    PublishOverflow --> Success
    
    RefuseOutside --> Failure([Return Error<br/>Accepted: false])
    RefuseFull --> Failure
    
    style Start fill:#e1f5ff
    style Success fill:#c8e6c9
    style Failure fill:#ffcdd2
    style CreatePrimary fill:#fff9c4
    style CreateOverflow fill:#ffe0b2
```

## Agent Assignment Workflow

```mermaid
flowchart TD
    Start([Message from Kafka]) --> Extract[Extract SessionId<br/>and QueueHint]
    Extract --> GetSession[Get Session from DB]
    GetSession --> CheckStatus{Status =<br/>Queued?}
    
    CheckStatus -->|No| Commit[Commit Kafka Offset]
    CheckStatus -->|Yes| GetTime[Get Current Local Time]
    
    GetTime --> DetermineTeam[Determine Target Team<br/>Primary: TeamA/B/C<br/>Overflow: Overflow]
    DetermineTeam --> CheckOverflow{QueueHint =<br/>Overflow?}
    
    CheckOverflow -->|Yes| CheckOfficeHours{Office Hours?}
    CheckOverflow -->|No| GetAgents
    
    CheckOfficeHours -->|No| Commit
    CheckOfficeHours -->|Yes| GetAgents[Get Team Agents]
    
    GetAgents --> SortSeniority[Sort by Seniority<br/>Junior → Mid → Senior → Lead]
    
    SortSeniority --> LoopJunior[Loop: Junior Agents]
    LoopJunior --> FilterShift[Filter: On Shift?]
    FilterShift --> RoundRobin[Round-Robin Select<br/>Using per-team cursor]
    
    RoundRobin --> TryLock{Acquire<br/>Agent Lock?}
    
    TryLock -->|No| NextAgent[Try Next Agent]
    TryLock -->|Yes| RecheckSession[Re-check Session Status]
    
    RecheckSession --> AddToAgent{Add Session<br/>to Agent?}
    
    AddToAgent -->|CAS Fail| ReleaseLock1[Release Lock]
    AddToAgent -->|Success| MarkAssigned{Mark Session<br/>Assigned?}
    
    MarkAssigned -->|CAS Fail| Rollback[Rollback:<br/>Remove from Agent]
    MarkAssigned -->|Success| UpdateCursor[Update Round-Robin<br/>Cursor]
    
    Rollback --> ReleaseLock2[Release Lock]
    UpdateCursor --> ReleaseLock3[Release Lock]
    
    ReleaseLock1 --> NextAgent
    ReleaseLock2 --> NextAgent
    ReleaseLock3 --> CommitSuccess[Commit Kafka Offset]
    
    NextAgent --> CheckMore{More Agents<br/>in Seniority?}
    CheckMore -->|Yes| RoundRobin
    CheckMore -->|No| NextSeniority{Next Seniority<br/>Level?}
    
    NextSeniority -->|Yes| LoopJunior
    NextSeniority -->|No| Commit
    
    CommitSuccess --> End([Assignment Complete])
    Commit --> End
    
    style Start fill:#e1f5ff
    style End fill:#c8e6c9
    style TryLock fill:#fff9c4
    style AddToAgent fill:#fff9c4
    style MarkAssigned fill:#fff9c4
```

## Customer Polling Workflow

```mermaid
flowchart TD
    Start([Client Polls Every 2s]) --> CallPoll[GET /sessions/:id/poll]
    CallPoll --> TouchPoll[Update Session:<br/>PollCount++<br/>LastPollAtUtc = Now]
    
    TouchPoll --> GetSession[Get Session]
    GetSession --> CheckStatus{Session Status?}
    
    CheckStatus -->|Assigned| ReturnAssigned[Return ASSIGNED<br/>with agentId and team]
    CheckStatus -->|Inactive/Closed| ReturnInactive[Return INACTIVE]
    CheckStatus -->|QueuedPrimary| ReturnWait[Return WAIT]
    CheckStatus -->|QueuedOverflow| ReturnWait
    
    ReturnAssigned --> DisplayAgent[UI: Show Agent<br/>Stop Polling]
    ReturnInactive --> DisplayInactive[UI: Show Inactive<br/>Stop Polling]
    ReturnWait --> Continue[UI: Keep Polling]
    
    Continue --> Wait2s[Wait 2 seconds]
    Wait2s --> Start
    
    DisplayAgent --> End([Session Connected])
    DisplayInactive --> End
    
    style Start fill:#e1f5ff
    style End fill:#c8e6c9
    style ReturnAssigned fill:#aed581
    style ReturnInactive fill:#ffcc80
    style ReturnWait fill:#fff59d
```

## Inactivity Detection Workflow

```mermaid
flowchart TD
    Start([Timer: Every 800ms]) --> CalcCutoff[Calculate Cutoff Time<br/>Now - 3 × PollInterval]
    CalcCutoff --> QueryDB[Query Sessions:<br/>PollCount > 0<br/>LastPollAtUtc < Cutoff<br/>Status IN Queued/Assigned]
    
    QueryDB --> LoopSessions[Loop Each Stale Session]
    
    LoopSessions --> TryMark{Mark Session<br/>Inactive<br/>CAS?}
    
    TryMark -->|Fail| NextSession[Next Session]
    TryMark -->|Success| GetSession[Get Session Details]
    
    GetSession --> CheckAgent{Has Assigned<br/>Agent?}
    
    CheckAgent -->|No| LogInactive[Log: Session Inactive]
    CheckAgent -->|Yes| RemoveFromAgent[Remove Session<br/>from Agent.ActiveSessionIds]
    
    RemoveFromAgent --> ReleaseCap[Release Agent Capacity]
    ReleaseCap --> LogInactive
    
    LogInactive --> NextSession
    
    NextSession --> MoreSessions{More Stale<br/>Sessions?}
    
    MoreSessions -->|Yes| LoopSessions
    MoreSessions -->|No| Wait[Wait 800ms]
    
    Wait --> Start
    
    style Start fill:#e1f5ff
    style TryMark fill:#fff9c4
    style RemoveFromAgent fill:#ffab91
```

## Team Selection Logic

```mermaid
flowchart TD
    Start([Get Current UTC Time]) --> GetLocal[Convert to Local Time<br/>TimeZone: UTC default]
    GetLocal --> GetHour[Extract Hour of Day]
    
    GetHour --> Check1{Hour >= 0<br/>AND Hour < 8?}
    Check1 -->|Yes| TeamC[Select TeamC<br/>Night Shift<br/>2× Mid-Level]
    Check1 -->|No| Check2{Hour >= 8<br/>AND Hour < 16?}
    
    Check2 -->|Yes| TeamA[Select TeamA<br/>Day Shift<br/>1 Lead, 2 Mid, 1 Junior]
    Check2 -->|No| TeamB[Select TeamB<br/>Evening Shift<br/>1 Senior, 1 Mid, 2 Junior]
    
    TeamC --> Return([Return Team])
    TeamA --> Return
    TeamB --> Return
    
    style Start fill:#e1f5ff
    style Return fill:#c8e6c9
    style TeamA fill:#fff59d
    style TeamB fill:#ffe082
    style TeamC fill:#90caf9
```

## Round-Robin Agent Selection

```mermaid
flowchart TD
    Start([Get Team Agents]) --> GroupBySeniority[Group Agents by Seniority]
    GroupBySeniority --> LoopSeniority[Seniority Order:<br/>1. Junior<br/>2. OverflowJunior<br/>3. MidLevel<br/>4. Senior<br/>5. TeamLead]
    
    LoopSeniority --> GetGroup[Get Agents in<br/>Current Seniority]
    GetGroup --> FilterShift[Filter: On Shift<br/>CurrentTime in ShiftStart-ShiftEnd]
    
    FilterShift --> CheckEmpty{Any Agents<br/>Available?}
    CheckEmpty -->|No| NextSeniority[Try Next Seniority]
    CheckEmpty -->|Yes| GetCursor[Get Round-Robin Cursor<br/>Key: team:seniority]
    
    GetCursor --> CalculateIndex[Calculate Index<br/>idx = cursor % agentCount]
    CalculateIndex --> SelectAgent[Select Agent at Index]
    
    SelectAgent --> TryAssign{Try Assign<br/>to Agent?}
    
    TryAssign -->|Success| UpdateCursor[Update Cursor<br/>cursor = idx + 1]
    TryAssign -->|Fail| NextAttempt[Try Next Agent<br/>idx + 1]
    
    NextAttempt --> CheckAllTried{Tried All<br/>in Group?}
    CheckAllTried -->|No| SelectAgent
    CheckAllTried -->|Yes| NextSeniority
    
    UpdateCursor --> Success([Assignment Complete])
    NextSeniority --> MoreSeniority{More Seniority<br/>Levels?}
    
    MoreSeniority -->|Yes| LoopSeniority
    MoreSeniority -->|No| NoAgent([No Agent Available])
    
    style Start fill:#e1f5ff
    style Success fill:#c8e6c9
    style NoAgent fill:#ffcdd2
    style TryAssign fill:#fff9c4
```

## Capacity Calculation

```mermaid
flowchart TD
    Start([Calculate Team Capacity]) --> GetAgents[Get All Agents in Team]
    GetAgents --> LoopAgents[For Each Agent]
    
    LoopAgents --> GetSeniority{Agent Seniority?}
    
    GetSeniority -->|Junior| MultJunior[10 × 0.4 = 4]
    GetSeniority -->|MidLevel| MultMid[10 × 0.6 = 6]
    GetSeniority -->|Senior| MultSenior[10 × 0.8 = 8]
    GetSeniority -->|TeamLead| MultLead[10 × 0.5 = 5]
    GetSeniority -->|OverflowJunior| MultOverflow[10 × 0.4 = 4]
    
    MultJunior --> AddToTotal[Add to Total Capacity]
    MultMid --> AddToTotal
    MultSenior --> AddToTotal
    MultLead --> AddToTotal
    MultOverflow --> AddToTotal
    
    AddToTotal --> MoreAgents{More Agents?}
    MoreAgents -->|Yes| LoopAgents
    MoreAgents -->|No| CalcQueue[Calculate Max Queue<br/>capacity × 1.5<br/>Round Down]
    
    CalcQueue --> Return([Return Capacity & MaxQueue])
    
    style Start fill:#e1f5ff
    style Return fill:#c8e6c9
    
    note right of GetSeniority
        MaxConcurrentChats = 10
        Multiplier varies by seniority
        Floor result to integer
    end note
```

## Example Scenarios

### Scenario 1: Normal Assignment (Office Hours)
```mermaid
gantt
    title Chat Session Timeline - Successful Assignment
    dateFormat HH:mm:ss
    axisFormat %H:%M:%S
    
    section Customer
    Submit Request     :active, 10:00:00, 1s
    Start Polling      :active, 10:00:01, 5s
    Receive Assignment :milestone, 10:00:05, 0s
    
    section System
    Queue Validation   :10:00:00, 1s
    Publish to Kafka   :10:00:01, 1s
    
    section Worker
    Consume Message    :10:00:02, 1s
    Find Agent         :10:00:03, 1s
    Acquire Lock       :10:00:04, 500ms
    Assign Session     :10:00:04, 1s
    
    section Agent
    Update ActiveSessions :10:00:04, 500ms
    Ready for Chat        :milestone, 10:00:05, 0s
```

### Scenario 2: Overflow Routing (Queue Full)
```mermaid
gantt
    title Chat Session Timeline - Overflow Routing
    dateFormat HH:mm:ss
    axisFormat %H:%M:%S
    
    section Customer
    Submit Request     :active, 14:30:00, 1s
    
    section System
    Check Primary Queue   :14:30:00, 500ms
    Primary Queue Full    :crit, 14:30:00, 100ms
    Check Office Hours    :14:30:00, 100ms
    Check Overflow Queue  :14:30:01, 500ms
    Overflow Available    :14:30:01, 100ms
    Route to Overflow     :14:30:01, 500ms
    Publish to Kafka      :14:30:02, 1s
    
    section Worker
    Consume from Overflow :14:30:03, 1s
    Assign to Overflow Agent :14:30:04, 2s
    
    section Customer
    Receive Assignment :milestone, 14:30:06, 0s
```

### Scenario 3: Session Refusal (All Queues Full)
```mermaid
gantt
    title Chat Session Timeline - Refused
    dateFormat HH:mm:ss
    axisFormat %H:%M:%S
    
    section Customer
    Submit Request        :active, 15:45:00, 1s
    Receive Refusal       :crit, 15:45:01, 0s
    
    section System
    Check Primary Queue      :15:45:00, 300ms
    Primary Queue Full       :crit, 15:45:00, 100ms
    Check Office Hours       :15:45:00, 100ms
    Check Overflow Queue     :15:45:01, 300ms
    Overflow Queue Full      :crit, 15:45:01, 100ms
    Return Refusal Response  :crit, 15:45:01, 200ms
```

### Scenario 4: Inactivity Detection
```mermaid
gantt
    title Chat Session Timeline - Inactive
    dateFormat HH:mm:ss
    axisFormat %H:%M:%S
    
    section Customer
    Last Poll          :active, 11:00:00, 1s
    Stop Polling       :crit, 11:00:01, 6s
    
    section System
    Poll Count = 5     :11:00:01, 0s
    No Poll Received   :crit, 11:00:01, 3s
    No Poll Received   :crit, 11:00:04, 3s
    
    section Worker
    Scan for Inactive  :11:00:04, 800ms
    Detect Stale Session :crit, 11:00:05, 100ms
    Mark Inactive      :11:00:05, 500ms
    Release Agent Slot :11:00:05, 500ms
```
