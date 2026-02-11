// API Configuration
const API_BASE = 'http://localhost:5000/api/chat';

// State Management
let currentSessionId = null;
let pollingInterval = null;
let pollCount = 0;

// View Navigation
document.querySelectorAll('.nav-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        const view = btn.dataset.view;
        switchView(view);
    });
});

function switchView(viewName) {
    // Update navigation
    document.querySelectorAll('.nav-btn').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.view === viewName);
    });

    // Update views
    document.querySelectorAll('.view').forEach(view => {
        view.classList.remove('active');
    });
    document.getElementById(`${viewName}-view`).classList.add('active');

    // Load data for admin view
    if (viewName === 'admin') {
        loadDashboardData();
    }
}

// Customer View - Start Chat
document.getElementById('startChatBtn').addEventListener('click', async () => {
    const customerRef = document.getElementById('customerRef').value || `customer-${Date.now()}`;
    
    try {
        document.getElementById('startChatBtn').disabled = true;
        document.getElementById('startChatBtn').textContent = 'Creating session...';

        const response = await fetch(`${API_BASE}/sessions`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ customerReference: customerRef })
        });

        if (!response.ok) {
            throw new Error('Failed to create session');
        }

        const data = await response.json();
        currentSessionId = data.sessionId;

        // Update UI
        document.getElementById('chat-form').classList.add('hidden');
        document.getElementById('chat-status').classList.remove('hidden');
        document.querySelector('.session-id').textContent = `Session: ${currentSessionId}`;
        document.querySelector('.queue-value').textContent = data.queue || 'Primary';

        // Start polling
        startPolling();
    } catch (error) {
        alert('Error creating chat session: ' + error.message);
        document.getElementById('startChatBtn').disabled = false;
        document.getElementById('startChatBtn').textContent = 'Start Chat Session';
    }
});

// Cancel Chat
document.getElementById('cancelChatBtn').addEventListener('click', () => {
    stopPolling();
    resetChatUI();
});

// Polling for Assignment
function startPolling() {
    pollCount = 0;
    updatePollCount();
    
    // Initial poll
    pollForAssignment();
    
    // Poll every 2 seconds
    pollingInterval = setInterval(pollForAssignment, 2000);
}

function stopPolling() {
    if (pollingInterval) {
        clearInterval(pollingInterval);
        pollingInterval = null;
    }
}

async function pollForAssignment() {
    if (!currentSessionId) return;

    try {
        const response = await fetch(`${API_BASE}/sessions/${currentSessionId}/poll`);
        
        if (!response.ok) {
            throw new Error('Polling failed');
        }

        const data = await response.json();
        pollCount++;
        updatePollCount();

        if (data.status === 'ASSIGNED') {
            // Agent assigned!
            stopPolling();
            showAssigned(data.agentId, data.team);
        } else if (data.status === 'INACTIVE') {
            // Session inactive
            stopPolling();
            showInactive();
        } else {
            // Still waiting
            document.querySelector('.status-value').textContent = 'Waiting...';
        }
    } catch (error) {
        console.error('Polling error:', error);
    }
}

function updatePollCount() {
    document.querySelector('.poll-count').textContent = pollCount;
}

function showAssigned(agentId, team) {
    document.querySelector('.status-indicator').classList.add('hidden');
    document.getElementById('assignment-info').classList.remove('hidden');
    document.getElementById('agentId').textContent = agentId;
    document.getElementById('agentTeam').textContent = team;
    document.querySelector('.status-value').textContent = 'Connected';
    document.getElementById('cancelChatBtn').textContent = 'End Session';
}

function showInactive() {
    document.querySelector('.status-indicator').classList.add('hidden');
    document.getElementById('inactive-info').classList.remove('hidden');
    document.querySelector('.status-value').textContent = 'Inactive';
}

function resetChatUI() {
    currentSessionId = null;
    pollCount = 0;
    
    document.getElementById('chat-form').classList.remove('hidden');
    document.getElementById('chat-status').classList.add('hidden');
    document.getElementById('assignment-info').classList.add('hidden');
    document.getElementById('inactive-info').classList.add('hidden');
    document.querySelector('.status-indicator').classList.remove('hidden');
    
    document.getElementById('startChatBtn').disabled = false;
    document.getElementById('startChatBtn').textContent = 'Start Chat Session';
    document.getElementById('customerRef').value = '';
    document.getElementById('cancelChatBtn').textContent = 'Cancel Session';
}

// Admin Dashboard
document.getElementById('refreshDashboard').addEventListener('click', loadDashboardData);

async function loadDashboardData() {
    try {
        // Load statistics
        const statsResponse = await fetch(`${API_BASE}/stats`);
        if (statsResponse.ok) {
            const stats = await statsResponse.json();
            document.getElementById('totalSessions').textContent = stats.totalSessions;
            document.getElementById('activeSessions').textContent = stats.activeSessions;
            document.getElementById('queuedSessions').textContent = stats.queuedSessions;
            document.getElementById('assignedSessions').textContent = stats.assignedSessions;
        }
        
        // Load sessions
        const sessionsResponse = await fetch(`${API_BASE}/sessions`);
        if (sessionsResponse.ok) {
            const sessions = await sessionsResponse.json();
            renderSessionsTable(sessions);
        } else {
            document.getElementById('sessionsTable').innerHTML = '<div class="loading">Failed to load sessions</div>';
        }
        
        // Load agents
        const agentsResponse = await fetch(`${API_BASE}/agents`);
        if (agentsResponse.ok) {
            const agents = await agentsResponse.json();
            renderAgentsTable(agents);
        } else {
            document.getElementById('agentsTable').innerHTML = '<div class="loading">Failed to load agents</div>';
        }
        
    } catch (error) {
        console.error('Error loading dashboard:', error);
        showError('Failed to load dashboard data');
    }
}

