import React, { useState, useEffect } from 'react';
import { Settings2, Activity, Database, User, Info, Shield, RefreshCw } from 'lucide-react';
import { getHealth } from '../services/api';
import { useApp } from '../App';

function Section({ title, icon: Icon, children }) {
  return (
    <div className="card" style={{marginBottom:16}}>
      <div className="card-header"><Icon size={14} style={{color:'var(--c-accent)'}}/><span className="card-title">{title}</span></div>
      <div className="card-body">{children}</div>
    </div>
  );
}

function Row({ label, value, mono, hint }) {
  return (
    <div style={{display:'flex',justifyContent:'space-between',alignItems:'center',padding:'8px 0',borderBottom:'1px solid var(--c-border)'}}>
      <div>
        <div style={{fontSize:13,color:'var(--c-text)'}}>{label}</div>
        {hint && <div style={{fontSize:11,color:'var(--c-dim)',marginTop:1}}>{hint}</div>}
      </div>
      <div className={mono?'mono':''} style={{fontSize:13,fontWeight:600,color:'var(--c-muted)'}}>{value||'—'}</div>
    </div>
  );
}

export default function SettingsPage() {
  const { theme, toggleTheme, toast } = useApp();
  const [health,  setHealth]  = useState(null);
  const [loading, setLoading] = useState(true);
  const user = (() => { try { return JSON.parse(localStorage.getItem('user')||'{}'); } catch { return {}; } })();

  const loadHealth = async () => {
    try { setHealth(await getHealth()); }
    catch {} setLoading(false);
  };

  useEffect(() => { loadHealth(); }, []);

  const handleLogout = () => {
    localStorage.removeItem('token'); localStorage.removeItem('user');
    window.location.href = '/login';
  };

  const uptime = health?.uptime_sec ? `${Math.floor(health.uptime_sec/3600)}h ${Math.floor((health.uptime_sec%3600)/60)}m ${health.uptime_sec%60}s` : '—';

  return (
    <div>
      <div className="page-header">
        <div><h1 className="page-title">Settings</h1><p className="page-sub">Platform configuration and preferences</p></div>
        <button className="btn btn-ghost btn-sm" onClick={loadHealth}><RefreshCw size={13}/></button>
      </div>

      {/* System Status */}
      <Section title="System Status" icon={Activity}>
        {loading ? <div className="spinner"/> : (
          <>
            <Row label="API Status"      value={health?.status}    mono hint="HTTP server health"/>
            <Row label="Database"        value={health?.database}  mono hint="PostgreSQL connection"/>
            <Row label="Uptime"          value={uptime}            mono hint="Time since last restart"/>
            <Row label="Total Endpoints" value={String(health?.total_nodes??0)} mono/>
            <Row label="Online Now"      value={String(health?.online_nodes??0)} mono/>
            <Row label="Recent Events"   value={String(health?.recent_events??0)} mono hint="Last 1 hour"/>
            <Row label="Unread Alerts"   value={String(health?.unread_alerts??0)} mono/>
          </>
        )}
      </Section>

      {/* Appearance */}
      <Section title="Appearance" icon={Settings2}>
        <div style={{display:'flex',justifyContent:'space-between',alignItems:'center',padding:'8px 0'}}>
          <div>
            <div style={{fontSize:13,color:'var(--c-text)'}}>Theme</div>
            <div style={{fontSize:11,color:'var(--c-dim)'}}>Currently: {theme === 'dark' ? 'Dark Mode' : 'Light Mode'}</div>
          </div>
          <button className="btn btn-ghost" onClick={toggleTheme}>
            {theme==='dark' ? '☀ Switch to Light' : '◑ Switch to Dark'}
          </button>
        </div>
      </Section>

      {/* Account */}
      <Section title="Account" icon={User}>
        <Row label="Username" value={user.username} hint="Your login name"/>
        <Row label="Email"    value={user.email || 'Not set'} hint="Contact email"/>
        <Row label="Role"     value={user.role}     mono hint="Access level"/>
        <div style={{marginTop:16}}>
          <button className="btn btn-danger btn-sm" onClick={handleLogout}>Sign Out</button>
        </div>
      </Section>

      {/* Network */}
      <Section title="Network Configuration" icon={Database}>
        <Row label="API Server"      value="localhost:3000"   mono hint="Node.js Express API"/>
        <Row label="TCP Relay"       value="0.0.0.0:3001"    mono hint="Agent command bridge"/>
        <Row label="UDP Discovery"   value="broadcast:3002"  mono hint="Admin IP announcement (30s interval)"/>
        <Row label="Database"        value="PostgreSQL :5432" mono hint="rasd_security database"/>
        <Row label="WebSocket"       value="Socket.IO"        mono hint="Real-time event bus"/>
      </Section>

      {/* About */}
      <Section title="About" icon={Info}>
        <Row label="Platform"     value="RASD Operations Ecosystem"/>
        <Row label="Version"      value="2.0.0" mono/>
        <Row label="Developer"    value="Amir Moneer Ali Al-Dubai"/>
        <Row label="University"   value="Ar-Rasheed Smart University, Sana'a"/>
        <Row label="Department"   value="Cybersecurity — 4th Year"/>
        <Row label="Project Type" value="Graduation Project 2026"/>
        <div style={{marginTop:16,padding:'12px 16px',background:'var(--c-accent-bg)',border:'1px solid rgba(249,115,22,0.3)',borderRadius:6}}>
          <div style={{display:'flex',gap:10,alignItems:'flex-start'}}>
            <Shield size={16} style={{color:'var(--c-accent)',flexShrink:0,marginTop:1}}/>
            <div style={{fontSize:12,color:'var(--c-muted)',lineHeight:1.6}}>
              RASD is a remote administration and endpoint security platform for monitoring and managing
              enterprise Windows workstations. Built as a full-stack graduation project demonstrating
              real-time agent communication, forensic monitoring, and centralized command & control.
            </div>
          </div>
        </div>
      </Section>
    </div>
  );
}
