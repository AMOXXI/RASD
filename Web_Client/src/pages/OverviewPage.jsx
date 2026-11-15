import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { Monitor, Bell, Shield, Activity, RefreshCw } from 'lucide-react';
import { getHealth, getNodes, getAlerts } from '../services/api';
import { useApp } from '../App';

function StatCard({ label, value, sub, color }) {
  return (
    <div className={`stat-card ${color}`}>
      <div className="stat-label">{label}</div>
      <div className="stat-value mono">{value ?? '—'}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}

function SevBadge({ sev }) {
  const cls = sev === 'CRITICAL' ? 'badge-critical' : sev === 'WARN' ? 'badge-warn' : 'badge-info';
  return <span className={`badge ${cls}`}>{sev}</span>;
}

export default function OverviewPage() {
  const { socket } = useApp();
  const navigate = useNavigate();
  const [health,  setHealth]  = useState(null);
  const [nodes,   setNodes]   = useState([]);
  const [alerts,  setAlerts]  = useState([]);
  const [loading, setLoading] = useState(true);
  const [lastRefresh, setLastRefresh] = useState(new Date());

  const refresh = useCallback(async () => {
    try {
      const [h, n, a] = await Promise.all([getHealth(), getNodes(), getAlerts(10)]);
      setHealth(h);
      setNodes(n.nodes || []);
      setAlerts(a.alerts || []);
      setLastRefresh(new Date());
    } catch {}
    setLoading(false);
  }, []);

  useEffect(() => { refresh(); const iv = setInterval(refresh, 10000); return () => clearInterval(iv); }, [refresh]);

  useEffect(() => {
    if (!socket) return;
    const onUpdate = () => refresh();
    socket.on('node:update', onUpdate);
    socket.on('nodes:sweep', onUpdate);
    socket.on('alert:new',   onUpdate);
    return () => { socket.off('node:update', onUpdate); socket.off('nodes:sweep', onUpdate); socket.off('alert:new', onUpdate); };
  }, [socket, refresh]);

  const total   = nodes.length;
  const online  = nodes.filter(n => n.online).length;
  const offline = total - online;
  const blocked = nodes.filter(n => n.usb_status === 'BLOCKED').length;
  const unreadAlerts = health?.unread_alerts ?? 0;

  const recentNodes  = [...nodes].sort((a,b) => new Date(b.last_seen) - new Date(a.last_seen)).slice(0,5);
  const isAdmin = n => n.is_admin || (n.hostname || '').startsWith('[Admin]');
  const displayName = n => (n.hostname || '').replace(/^\[Admin\]\s*/, '') || '—';

  return (
    <div>
      <div className="page-header">
        <div>
          <h1 className="page-title">Overview</h1>
          <p className="page-sub">Last refreshed {lastRefresh.toLocaleTimeString()}</p>
        </div>
        <button className="btn btn-ghost btn-sm" onClick={refresh}>
          <RefreshCw size={13} /> Refresh
        </button>
      </div>

      {loading ? (
        <div style={{ display:'flex', justifyContent:'center', padding:60 }}>
          <div className="spinner" style={{ width:32, height:32 }} />
        </div>
      ) : (
        <>
          {/* Stats row */}
          <div style={{ display:'grid', gridTemplateColumns:'repeat(auto-fit,minmax(200px,1fr))', gap:16, marginBottom:24 }}>
            <StatCard label="Total Endpoints" value={total}      color="accent"  sub="Enrolled devices" />
            <StatCard label="Online"           value={online}    color="online"  sub={`${offline} offline`} />
            <StatCard label="USB Blocked"      value={blocked}   color="warn"    sub="Active restrictions" />
            <StatCard label="Unread Alerts"    value={unreadAlerts} color="offline" sub="Require attention" />
          </div>

          <div style={{ display:'grid', gridTemplateColumns:'1fr 1fr', gap:16, marginBottom:16 }}>
            {/* System health */}
            <div className="card">
              <div className="card-header">
                <Activity size={14} style={{ color:'var(--c-accent)' }} />
                <span className="card-title">System Health</span>
              </div>
              <div className="card-body" style={{ display:'grid', gridTemplateColumns:'1fr 1fr', gap:12 }}>
                {[
                  ['API Status',    health?.status || 'UNKNOWN'],
                  ['Database',      health?.database || 'UNKNOWN'],
                  ['Uptime',        health?.uptime_sec ? `${Math.round(health.uptime_sec/3600)}h ${Math.round((health.uptime_sec%3600)/60)}m` : '—'],
                  ['Recent Events', String(health?.recent_events ?? '—')],
                ].map(([k,v]) => (
                  <div key={k}>
                    <div style={{ fontSize:10, color:'var(--c-muted)', textTransform:'uppercase', letterSpacing:'0.08em', marginBottom:3 }}>{k}</div>
                    <div style={{ fontSize:13, fontWeight:600, color: v==='OPERATIONAL'||v==='CONNECTED'?'var(--c-online)':v==='DEGRADED'||v==='DISCONNECTED'?'var(--c-offline)':'var(--c-text)' }}
                         className="mono">{v}</div>
                  </div>
                ))}
              </div>
            </div>

            {/* Recent alerts */}
            <div className="card">
              <div className="card-header">
                <Bell size={14} style={{ color:'var(--c-accent)' }} />
                <span className="card-title">Recent Alerts</span>
                <button className="btn btn-ghost btn-xs" style={{ marginLeft:'auto' }} onClick={() => navigate('/alerts')}>View all</button>
              </div>
              {alerts.length === 0 ? (
                <div className="empty-state" style={{ padding:'30px 20px' }}>
                  <Shield size={28} style={{ color:'var(--c-online)' }} />
                  <p>No recent alerts</p>
                </div>
              ) : (
                <div style={{ overflow:'hidden' }}>
                  {alerts.slice(0,5).map(a => (
                    <div key={a.alert_id} style={{ padding:'10px 20px', borderBottom:'1px solid var(--c-border)', display:'flex', alignItems:'center', gap:10 }}>
                      <div style={{ flex:1, minWidth:0 }}>
                        <div style={{ fontSize:12, fontWeight:600, color: !a.is_read ? 'var(--c-text)' : 'var(--c-muted)' }} className="truncate-cell">{a.message}</div>
                        <div style={{ fontSize:10, color:'var(--c-dim)', marginTop:2 }}>{a.hostname} · {new Date(a.created_at).toLocaleTimeString()}</div>
                      </div>
                      <span className={`badge ${a.alert_type?.includes('CRITICAL')?'badge-critical':'badge-warn'}`}>
                        {a.alert_type?.replace(/_/g,' ') || 'ALERT'}
                      </span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Recent endpoints */}
          <div className="card">
            <div className="card-header">
              <Monitor size={14} style={{ color:'var(--c-accent)' }} />
              <span className="card-title">Endpoint Activity</span>
              <button className="btn btn-ghost btn-xs" style={{ marginLeft:'auto' }} onClick={() => navigate('/endpoints')}>View all</button>
            </div>
            <div style={{ overflowX:'auto' }}>
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Status</th><th>Hostname</th><th>IP Address</th>
                    <th>OS</th><th>USB</th><th>Last Seen</th>
                  </tr>
                </thead>
                <tbody>
                  {recentNodes.map(n => (
                    <tr key={n.id} className="clickable" onClick={() => navigate(`/endpoints/${n.id}`)}>
                      <td><div style={{ display:'flex', alignItems:'center', gap:6 }}><div className={`dot dot-${n.online?'online':'offline'}`}/>{n.online?'Online':'Offline'}</div></td>
                      <td style={{ fontWeight:600 }}>
                        {isAdmin(n) && (
                          <span style={{ fontSize:9, fontWeight:700, letterSpacing:'0.05em', color:'#00B4D8',
                            background:'rgba(0,180,216,0.12)', border:'1px solid rgba(0,180,216,0.35)',
                            borderRadius:4, padding:'1px 5px', marginRight:6, verticalAlign:'middle' }}>ADMIN</span>
                        )}
                        {displayName(n)}
                      </td>
                      <td className="mono" style={{ fontSize:12 }}>{n.ip_address}</td>
                      <td style={{ color:'var(--c-muted)', fontSize:12 }}>{n.os_version || '—'}</td>
                      <td><span className={`badge ${n.usb_status==='BLOCKED'?'badge-warn':'badge-online'}`}>{n.usb_status}</span></td>
                      <td style={{ color:'var(--c-muted)', fontSize:12 }}>{n.last_seen ? new Date(n.last_seen).toLocaleString() : '—'}</td>
                    </tr>
                  ))}
                  {recentNodes.length === 0 && (
                    <tr><td colSpan={6}>
                      <div className="empty-state" style={{ padding:'30px' }}>
                        <Monitor size={28} />
                        <p>No endpoints enrolled yet</p>
                      </div>
                    </td></tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
