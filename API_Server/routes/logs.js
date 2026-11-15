const express = require('express');
const router  = express.Router();
const { pool } = require('../db/pool');
const { authenticateToken } = require('./auth');

// GET /api/v1/logs
// Query params: limit, type (all|forensic|command|alert), severity (ALL|INFO|WARN|CRITICAL), from, to
router.get('/', authenticateToken, async (req, res) => {
  const limit    = Math.min(parseInt(req.query.limit) || 500, 2000);
  const type     = req.query.type     || 'all';
  const severity = (req.query.severity || 'ALL').toUpperCase();
  const from     = req.query.from || new Date(Date.now() - 7 * 86400000).toISOString();
  const to       = req.query.to   || new Date().toISOString();

  try {
    const logs = [];

    if (type === 'all' || type === 'forensic') {
      const sevClause = severity === 'ALL' ? '' : `AND fl.severity = '${severity}'`;
      const { rows } = await pool.query(`
        SELECT
          fl.id::text AS id,
          'forensic'   AS log_type,
          fl.severity,
          fl.event_type AS title,
          COALESCE(fl.details, fl.file_name, '') AS message,
          fl.logged_at  AS timestamp,
          mn.hostname,
          mn.ip_address::text AS ip_address
        FROM forensic_logs fl
        LEFT JOIN managed_nodes mn ON fl.node_id = mn.id
        WHERE fl.logged_at BETWEEN $1 AND $2 ${sevClause}
        ORDER BY fl.logged_at DESC
        LIMIT $3
      `, [from, to, limit]);
      logs.push(...rows);
    }

    if (type === 'all' || type === 'command') {
      const { rows } = await pool.query(`
        SELECT
          cl.id::text  AS id,
          'command'    AS log_type,
          CASE cl.status WHEN 'FAILED' THEN 'WARN' ELSE 'INFO' END AS severity,
          cl.command   AS title,
          cl.status    AS message,
          cl.issued_at AS timestamp,
          mn.hostname,
          mn.ip_address::text AS ip_address
        FROM command_log cl
        LEFT JOIN managed_nodes mn ON cl.node_id = mn.id
        WHERE cl.issued_at BETWEEN $1 AND $2
        ORDER BY cl.issued_at DESC
        LIMIT $3
      `, [from, to, limit]);
      logs.push(...rows);
    }

    if (type === 'all' || type === 'alert') {
      const { rows } = await pool.query(`
        SELECT
          a.alert_id::text AS id,
          'alert'          AS log_type,
          CASE WHEN a.alert_type LIKE 'CRITICAL%' THEN 'CRITICAL' ELSE 'WARN' END AS severity,
          a.alert_type AS title,
          a.message,
          a.created_at AS timestamp,
          mn.hostname,
          mn.ip_address::text AS ip_address
        FROM alerts a
        LEFT JOIN managed_nodes mn ON a.node_id = mn.id
        WHERE a.created_at BETWEEN $1 AND $2
        ORDER BY a.created_at DESC
        LIMIT $3
      `, [from, to, limit]);
      logs.push(...rows);
    }

    logs.sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));
    res.json({ success: true, logs: logs.slice(0, limit), total: logs.length });
  } catch (err) {
    console.error('logs route error:', err);
    res.status(500).json({ success: false, error: err.message });
  }
});

module.exports = router;
