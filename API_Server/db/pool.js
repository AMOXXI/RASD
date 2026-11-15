const { Pool } = require('pg');
require('dotenv').config();

const pool = new Pool({
  host:     process.env.DB_HOST     || 'localhost',
  port:     parseInt(process.env.DB_PORT) || 5432,
  database: process.env.DB_NAME     || 'rasd_security',
  user:     process.env.DB_USER     || 'postgres',
  password: process.env.DB_PASS     || 'admin',
  max:      20,
  idleTimeoutMillis: 30000,
  connectionTimeoutMillis: 5000,
});

pool.on('error', (err) => {
  console.error('[DB] Unexpected pool error:', err.message);
});

// Test connection on startup
async function testConnection() {
  try {
    const client = await pool.connect();
    const res = await client.query('SELECT NOW() AS time, current_database() AS db');
    console.log(`[DB] Connected — ${res.rows[0].db} @ ${res.rows[0].time}`);
    client.release();
    return true;
  } catch (err) {
    console.error('[DB] Connection FAILED:', err.message);
    return false;
  }
}

module.exports = { pool, testConnection };
