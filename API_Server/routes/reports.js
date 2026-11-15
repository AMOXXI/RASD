const express = require('express');
const router  = express.Router();
const { pool } = require('../db/pool');
const { authenticateToken } = require('./auth');

const defaultFrom = () => new Date(Date.now() - 7 * 86400000).toISOString();
const defaultTo   = () => new Date().toISOString();

// GET /api/v1/reports/summary
router.get('/summary', authenticateToken, async (req, res) => {
  const from = req.query.from || defaultFrom();
  const to   = req.query.to   || defaultTo();
  try {
    const [nodes, alerts, forensics, commands] = await Promise.all([
      pool.query(`SELECT
               COUNT(*)                               AS total,
               COUNT(*) FILTER(WHERE online)          AS online,
               COUNT(*) FILTER(WHERE NOT online)      AS offline,
               COUNT(*) FILTER(WHERE usb_status='BLOCKED') AS usb_blocked
             FROM managed_nodes`),
      pool.query(`SELECT
               COUNT(*)                               AS total,
               COUNT(*) FILTER(WHERE NOT is_read)     AS unread,
               COUNT(*) FILTER(WHERE alert_type LIKE 'CRITICAL%') AS critical
             FROM alerts WHERE created_at BETWEEN $1 AND $2`, [from, to]),
      pool.query(`SELECT
               COUNT(*)                               AS total,
               COUNT(*) FILTER(WHERE severity='CRITICAL') AS critical,
               COUNT(*) FILTER(WHERE severity='WARN')     AS warn,
               COUNT(*) FILTER(WHERE severity='INFO')     AS info
             FROM forensic_logs WHERE logged_at BETWEEN $1 AND $2`, [from, to]),
      pool.query(`SELECT
               COUNT(*)                               AS total,
               COUNT(*) FILTER(WHERE status='DELIVERED') AS delivered,
               COUNT(*) FILTER(WHERE status='FAILED')    AS failed,
               COUNT(*) FILTER(WHERE status='PENDING')   AS pending
             FROM command_log WHERE issued_at BETWEEN $1 AND $2`, [from, to]),
    ]);
    res.json({
      success: true,
      period:    { from, to },
      nodes:     nodes.rows[0],
      alerts:    alerts.rows[0],
      forensics: forensics.rows[0],
      commands:  commands.rows[0],
    });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// GET /api/v1/reports/activity
router.get('/activity', authenticateToken, async (req, res) => {
  const from  = req.query.from  || defaultFrom();
  const to    = req.query.to    || defaultTo();
  const limit = Math.min(parseInt(req.query.limit) || 5000, 10000);
  try {
    const { rows } = await pool.query(`
      (SELECT fl.logged_at AS time, 'FORENSIC' AS type, fl.severity,
              mn.hostname, mn.ip_address::text, fl.event_type AS event, fl.details AS details
       FROM forensic_logs fl
       LEFT JOIN managed_nodes mn ON fl.node_id = mn.id
       WHERE fl.logged_at BETWEEN $1 AND $2)
      UNION ALL
      (SELECT cl.issued_at, 'COMMAND', 'INFO',
              mn.hostname, mn.ip_address::text, cl.command, cl.status
       FROM command_log cl
       LEFT JOIN managed_nodes mn ON cl.node_id = mn.id
       WHERE cl.issued_at BETWEEN $1 AND $2)
      ORDER BY time DESC
      LIMIT $3
    `, [from, to, limit]);
    res.json({ success: true, data: rows, period: { from, to } });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// GET /api/v1/reports/alerts
router.get('/alerts', authenticateToken, async (req, res) => {
  const from  = req.query.from  || defaultFrom();
  const to    = req.query.to    || defaultTo();
  try {
    const { rows } = await pool.query(`
      SELECT a.alert_id, a.alert_type, a.message, a.is_read, a.created_at,
             mn.hostname, mn.ip_address::text
      FROM alerts a
      LEFT JOIN managed_nodes mn ON a.node_id = mn.id
      WHERE a.created_at BETWEEN $1 AND $2
      ORDER BY a.created_at DESC
      LIMIT 5000
    `, [from, to]);
    res.json({ success: true, data: rows, period: { from, to } });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

// GET /api/v1/reports/endpoints
router.get('/endpoints', authenticateToken, async (req, res) => {
  try {
    const { rows } = await pool.query(`
      SELECT
        mn.hostname, mn.ip_address::text, mn.mac_address,
        mn.os_version, mn.agent_version, mn.usb_status,
        mn.online, mn.last_seen, mn.enrolled_at,
        COUNT(DISTINCT fl.id)                               AS total_events,
        COUNT(DISTINCT fl.id) FILTER(WHERE fl.severity='CRITICAL') AS critical_events,
        COUNT(DISTINCT cl.id)                               AS total_commands,
        COUNT(DISTINCT cl.id) FILTER(WHERE cl.status='FAILED') AS failed_commands
      FROM managed_nodes mn
      LEFT JOIN forensic_logs fl ON fl.node_id = mn.id
      LEFT JOIN command_log   cl ON cl.node_id = mn.id
      GROUP BY mn.id
      ORDER BY mn.online DESC, mn.last_seen DESC
    `);
    res.json({ success: true, data: rows });
  } catch (err) {
    res.status(500).json({ success: false, error: err.message });
  }
});

module.exports = router;
