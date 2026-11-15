'use strict';
const express  = require('express');
const router   = express.Router();
const { pool } = require('../db/pool');
const { createAlert } = require('./alerts');

// Helper: issue a command to a node and emit to its agent via Socket.IO
async function issueCommand(req, res, nodeId, command, payload = null) {
  try {
    const { rows: nodeRows } = await pool.query(
      'SELECT id, hostname, online FROM managed_nodes WHERE id = $1', [nodeId]
    );
    if (!nodeRows.length) {
      return res.status(404).json({ success: false, error: 'Node not found.' });
    }

    const { rows } = await pool.query(`
      INSERT INTO command_log (node_id, command, payload, status)
      VALUES ($1, $2, $3, 'PENDING')
      RETURNING id
    `, [nodeId, command, payload ? JSON.stringify(payload) : null]);

    const cmdId = rows[0].id;

    const io = req.app.get('io');
    if (io) {
      io.to(`agent:${nodeId}`).emit('command', { cmdId, command, payload });
      io.emit('command:issued', { nodeId, command, cmdId });
    }

    // Generate alert for command
    await createAlert(io, nodeId, 'COMMAND_ISSUED',
      `${command} sent to ${nodeRows[0].hostname || 'Unknown'}`);

    res.json({ success: true, cmdId, command, node: nodeRows[0].hostname });
  } catch (err) {
    console.error(`[CMD] ${command} error:`, err.message);
    res.status(500).json({ success: false, error: err.message });
  }
}

// POST /api/command/restart/:nodeId
router.post('/restart/:nodeId', (req, res) => {
  issueCommand(req, res, req.params.nodeId, 'RESTART');
});

// POST /api/command/shutdown/:nodeId
router.post('/shutdown/:nodeId', (req, res) => {
  issueCommand(req, res, req.params.nodeId, 'SHUTDOWN');
});

// POST /api/command/update-windows/:nodeId
router.post('/update-windows/:nodeId', (req, res) => {
  issueCommand(req, res, req.params.nodeId, 'UPDATE_WINDOWS');
});

// POST /api/command/update-windows-all — issue UPDATE_WINDOWS to every online node
router.post('/update-windows-all', async (req, res) => {
  try {
    const { rows: nodes } = await pool.query(
      `SELECT id, hostname FROM managed_nodes WHERE online = TRUE`
    );

    const io = req.app.get('io');
    let issued = 0;

    for (const node of nodes) {
      const { rows } = await pool.query(`
        INSERT INTO command_log (node_id, command, payload, status)
        VALUES ($1, 'UPDATE_WINDOWS', NULL, 'PENDING')
        RETURNING id
      `, [node.id]);
      const cmdId = rows[0].id;

      if (io) {
        io.to(`agent:${node.id}`).emit('command', { cmdId, command: 'UPDATE_WINDOWS', payload: null });
        io.emit('command:issued', { nodeId: node.id, command: 'UPDATE_WINDOWS', cmdId });
      }
      issued++;
    }

    await createAlert(io, null, 'COMMAND_ISSUED',
      `UPDATE_WINDOWS sent to ${issued} online endpoint(s)`);

    res.json({ success: true, issued });
  } catch (err) {
    console.error('[CMD] update-windows-all error:', err.message);
    res.status(500).json({ success: false, error: err.message });
  }
});

// POST /api/command/block-usb/:nodeId
router.post('/block-usb/:nodeId', async (req, res) => {
  try {
    await pool.query(
      `UPDATE managed_nodes SET usb_status = 'BLOCKED' WHERE id = $1`, [req.params.nodeId]
    );
    issueCommand(req, res, req.params.nodeId, 'BLOCK_USB');
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// POST /api/command/allow-usb/:nodeId
router.post('/allow-usb/:nodeId', async (req, res) => {
  try {
    await pool.query(
      `UPDATE managed_nodes SET usb_status = 'ALLOWED' WHERE id = $1`, [req.params.nodeId]
    );
    issueCommand(req, res, req.params.nodeId, 'ALLOW_USB');
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// POST /api/command/exec-ps/:nodeId  — Execute arbitrary PowerShell
router.post('/exec-ps/:nodeId', (req, res) => {
  const { script } = req.body;
  if (!script) return res.status(400).json({ success: false, error: 'script is required.' });
  issueCommand(req, res, req.params.nodeId, 'EXEC_PS', { script });
});

// POST /api/command/:cmdId/ack  — Agent acknowledges command delivery
router.post('/:cmdId/ack', async (req, res) => {
  const { status, output } = req.body;
  try {
    await pool.query(`
      UPDATE command_log
      SET status = $1, payload = COALESCE(payload, '') || $2, delivered_at = NOW()
      WHERE id = $3
    `, [status || 'DELIVERED', output ? '\nOUTPUT: ' + output : '', req.params.cmdId]);

    const io = req.app.get('io');
    if (io) io.emit('command:ack', { cmdId: req.params.cmdId, status, output });

    res.json({ success: true });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// GET /api/command/log/:nodeId  — Fetch command history
router.get('/log/:nodeId', async (req, res) => {
  try {
    const { rows } = await pool.query(`
      SELECT id, command, payload, status, issued_by, issued_at, delivered_at
      FROM   command_log
      WHERE  node_id = $1
      ORDER  BY issued_at DESC
      LIMIT  100
    `, [req.params.nodeId]);
    res.json({ success: true, log: rows });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

module.exports = router;
