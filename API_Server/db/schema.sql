-- ============================================================
-- RASD ENTERPRISE SECURITY DATABASE SCHEMA v2.0
-- Run: psql -U postgres -d rasd_security -f schema.sql
-- ============================================================

-- ---- MANAGED NODES (Fleet Table) ----
CREATE TABLE IF NOT EXISTS managed_nodes (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    hostname      VARCHAR(255)  NOT NULL,
    mac_address   VARCHAR(17)   NOT NULL UNIQUE,
    ip_address    INET          NOT NULL,
    os_version    VARCHAR(255),
    agent_version VARCHAR(50)   DEFAULT '2.0.0',
    usb_status    VARCHAR(10)   NOT NULL DEFAULT 'ALLOWED'
                    CHECK (usb_status IN ('ALLOWED', 'BLOCKED')),
    online        BOOLEAN       NOT NULL DEFAULT FALSE,
    last_seen     TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    enrolled_at   TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

-- Admin machine flag (added post-v2.0; safe to re-run)
ALTER TABLE managed_nodes ADD COLUMN IF NOT EXISTS is_admin BOOLEAN DEFAULT false;

-- ---- FORENSIC EVENT LOG ----
CREATE TABLE IF NOT EXISTS forensic_logs (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    node_id       UUID          NOT NULL REFERENCES managed_nodes(id) ON DELETE CASCADE,
    event_type    VARCHAR(50)   NOT NULL,
    file_name     VARCHAR(500),
    file_hash     VARCHAR(64),
    file_size     BIGINT,
    source_url    TEXT,
    severity      VARCHAR(10)   NOT NULL DEFAULT 'INFO'
                    CHECK (severity IN ('INFO','WARN','CRITICAL')),
    details       TEXT,
    logged_at     TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

-- ---- COMMAND AUDIT LOG ----
CREATE TABLE IF NOT EXISTS command_log (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    node_id       UUID          NOT NULL REFERENCES managed_nodes(id) ON DELETE CASCADE,
    command       VARCHAR(100)  NOT NULL,
    payload       TEXT,
    issued_by     VARCHAR(100)  DEFAULT 'ADMIN',
    status        VARCHAR(20)   NOT NULL DEFAULT 'PENDING'
                    CHECK (status IN ('PENDING','DELIVERED','FAILED')),
    issued_at     TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    delivered_at  TIMESTAMPTZ
);

-- ---- USERS (Authentication) ----
CREATE TABLE IF NOT EXISTS users (
    user_id       SERIAL PRIMARY KEY,
    username      VARCHAR(50) UNIQUE NOT NULL,
    email         VARCHAR(100) UNIQUE NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    role          VARCHAR(20) DEFAULT 'admin',
    created_at    TIMESTAMPTZ DEFAULT NOW()
);

-- ---- ALERTS ----
CREATE TABLE IF NOT EXISTS alerts (
    alert_id      SERIAL PRIMARY KEY,
    node_id       UUID REFERENCES managed_nodes(id) ON DELETE CASCADE,
    alert_type    VARCHAR(50) NOT NULL,
    message       TEXT,
    is_read       BOOLEAN DEFAULT FALSE,
    created_at    TIMESTAMPTZ DEFAULT NOW()
);

-- ---- INDEXES ----
CREATE INDEX IF NOT EXISTS idx_nodes_mac       ON managed_nodes(mac_address);
CREATE INDEX IF NOT EXISTS idx_nodes_online    ON managed_nodes(online);
CREATE INDEX IF NOT EXISTS idx_forensic_node   ON forensic_logs(node_id);
CREATE INDEX IF NOT EXISTS idx_forensic_time   ON forensic_logs(logged_at DESC);
CREATE INDEX IF NOT EXISTS idx_cmd_node        ON command_log(node_id);
CREATE INDEX IF NOT EXISTS idx_cmd_status      ON command_log(status);
CREATE INDEX IF NOT EXISTS idx_alerts_node     ON alerts(node_id);
CREATE INDEX IF NOT EXISTS idx_alerts_unread   ON alerts(is_read) WHERE is_read = FALSE;

-- ---- OFFLINE SWEEP FUNCTION ----
CREATE OR REPLACE FUNCTION sweep_offline_nodes()
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    UPDATE managed_nodes
    SET    online = FALSE
    WHERE  online = TRUE
      AND  last_seen < NOW() - INTERVAL '100 seconds';
END;
$$;

-- ---- DEFAULT ADMIN USER ----
-- Password: admin123 (bcrypt hash)
INSERT INTO users (username, email, password_hash, role)
VALUES ('admin', 'admin@rasd.local',
  '$2a$10$92IXUNpkjO0rOQ5byMi.Ye4oKoEa3Ro9llC/.og/at2.uheWG/igi', 'admin')
ON CONFLICT (username) DO NOTHING;
