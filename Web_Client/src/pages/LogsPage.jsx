import React, { useState, useEffect, useCallback, useRef } from 'react';
import { ScrollText, Search, RefreshCw, Download } from 'lucide-react';
import { getLogs } from '../services/api';
import { useApp } from '../App';

const SEV_BADGE = { CRITICAL:'badge-critical', WARN:'badge-warn', INFO:'badge-info' };
const TYPE_BADGE = { forensic:'badge-accent', command:'badge-info', alert:'badge-warn' };

function exportCSV(rows) {
  if (!rows?.length) return;
  const cols = ['timestamp','log_type','severity','title','message','hostname','ip_address'];
  const csv  = [cols.join(','), ...rows.map(r=>cols.map(c=>`"${String(r[c]??'').replace(/"/g,'""')}"`).join(','))].join('\n');
  const blob = new Blob([csv], { type:'text/csv' });
  const url  = URL.createObjectURL(blob);
  const a    = Object.assign(document.createElement('a'), { href:url, download:`rasd_logs_${new Date().toISOString().split('T')[0]}.csv` });
  a.click(); URL.revokeObjectURL(url);
}

const today  = () => new Date().toISOString().split('T')[0];
const daysAgo = n => new Date(Date.now()-n*86400000).toISOString().split('T')[0];

export default function LogsPage() {
  const { toast } = useApp();
  const [logs,    setLogs]    = useState([]);
  const [type,    setType]    = useState('all');
  const [severity,setSev]     = useState('ALL');
  const [search,  setSearch]  = useState('');
  const [from,    setFrom]    = useState(daysAgo(1));
  const [to,      setTo]      = useState(today());
  const [loading, setLoading] = useState(false);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const tableRef = useRef(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const d = await getLogs({ type, severity, from: new Date(from).toISOString(), to: new Date(to+'T23:59:59').toISOString(), limit: 1000 });
      setLogs(d.logs || []);
    } catch (e) { toast('Failed to load logs','error'); }
    setLoading(false);
  }, [type, severity, from, to, toast]);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!autoRefresh) return;
    const iv = setInterval(load, 15000);
    return () => clearInterval(iv);
  }, [autoRefresh, load]);

  const visible = search
    ? logs.filter(l => [l.title,l.message,l.hostname,l.ip_address].join(' ').toLowerCase().includes(search.toLowerCase()))
    : logs;

  const ts = d => {
    try { return new Date(d).toLocaleString(); } catch { return d; }
  };

  return (
    <div>
      <div className="page-header">
        <div><h1 className="page-title">System Logs</h1><p className="page-sub">{visible.length} entries shown</p></div>
        <div style={{ display:'flex', gap:8 }}>
          <button className={`btn btn-sm ${autoRefresh?'btn-primary':'btn-ghost'}`} onClick={()=>setAutoRefresh(a=>!a)}>
            {autoRefresh ? '⏸ Auto Refresh ON' : '▶ Auto Refresh'}
          </button>
          <button className="btn btn-ghost btn-sm" onClick={load}><RefreshCw size={13}/></button>
          <button className="btn btn-ghost btn-sm" onClick={()=>exportCSV(visible)} disabled={!visible.length}><Download size={13}/> CSV</button>
        </div>
      </div>

      {/* Filters */}
      <div style={{ display:'grid', gridTemplateColumns:'1fr 1fr 1fr 1fr', gap:10, marginBottom:16 }}>
        <div>
          <label className="input-label">Log Type</label>
          <select className="input" value={type} onChange={e=>setType(e.target.value)}>
            <option value="all">All Types</option>
            <option value="forensic">Forensic</option>
            <option value="command">Commands</option>
            <option value="alert">Alerts</option>
          </select>
        </div>
        <div>
          <label className="input-label">Severity</label>
          <select className="input" value={severity} onChange={e=>setSev(e.target.value)}>
            {['ALL','CRITICAL','WARN','INFO'].map(s=><option key={s} value={s}>{s}</option>)}
          </select>
        </div>
        <div>
          <label className="input-label">From</label>
          <input className="input" type="date" value={from} max={to} onChange={e=>setFrom(e.target.value)}/>
        </div>
        <div>
          <label className="input-label">To</label>
          <input className="input" type="date" value={to} min={from} max={today()} onChange={e=>setTo(e.target.value)}/>
        </div>
      </div>

      <div className="filter-bar" style={{marginBottom:16}}>
        <div className="search-box" style={{flex:1,maxWidth:360}}>
          <Search size={13} style={{color:'var(--c-dim)',flexShrink:0}}/>
          <input placeholder="Search title, message, hostname…" value={search} onChange={e=>setSearch(e.target.value)}/>
        </div>
        {['1d','7d','30d'].map(r=>(
          <button key={r} className="btn btn-ghost btn-xs"
            onClick={()=>{setFrom(daysAgo(r==='1d'?1:r==='7d'?7:30));setTo(today());}}>
            Last {r}
          </button>
        ))}
        <span style={{fontSize:11,color:'var(--c-muted)',marginLeft:'auto'}}>{logs.length} total{search&&` · ${visible.length} matching`}</span>
      </div>

      <div className="card">
        {loading ? (
          <div style={{display:'flex',justifyContent:'center',padding:60}}><div className="spinner" style={{width:28,height:28}}/></div>
        ) : visible.length===0 ? (
          <div className="empty-state"><ScrollText size={32}/><h3>No logs found</h3><p>Try adjusting the filters or date range</p></div>
        ) : (
          <div ref={tableRef} style={{overflowX:'auto',maxHeight:'calc(100vh - 340px)',overflowY:'auto'}}>
            <table className="data-table">
              <thead style={{position:'sticky',top:0,background:'var(--c-panel)'}}>
                <tr><th>Time</th><th>Type</th><th>Severity</th><th>Title</th><th>Message</th><th>Endpoint</th><th>IP</th></tr>
              </thead>
              <tbody>
                {visible.map((l,i)=>(
                  <tr key={`${l.id}-${i}`}>
                    <td className="mono" style={{fontSize:11,color:'var(--c-muted)',whiteSpace:'nowrap'}}>{ts(l.timestamp)}</td>
                    <td><span className={`badge ${TYPE_BADGE[l.log_type]||'badge-info'}`}>{l.log_type}</span></td>
                    <td><span className={`badge ${SEV_BADGE[l.severity]||'badge-info'}`}>{l.severity}</span></td>
                    <td style={{fontWeight:600,fontSize:12,whiteSpace:'nowrap'}}>{l.title}</td>
                    <td style={{fontSize:12,color:'var(--c-muted)',maxWidth:300,overflow:'hidden',textOverflow:'ellipsis',whiteSpace:'nowrap'}} title={l.message}>{l.message||'—'}</td>
                    <td style={{fontSize:12}}>{l.hostname||'—'}</td>
                    <td className="mono" style={{fontSize:11,color:'var(--c-muted)'}}>{l.ip_address||'—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
