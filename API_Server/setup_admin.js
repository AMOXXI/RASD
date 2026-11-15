/**
 * RASD First-Run Setup Script
 * Run: node setup_admin.js
 * Creates the admin user with password "admin123" using a fresh bcrypt hash.
 * Safe to run multiple times (uses ON CONFLICT DO UPDATE).
 */
'use strict';
require('dotenv').config();

const { Pool } = require('pg');
const bcrypt   = require('bcryptjs');

const pool = new Pool({
  host:     process.env.DB_HOST     || 'localhost',
  port:     parseInt(process.env.DB_PORT) || 5432,
  database: process.env.DB_NAME     || 'rasd_security',
  user:     process.env.DB_USER     || 'postgres',
  password: process.env.DB_PASS     || 'admin123',
});

async function main() {
  let client;
  try {
    client = await pool.connect();
    console.log('[SETUP] Connected to database.');

    const hash = bcrypt.hashSync('admin123', 10);
    console.log('[SETUP] Generated admin password hash.');

    await client.query(`
      INSERT INTO users (username, email, password_hash, role)
      VALUES ('admin', 'admin@rasd.local', $1, 'admin')
      ON CONFLICT (username) DO UPDATE
        SET password_hash = EXCLUDED.password_hash,
            email         = EXCLUDED.email,
            role          = EXCLUDED.role
    `, [hash]);

    console.log('[SETUP] ✓ Admin user created/updated. Username: admin | Password: admin123');
    console.log('[SETUP] Setup complete. You can now start the server with: node server.js');
  } catch (err) {
    console.error('[SETUP] Error:', err.message);
    process.exit(1);
  } finally {
    if (client) client.release();
    await pool.end();
  }
}

main();
