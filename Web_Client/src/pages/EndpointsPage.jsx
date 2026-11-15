import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { Monitor, Search, RefreshCw, ChevronRight } from 'lucide-react';
import { getNodes } from '../services/api';
import { useApp } from '../App';

export default function EndpointsPage() {
  const { socket } = useApp();
  const navigate = useNavigate();
  const [nodes,  setNodes]  = useState([]);
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState('all'); // all|online|offline
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try { const d = await getNodes(); setNodes(d.nodes || []); }
    catch {}
    setLoading(false);
  }, []);

  useEffect(() => { load(); const iv = setInterval(load, 10000); return () => clearInterval(iv); }, [load]);

  useEffect(() => {
    if (!socket) return;
    const fn = () => load();
    socket.on('node:update', fn); socket.on('nodes:sweep', fn);
    return () => { socket.off('node:update', fn); socket.off('nodes:sweep', fn); };
  }, [socket, load]);

  const visible = nodes
    .filter(n => filter === 'all' ? true : filter === 'online' ? n.online : !n.online)
    .filter(n => !search || [n.hostname, n.ip_address, n.mac_address, n.os_version].join(' ').toLowerCase().includes(search.toLowerCase()));

  const total = nodes.length, online = nodes.filter(n=>n.online).length;
  const isAdmin = n => n.is_admin || (n.hostname || '').startsWith('[Admin]');
  const displayName = n => (n.hostname || '').replace(/^\[Admin\]\s*/, '') || '—';

  return (
    <div>
      <div className="page-header">
        <div>
          <h1 className="page-title">Endpoints</h1>
          <p className="page-sub">{total} enrolled · {online} online · {total-online} offline</p>
        </div>
        <button className="btn btn-ghost btn-sm" onClick={load}><RefreshCw size={13}/> Refresh</button>
      </div>

      <div className="filter-bar">
        <div className="search-box" style={{ flex:1, maxWidth:320 }}>
          <Search size={14} style={{ color:'var(--c-dim)', flexShrink:0 }}/>
          <input placeholder="Search hostname, IP, OS…" value={search} onChange={e=>setSearch(e.target.value)}/>
        </div>
        {['all','online','offline'].map(f => (
          <button key={f} onClick={()=>setFilter(f)}
            className={`btn btn-sm ${filter===f?'btn-primary':'btn-ghost'}`} style={{ textTransform:'capitalize' }}>
            {f}
          </button>
        ))}
      </div>

      <div className="card">
        {loading ? (
          <div style={{ display:'flex', justifyContent:'center', padding:60 }}>
            <div className="spinner" style={{ width:28, height:28 }}/>
          </div>
        ) : (
          <div style={{ overflowX:'auto' }}>
            <table className="data-table">
              <thead>
                <tr>
                  <th>Status</th><th>Hostname</th><th>IP Address</th>
                  <th>MAC</th><th>OS</th><th>USB</th><th>Alerts</th><th>Last Seen</th><th></th>
                </tr>
              </thead>
              <tbody>
                {visible.map(n => (
                  <tr key={n.id} className="clickable" onClick={() => navigate(`/endpoints/${n.id}`)}>
                    <td>
                      <div style={{ display:'flex', alignItems:'center', gap:7 }}>
                        <div className={`dot dot-${n.online?'online':'offline'}`}/>
                        <span style={{ fontSize:12, fontWeight:500, color: n.online?'var(--c-online)':'var(--c-muted)' }}>
                          {n.online?'Online':'Offline'}
                        </span>
                      </div>
                    </td>
                    <td style={{ fontWeight:600 }}>
                      {isAdmin(n) && (
                        <span style={{ fontSize:9, fontWeight:700, letterSpacing:'0.05em', color:'#00B4D8',
                          background:'rgba(0,180,216,0.12)', border:'1px solid rgba(0,180,216,0.35)',
                          borderRadius:4, padding:'1px 5px', marginRight:6, verticalAlign:'middle' }}>ADMIN</span>
                      )}
                      {displayName(n)}
                    </td>
                    <td className="mono" style={{ fontSize:12 }}>{n.ip_address||'—'}</td>
                    <td className="mono" style={{ fontSize:11, color:'var(--c-muted)' }}>{n.mac_address||'—'}</td>
                    <td style={{ color:'var(--c-muted)', fontSize:12 }}>{n.os_version||'—'}</td>
                    <td><span className={`badge ${n.usb_status==='BLOCKED'?'badge-warn':'badge-online'}`}>{n.usb_status||'—'}</span></td>
                    <td>
                      {n.critical_alerts > 0
                        ? <span className="badge badge-critical">{n.critical_alerts} critical</span>
                        : <span style={{ fontSize:12, color:'var(--c-dim)' }}>{n.total_events||0} events</span>}
                    </td>
                    <td style={{ fontSize:12, color:'var(--c-muted)' }}>
                      {n.last_seen ? new Date(n.last_seen).toLocaleString() : '—'}
                    </td>
                    <td><ChevronRight size={14} style={{ color:'var(--c-dim)' }}/></td>
                  </tr>
                ))}
                {visible.length === 0 && (
                  <tr><td colSpan={9}>
                    <div className="empty-state">
                      <Monitor size={32}/>
                      <h3>No endpoints found</h3>
                      <p>{search ? 'Try a different search term' : 'No agents connected yet'}</p>
                    </div>
                  </td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
