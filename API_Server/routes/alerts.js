'use strict';
const express  = require('express');
const router   = express.Router();
const { pool } = require('../db/pool');

// ─── Helper: Create Alert + Emit via Socket.IO ──────────────────────
async function createAlert(ioOrReq, nodeId, alertType, message) {
  try {
    const { rows } = await pool.query(
      `INSERT INTO alerts (node_id, alert_type, message)
       VALUES ($1, $2, $3) RETURNING *`,
      [nodeId, alertType, message]
    );
    const alert = rows[0];

    // Get the io instance
    const io = ioOrReq && ioOrReq.app ? ioOrReq.app.get('io') : ioOrReq;
    if (io && typeof io.emit === 'function') {
      io.emit('alert:new', alert);
    }

    return alert;
  } catch (err) {
    console.error('[ALERTS] Create error:', err.message);
    return null;
  }
}

// ─── GET / — List all alerts ─────────────────────────────────────────
router.get('/', async (req, res) => {
  try {
    const limit = parseInt(req.query.limit) || 200;
    const unreadOnly = req.query.unread === 'true';

    let query = `
      SELECT a.alert_id, a.node_id, a.alert_type, a.message, a.is_read, a.created_at,
             n.hostname, n.ip_address::text
      FROM   alerts a
      LEFT   JOIN managed_nodes n ON n.id = a.node_id
    `;
    const params = [];

    if (unreadOnly) {
      query += ' WHERE a.is_read = FALSE';
    }

    query += ' ORDER BY a.created_at DESC LIMIT $' + (params.length + 1);
    params.push(limit);

    const { rows } = await pool.query(query, params);
    res.json({ success: true, alerts: rows });
  } catch (err) {
    console.error('[ALERTS] GET error:', err.message);
    res.status(500).json({ success: false, error: err.message });
  }
});

// ─── GET /unread-count ───────────────────────────────────────────────
router.get('/unread-count', async (req, res) => {
  try {
    const { rows } = await pool.query('SELECT COUNT(*) AS count FROM alerts WHERE is_read = FALSE');
    res.json({ success: true, count: parseInt(rows[0].count) });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// ─── PUT /:id/read — Mark alert as read ──────────────────────────────
router.put('/:id/read', async (req, res) => {
  try {
    await pool.query('UPDATE alerts SET is_read = TRUE WHERE alert_id = $1', [req.params.id]);
    res.json({ success: true });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// ─── PUT /mark-all-read — Mark all alerts as read ────────────────────
router.put('/mark-all-read', async (req, res) => {
  try {
    const { rowCount } = await pool.query('UPDATE alerts SET is_read = TRUE WHERE is_read = FALSE');
    res.json({ success: true, updated: rowCount });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

module.exports = router;
module.exports.createAlert = createAlert;
