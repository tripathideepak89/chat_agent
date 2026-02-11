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
        // Note: These endpoints would need to be created in the backend
        // For now, we'll show placeholder data
        
        // Simulate loading
        document.getElementById('sessionsTable').innerHTML = '<div class="loading">Loading sessions...</div>';
        document.getElementById('agentsTable').innerHTML = '<div class="loading">Loading agents...</div>';
        
        // Since we don't have these endpoints yet, show a message
        setTimeout(() => {
            document.getElementById('totalSessions').textContent = 'N/A';
            document.getElementById('activeSessions').textContent = 'N/A';
            document.getElementById('queuedSessions').textContent = 'N/A';
            document.getElementById('assignedSessions').textContent = 'N/A';
            
            document.getElementById('sessionsTable').innerHTML = `
                <div style="padding: 2rem; text-align: center; color: var(--text-secondary);">
                    <p>📊 Dashboard endpoints not yet implemented</p>
                    <p style="margin-top: 0.5rem; font-size: 0.875rem;">To enable the dashboard, add the following endpoints to the backend:</p>
                    <ul style="list-style: none; margin-top: 1rem; font-family: monospace; font-size: 0.875rem;">
                        <li>GET /api/chat/sessions (list all sessions)</li>
                        <li>GET /api/chat/agents (list all agents)</li>
                        <li>GET /api/chat/stats (get statistics)</li>
                    </ul>
                </div>
            `;
            
            document.getElementById('agentsTable').innerHTML = `
                <div style="padding: 2rem; text-align: center; color: var(--text-secondary);">
                    <p>Use Couchbase Query Console at <a href="http://localhost:8093" target="_blank" style="color: var(--primary-color);">http://localhost:8093</a> to query agent data</p>
                    <p style="margin-top: 0.5rem; font-size: 0.875rem;">Example query:</p>
                    <code style="display: block; margin-top: 0.5rem; padding: 1rem; background: var(--bg-color); border-radius: 6px;">
                        SELECT d.* FROM \`support\`._default._default d<br>
                        WHERE META(d).id LIKE 'agent::%'
                    </code>
                </div>
            `;
        }, 500);
        
    } catch (error) {
        console.error('Error loading dashboard:', error);
        showError('Failed to load dashboard data');
    }
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

// Initialize
console.log('Support Chat UI initialized');
console.log('API Base:', API_BASE);