function renderSessionsTable(sessions) {
    if (sessions.length === 0) {
        document.getElementById('sessionsTable').innerHTML = '<div class="loading">No sessions found</div>';
        return;
    }
    
    const table = `
        <table>
            <thead>
                <tr>
                    <th>Session ID</th>
                    <th>Customer Ref</th>
                    <th>Status</th>
                    <th>Queue</th>
                    <th>Agent</th>
                    <th>Team</th>
                    <th>Polls</th>
                    <th>Created</th>
                </tr>
            </thead>
            <tbody>
                ${sessions.map(s => `
                    <tr>
                        <td style="font-family: monospace; font-size: 0.875rem;">${s.id.substring(0, 8)}...</td>
                        <td>${s.customerReference || '-'}</td>
                        <td>${getStatusBadge(s.status)}</td>
                        <td>${s.queueHint || '-'}</td>
                        <td style="font-family: monospace; font-size: 0.875rem;">${s.assignedAgentId || '-'}</td>
                        <td>${s.assignedTeam || '-'}</td>
                        <td>${s.pollCount}</td>
                        <td style="font-size: 0.875rem;">${formatDateTime(s.createdAtUtc)}</td>
                    </tr>
                `).join('')}
            </tbody>
        </table>
    `;
    
    document.getElementById('sessionsTable').innerHTML = table;
}

function renderAgentsTable(agents) {
    if (agents.length === 0) {
        document.getElementById('agentsTable').innerHTML = '<div class="loading">No agents found</div>';
        return;
    }
    
    const table = `
        <table>
            <thead>
                <tr>
                    <th>Agent ID</th>
                    <th>Team</th>
                    <th>Seniority</th>
                    <th>Shift</th>
                    <th>Active Chats</th>
                    <th>Capacity</th>
                    <th>Status</th>
                </tr>
            </thead>
            <tbody>
                ${agents.map(a => {
                    const capacity = getAgentCapacity(a);
                    const activeChats = a.activeSessionIds ? a.activeSessionIds.length : 0;
                    const utilization = capacity > 0 ? Math.round((activeChats / capacity) * 100) : 0;
                    const isOnShift = isAgentOnShift(a);
                    return `
                        <tr>
                            <td style="font-family: monospace;">${a.id}</td>
                            <td>${a.team}</td>
                            <td>${getSeniorityName(a.seniority)}</td>
                            <td style="font-size: 0.875rem;">${formatShift(a.shiftStart, a.shiftEnd)}</td>
                            <td>${activeChats}</td>
                            <td>${capacity}</td>
                            <td>
                                ${isOnShift 
                                    ? `<span class="status-badge status-assigned">On Shift (${utilization}%)</span>`
                                    : `<span class="status-badge status-inactive">Off Shift</span>`
                                }
                            </td>
                        </tr>
                    `;
                }).join('')}
            </tbody>
        </table>
    `;
    
    document.getElementById('agentsTable').innerHTML = table;
}

function showError(message) {
    alert(message);
}

// Utility Functions
function formatDateTime(dateString) {
    if (!dateString || dateString === '0001-01-01T00:00:00+00:00') return 'Never';
    const date = new Date(dateString);
    return date.toLocaleString();
}

function getStatusBadge(status) {
    const badges = {
        0: '<span class="status-badge status-queued">Queued Primary</span>',
        1: '<span class="status-badge status-queued">Queued Overflow</span>',
        2: '<span class="status-badge status-assigned">Assigned</span>',
        3: '<span class="status-badge status-inactive">Inactive</span>',
        4: '<span class="status-badge status-inactive">Closed</span>',
        5: '<span class="status-badge status-inactive">Refused</span>'
    };
    return badges[status] || `<span class="status-badge">${status}</span>`;
}

function getSeniorityName(seniority) {
    const names = {
        0: 'Junior',
        1: 'Mid-Level',
        2: 'Senior',
        3: 'Team Lead',
        4: 'Overflow Junior'
    };
    return names[seniority] || 'Unknown';
}

function formatShift(start, end) {
    if (!start || !end) return 'N/A';
    if (start === '00:00:00' && end === '00:00:00') return '24/7';
    return `${start.substring(0, 5)} - ${end.substring(0, 5)}`;
}

function getAgentCapacity(agent) {
    const multipliers = {
        0: 0.4, // Junior
        1: 0.6, // MidLevel
        2: 0.8, // Senior
        3: 0.5, // TeamLead
        4: 0.4  // OverflowJunior
    };
    const multiplier = multipliers[agent.seniority] || 0.4;
    return Math.floor(agent.maxConcurrentChats * multiplier);
}

function isAgentOnShift(agent) {
    const now = new Date();
    const utcHour = now.getUTCHours();
    const utcMinute = now.getUTCMinutes();
    const currentTime = utcHour + (utcMinute / 60);
    
    if (!agent.shiftStart || !agent.shiftEnd) return false;
    if (agent.shiftStart === '00:00:00' && agent.shiftEnd === '00:00:00') return true;
    
    const start = parseShiftTime(agent.shiftStart);
    const end = parseShiftTime(agent.shiftEnd);
    
    if (start < end) {
        return currentTime >= start && currentTime < end;
    } else {
        // Shift crosses midnight
        return currentTime >= start || currentTime < end;
    }
}

function parseShiftTime(timeString) {
    const parts = timeString.split(':');
    return parseInt(parts[0]) + (parseInt(parts[1]) / 60);
}

// Initialize
console.log('Support Chat UI initialized');
console.log('API Base:', API_BASE);
