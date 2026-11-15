import { useState, useEffect, useRef } from 'react';
import {
  Shield, MonitorDot, Activity, Database,
  Bell, WifiOff, ChevronUp, AlertTriangle,
} from 'lucide-react';
import FleetTable from './FleetTable.jsx';

// ---- Stat card ----
function StatCard({ icon: Icon, label, value, sub, accent }) {
  return (
    <div className={`panel p-5 flex items-start gap-4 ${accent ? 'border-accent/20 bg-accent/5' : ''}`}>
      <div className={`w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 ${accent ? 'bg-accent/15' : 'bg-white/5'}`}>
        <Icon className={`w-5 h-5 ${accent ? 'text-accent' : 'text-ghost'}`} />
      </div>
      <div>
        <p className="text-2xl font-bold text-white font-mono">{value ?? '—'}</p>
        <p className="text-xs text-dim font-mono mt-0.5">{label}</p>
        {sub && <p className="text-[11px] text-muted mt-0.5">{sub}</p>}
      </div>
    </div>
  );
}

// ---- Alert toast strip ----
function AlertBanner({ alerts }) {
  const [dismissed, setDismissed] = useState(false);
  const latest = alerts[0];
  if (!latest || dismissed) return null;

  return (
    <div className="flex items-center gap-3 px-4 py-3 bg-offline/10 border border-offline/30 rounded-lg animate-slide-up">
      <AlertTriangle className="w-4 h-4 text-offline flex-shrink-0" />
      <div className="flex-1 min-w-0">
        <span className="text-xs font-mono text-offline font-semibold">FORENSIC ALERT — </span>
        <span className="text-xs font-mono text-ghost">{latest.event_type}: {latest.file_name || latest.details}</span>
      </div>
      <button onClick={() => setDismissed(true)} className="text-dim hover:text-white transition-colors">
        <ChevronUp className="w-4 h-4" />
      </button>
    </div>
  );
}

// ================================================================
// DASHBOARD
// ================================================================
export default function Dashboard({ nodes, setNodes, healthData, alerts }) {
  const onlineCount  = nodes.filter(n => n.online).length;
  const offlineCount = nodes.length - onlineCount;
  const blockedUsb   = nodes.filter(n => n.usb_status === 'BLOCKED').length;
  const criticalAlerts = nodes.reduce((s, n) => s + (parseInt(n.critical_alerts) || 0), 0);

  // Live clock
  const [time, setTime] = useState(new Date());
  useEffect(() => {
    const t = setInterval(() => setTime(new Date()), 1000);
    return () => clearInterval(t);
  }, []);

  return (
    <div className="min-h-screen bg-void flex flex-col">
      {/* Scanline */}
      <div className="scanline" aria-hidden />

      {/* ---- TOP NAV ---- */}
      <header className="sticky top-0 z-40 border-b border-border bg-void/90 backdrop-blur-md flex-shrink-0">
        <div className="max-w-[1600px] mx-auto px-6 h-14 flex items-center justify-between">
          {/* Brand */}
          <div className="flex items-center gap-3">
            <div className="w-7 h-7 rounded-md bg-accent/10 border border-accent/30 flex items-center justify-center">
              <Shield className="w-3.5 h-3.5 text-accent" />
            </div>
            <div>
              <span className="text-sm font-mono font-bold text-white tracking-wider">RASD</span>
              <span className="text-xs text-dim font-mono ml-2 hidden sm:inline">OPERATIONS CENTER</span>
            </div>
          </div>

          {/* Live indicators */}
          <div className="flex items-center gap-5">
            {/* Connectivity */}
            <div className="flex items-center gap-1.5">
              <span className="w-1.5 h-1.5 rounded-full bg-online glow-dot animate-pulse-slow" />
              <span className="text-xs font-mono text-dim hidden sm:inline">API LIVE</span>
            </div>
            {/* DB status */}
            <div className="flex items-center gap-1.5">
              <Database className="w-3 h-3 text-online" />
              <span className="text-xs font-mono text-dim hidden sm:inline">DB</span>
            </div>
            {/* Alerts badge */}
            {criticalAlerts > 0 && (
              <div className="flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-offline/10 border border-offline/30">
                <Bell className="w-3 h-3 text-offline" />
                <span className="text-xs font-mono text-offline">{criticalAlerts}</span>
              </div>
            )}
            {/* Clock */}
            <span className="text-xs font-mono text-muted hidden md:block">
              {time.toLocaleTimeString()}
            </span>
          </div>
        </div>
      </header>

      {/* ---- MAIN CONTENT ---- */}
      <main className="flex-1 max-w-[1600px] mx-auto w-full px-6 py-6 space-y-6">

        {/* Alert banner */}
        {alerts.length > 0 && <AlertBanner alerts={alerts} />}

        {/* ---- STAT CARDS ---- */}
        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          <StatCard
            icon={MonitorDot}
            label="ONLINE NODES"
            value={onlineCount}
            sub={`${offlineCount} offline`}
            accent
          />
          <StatCard
            icon={WifiOff}
            label="OFFLINE NODES"
            value={offlineCount}
            sub={`${nodes.length} total enrolled`}
          />
          <StatCard
            icon={Activity}
            label="USB BLOCKED"
            value={blockedUsb}
            sub={`${nodes.length - blockedUsb} allowed`}
          />
          <StatCard
            icon={AlertTriangle}
            label="CRITICAL ALERTS"
            value={criticalAlerts}
            sub="Last 24 hours"
          />
        </div>

        {/* ---- FLEET TABLE ---- */}
        <FleetTable nodes={nodes} setNodes={setNodes} />

        {/* ---- SYSTEM INFO STRIP ---- */}
        <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-3 panel text-xs font-mono text-muted">
          <div className="flex items-center gap-4">
            <span>UPTIME: {healthData?.uptime_sec}s</span>
            <span className="text-border">|</span>
            <span>DB: {healthData?.database}</span>
            <span className="text-border">|</span>
            <span>EVENTS (1h): {healthData?.recent_events ?? 0}</span>
          </div>
          <span>RASD ENTERPRISE v2.0</span>
        </div>
      </main>
    </div>
  );
}
