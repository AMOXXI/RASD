'use strict';
require('dotenv').config();

const express    = require('express');
const http       = require('http');
const cors       = require('cors');
const helmet     = require('helmet');
const path       = require('path');
const { Server } = require('socket.io');
const os         = require('os');

const { pool, testConnection } = require('./db/pool');
const nodesRouter    = require('./routes/nodes');
const commandsRouter = require('./routes/commands');
const authRouter     = require('./routes/auth');
const alertsRouter   = require('./routes/alerts');
const logsRouter     = require('./routes/logs');
const reportsRouter  = require('./routes/reports');
const usersRouter    = require('./routes/users');
const { authenticateToken } = require('./routes/auth');

const app    = express();
const server = http.createServer(app);
const PORT   = parseInt(process.env.PORT) || 3000;

// ================================================================
// SOCKET.IO — Real-time bridge (Dashboard ↔ Agents)
// ================================================================
const io = new Server(server, {
  cors: { origin: '*', methods: ['GET', 'POST'] },
  transports: ['websocket', 'polling'],
  pingInterval: 25000,
  pingTimeout: 20000,
});

app.set('io', io);

io.on('connection', (socket) => {
  const { role, nodeId } = socket.handshake.query;
  console.log(`[WS] ${role === 'agent' ? 'Agent' : role === 'watcher' ? 'Watcher' : 'Dashboard'} connected — ${socket.id}`);

  if (role === 'agent' && nodeId) {
    socket.join(`agent:${nodeId}`);
    console.log(`[WS] Agent ${nodeId} joined room agent:${nodeId}`);
  }

  socket.on('disconnect', () => {
    console.log(`[WS] Disconnected — ${socket.id}`);
  });
});

// ================================================================
// MIDDLEWARE
// ================================================================
app.use(helmet({ contentSecurityPolicy: false, crossOriginEmbedderPolicy: false }));
app.use(cors({ origin: '*' }));
app.use(express.json({ limit: '2mb' }));
app.use(express.urlencoded({ extended: true }));

app.use((req, _res, next) => {
  process.stdout.write(`[API] ${req.method} ${req.path}\n`);
  next();
});

// ================================================================
// ROUTES — Public (no auth, for agents)
// ================================================================
app.use('/api/nodes',   nodesRouter);
app.use('/api/command', commandsRouter);

// ================================================================
// ROUTES — Auth
// ================================================================
app.use('/api/v1/auth', authRouter);

// ================================================================
// ROUTES — Protected (JWT required, for dashboard)
// ================================================================
app.use('/api/v1/alerts',   authenticateToken, alertsRouter);
app.use('/api/v1/nodes',    authenticateToken, nodesRouter);
app.use('/api/v1/commands', authenticateToken, commandsRouter);
app.use('/api/v1/logs',     authenticateToken, logsRouter);
app.use('/api/v1/reports',  authenticateToken, reportsRouter);
app.use('/api/v1/users',    authenticateToken, usersRouter);

// ================================================================
// HEALTH CHECK — Public (used by Admin Watcher watchdog)
// ================================================================
app.get('/api/health', async (req, res) => {
  try {
    const { rows } = await pool.query(`
      SELECT
        (SELECT COUNT(*) FROM managed_nodes)                          AS total_nodes,
        (SELECT COUNT(*) FROM managed_nodes WHERE online = TRUE)      AS online_nodes,
        (SELECT COUNT(*) FROM forensic_logs WHERE logged_at > NOW() - INTERVAL '1 hour') AS recent_events,
        (SELECT COUNT(*) FROM alerts WHERE is_read = FALSE)           AS unread_alerts,
        NOW() AS server_time
    `);
    res.json({
      status:     'OPERATIONAL',
      database:   'CONNECTED',
      ...rows[0],
      uptime_sec: process.uptime().toFixed(0),
    });
  } catch (err) {
    res.status(503).json({
      status:   'DEGRADED',
      database: 'DISCONNECTED',
      error:    err.message,
    });
  }
});

app.get('/api/v1/health', async (req, res) => {
  try {
    const { rows } = await pool.query(`
      SELECT
        (SELECT COUNT(*) FROM managed_nodes)                          AS total_nodes,
        (SELECT COUNT(*) FROM managed_nodes WHERE online = TRUE)      AS online_nodes,
        (SELECT COUNT(*) FROM forensic_logs WHERE logged_at > NOW() - INTERVAL '1 hour') AS recent_events,
        (SELECT COUNT(*) FROM alerts WHERE is_read = FALSE)           AS unread_alerts,
        NOW() AS server_time
    `);
    res.json({
      status:     'OPERATIONAL',
      database:   'CONNECTED',
      ...rows[0],
      uptime_sec: process.uptime().toFixed(0),
    });
  } catch (err) {
    res.status(503).json({ status: 'DEGRADED', database: 'DISCONNECTED', error: err.message });
  }
});

// ================================================================
// OFFLINE NODE SWEEP — Mark nodes offline after 100s silence
// ================================================================
async function sweepOfflineNodes() {
  try {
    const { rowCount } = await pool.query(`
      UPDATE managed_nodes
      SET    online = FALSE
      WHERE  online = TRUE
        AND  last_seen < NOW() - INTERVAL '100 seconds'
    `);
    if (rowCount > 0) {
      console.log(`[SWEEP] Marked ${rowCount} node(s) offline.`);
      io.emit('nodes:sweep', { count: rowCount, timestamp: new Date().toISOString() });
    }
  } catch (err) {
    console.error('[SWEEP] Error:', err.message);
  }
}

// ================================================================
// LAN IP DISCOVERY
// ================================================================
function getLanIp() {
  const nets = os.networkInterfaces();
  for (const iface of Object.values(nets)) {
    for (const net of iface) {
      if (net.family === 'IPv4' && !net.internal) return net.address;
    }
  }
  return '127.0.0.1';
}

// ================================================================
// SERVE REACT DASHBOARD — Static files from Web_Client/dist
// ================================================================
const clientDistDir = path.join(__dirname, '..', 'Web_Client', 'dist');
const fs = require('fs');
if (fs.existsSync(clientDistDir)) {
  app.use(express.static(clientDistDir, { maxAge: '1h' }));
  // All non-API routes → React app (client-side routing)
  app.get('*', (req, res) => {
    if (req.path.startsWith('/api')) return res.status(404).json({ error: 'Not found' });
    res.sendFile(path.join(clientDistDir, 'index.html'));
  });
  console.log('[STATIC] Serving React dashboard from', clientDistDir);
}

// ================================================================
// STARTUP
// ================================================================
async function bootstrap() {
  const dbOk = await testConnection();
  if (!dbOk) {
    console.error('[BOOT] Cannot start without database. Check .env and PostgreSQL service.');
    process.exit(1);
  }

  setInterval(sweepOfflineNodes, 30_000);

  server.listen(PORT, '0.0.0.0', () => {
    const lan = getLanIp();
    console.log('\n╔══════════════════════════════════════════╗');
    console.log('║      RASD ORCHESTRATOR API — ONLINE      ║');
    console.log('╠══════════════════════════════════════════╣');
    console.log(`║  Local   : http://localhost:${PORT}           ║`);
    console.log(`║  LAN     : http://${lan}:${PORT}     ║`);
    console.log('║  Auth    : JWT (24h expiry)               ║');
    console.log('║  Status  : OPERATIONAL                   ║');
    console.log('╚══════════════════════════════════════════╝\n');
  });
}

bootstrap();
