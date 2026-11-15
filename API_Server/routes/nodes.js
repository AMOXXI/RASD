'use strict';
const express  = require('express');
const router   = express.Router();
const { pool } = require('../db/pool');
const { createAlert } = require('./alerts');

// GET /api/nodes  — Full fleet list
router.get('/', async (req, res) => {
  try {
    const { rows } = await pool.query(`
      SELECT
        n.id, n.hostname, n.mac_address, n.ip_address::text,
        n.os_version, n.agent_version, n.usb_status, n.online,
        n.last_seen, n.enrolled_at, n.is_admin,
        COUNT(f.id) FILTER (WHERE f.severity = 'CRITICAL') AS critical_alerts,
        COUNT(f.id) AS total_events
      FROM  managed_nodes n
      LEFT  JOIN forensic_logs f ON f.node_id = n.id
      GROUP BY n.id
      ORDER BY n.online DESC, n.last_seen DESC
    `);
    res.json({ success: true, nodes: rows });
  } catch (err) {
    console.error('[NODES] GET /api/nodes error:', err.message);
    res.status(500).json({ success: false, error: err.message });
  }
});

// POST /api/nodes/heartbeat  — Agent check-in / self-healing IP
router.post('/heartbeat', async (req, res) => {
  const { mac_address, ip_address, hostname, os_version, agent_version, usb_status } = req.body;

  if (!mac_address || !ip_address) {
    return res.status(400).json({ success: false, error: 'mac_address and ip_address are required.' });
  }

  try {
    // Check if this node already exists
    const { rows: existing } = await pool.query(
      'SELECT id, ip_address::text AS ip FROM managed_nodes WHERE mac_address = $1',
      [mac_address]
    );
    const isNewNode = existing.length === 0;
    const oldIp = existing.length > 0 ? existing[0].ip : null;

    // Upsert: insert new node or update existing
    const { rows } = await pool.query(`
      INSERT INTO managed_nodes
        (mac_address, ip_address, hostname, os_version, agent_version, usb_status, online, last_seen)
      VALUES
        ($1, $2::inet, $3, $4, $5, $6, TRUE, NOW())
      ON CONFLICT (mac_address) DO UPDATE SET
        ip_address    = EXCLUDED.ip_address,
        hostname      = CASE WHEN managed_nodes.is_admin THEN managed_nodes.hostname
                             ELSE COALESCE(EXCLUDED.hostname, managed_nodes.hostname) END,
        os_version    = COALESCE(EXCLUDED.os_version,    managed_nodes.os_version),
        agent_version = COALESCE(EXCLUDED.agent_version, managed_nodes.agent_version),
        usb_status    = COALESCE(EXCLUDED.usb_status,    managed_nodes.usb_status),
        online        = TRUE,
        last_seen     = NOW()
      RETURNING *
    `, [mac_address, ip_address, hostname, os_version, agent_version, usb_status || 'ALLOWED']);

    const node = rows[0];

    // Emit real-time update
    const io = req.app.get('io');
    if (io) io.emit('node:update', { ...node, ip_address: node.ip_address.toString() });

    // Generate alerts for important events
    if (isNewNode) {
      await createAlert(io, node.id, 'NEW_DEVICE',
        `New endpoint enrolled: ${hostname || 'Unknown'} (${ip_address})`);
    } else if (oldIp && oldIp !== ip_address) {
      await createAlert(io, node.id, 'IP_CHANGED',
        `IP changed for ${hostname || 'Unknown'}: ${oldIp} → ${ip_address}`);
    }

    // Return any pending commands
    const { rows: pending } = await pool.query(`
      SELECT id, command, payload FROM command_log
      WHERE node_id = $1 AND status = 'PENDING'
      ORDER BY issued_at ASC
    `, [node.id]);

    res.json({ success: true, node_id: node.id, pending_commands: pending });
  } catch (err) {
    console.error('[NODES] Heartbeat error:', err.message);
    res.status(500).json({ success: false, error: err.message });
  }
});

// POST /api/nodes/register-admin  — the admin machine self-registers
router.post('/register-admin', async (req, res) => {
  const { hostname, ip, os, mac } = req.body;
  if (!mac || !ip) {
    return res.status(400).json({ success: false, error: 'mac and ip are required.' });
  }

  // Normalise to a single "[Admin] " prefix regardless of what was sent
  const cleanHost = (hostname || 'Unknown').replace(/^\[Admin\]\s*/i, '');
  const adminHost = '[Admin] ' + cleanHost;

  try {
    const { rows: existing } = await pool.query(
      'SELECT id FROM managed_nodes WHERE mac_address = $1', [mac]
    );

    let node;
    if (existing.length > 0) {
      const { rows } = await pool.query(`
        UPDATE managed_nodes
        SET hostname   = $2,
            ip_address = $3::inet,
            os_version = COALESCE($4, os_version),
            is_admin   = TRUE,
            online     = TRUE,
            last_seen  = NOW()
        WHERE mac_address = $1
        RETURNING *
      `, [mac, adminHost, ip, os]);
      node = rows[0];
    } else {
      const { rows } = await pool.query(`
        INSERT INTO managed_nodes
          (mac_address, ip_address, hostname, os_version, agent_version, usb_status, online, last_seen, is_admin)
        VALUES ($1, $2::inet, $3, $4, 'admin', 'ALLOWED', TRUE, NOW(), TRUE)
        RETURNING *
      `, [mac, ip, adminHost, os]);
      node = rows[0];
    }

    const io = req.app.get('io');
    if (io) io.emit('node:update', { ...node, ip_address: node.ip_address.toString() });

    res.json({ success: true, node });
  } catch (err) {
    console.error('[NODES] register-admin error:', err.message);
    res.status(500).json({ success: false, error: err.message });
  }
});

// POST /api/nodes/:id/forensic  — Agent submits forensic event
router.post('/:id/forensic', async (req, res) => {
  const { id } = req.params;
  const { event_type, file_name, file_hash, file_size, source_url, severity, details } = req.body;

  try {
    const { rows } = await pool.query(`
      INSERT INTO forensic_logs
        (node_id, event_type, file_name, file_hash, file_size, source_url, severity, details)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
      RETURNING *
    `, [id, event_type, file_name, file_hash, file_size, source_url, severity || 'INFO', details]);

    const log = rows[0];
    const io = req.app.get('io');
    if (io) io.emit('forensic:event', log);

    // Auto-generate alert for CRITICAL events
    if (severity === 'CRITICAL') {
      await createAlert(io, id, 'CRITICAL_FORENSIC',
        `CRITICAL: ${event_type} detected — ${file_name || 'unknown file'} (Hash: ${file_hash || 'N/A'})`);
    }

    res.json({ success: true, log });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// GET /api/nodes/:id/forensics  — Get forensic log for a node
router.get('/:id/forensics', async (req, res) => {
  const { id } = req.params;
  const limit = parseInt(req.query.limit) || 50;
  try {
    const { rows } = await pool.query(`
      SELECT * FROM forensic_logs
      WHERE node_id = $1
      ORDER BY logged_at DESC
      LIMIT $2
    `, [id, limit]);
    res.json({ success: true, events: rows });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// GET /api/nodes/forensics/all — Get ALL forensic logs
router.get('/forensics/all', async (req, res) => {
  const limit = parseInt(req.query.limit) || 200;
  const severity = req.query.severity;
  try {
    let query = `
      SELECT f.*, n.hostname, n.ip_address::text
      FROM forensic_logs f
      LEFT JOIN managed_nodes n ON n.id = f.node_id
    `;
    const params = [];

    if (severity && severity !== 'ALL') {
      query += ' WHERE f.severity = $1';
      params.push(severity);
    }

    query += ` ORDER BY f.logged_at DESC LIMIT $${params.length + 1}`;
    params.push(limit);

    const { rows } = await pool.query(query, params);
    res.json({ success: true, events: rows });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

module.exports = router;
