import React, { useState, useEffect, useCallback } from 'react';
import { Bell, CheckCheck, RefreshCw, Filter } from 'lucide-react';
import { getAlerts, markAlertRead, markAllRead } from '../services/api';
import { useApp } from '../App';

const TYPE_BADGE = { CRITICAL_FORENSIC:'badge-critical', NEW_DEVICE:'badge-info', IP_CHANGED:'badge-warn', COMMAND_ISSUED:'badge-accent' };

export default function AlertsPage() {
  const { socket, setUnread, toast } = useApp();
  const [alerts,  setAlerts]  = useState([]);
  const [filter,  setFilter]  = useState('all');
  const [typeF,   setTypeF]   = useState('ALL');
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try { const d = await getAlerts(500); setAlerts(d.alerts||[]); }
    catch {} setLoading(false);
  }, []);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!socket) return;
    socket.on('alert:new', load);
    return () => socket.off('alert:new', load);
  }, [socket, load]);

  const handleRead = async id => {
    try {
      await markAlertRead(id);
      setAlerts(prev => prev.map(a => a.alert_id===id ? {...a,is_read:true} : a));
      setUnread(p => Math.max(0,p-1));
    } catch { toast('Failed','error'); }
  };

  const handleAllRead = async () => {
    try { await markAllRead(); setAlerts(p=>p.map(a=>({...a,is_read:true}))); setUnread(0); toast('All marked read','info'); }
    catch { toast('Failed','error'); }
  };

  const types = ['ALL',...new Set(alerts.map(a=>a.alert_type).filter(Boolean))];
  const visible = alerts
    .filter(a => filter==='unread' ? !a.is_read : true)
    .filter(a => typeF==='ALL' || a.alert_type===typeF);
  const unreadCnt = alerts.filter(a=>!a.is_read).length;

  return (
    <div>
      <div className="page-header">
        <div>
          <h1 className="page-title">Alerts</h1>
          <p className="page-sub">{unreadCnt} unread · {alerts.length} total</p>
        </div>
        <div style={{ display:'flex', gap:8 }}>
          <button className="btn btn-ghost btn-sm" onClick={load}><RefreshCw size={13}/></button>
          {unreadCnt>0 && <button className="btn btn-success btn-sm" onClick={handleAllRead}><CheckCheck size={13}/> Mark All Read</button>}
        </div>
      </div>

      <div className="filter-bar">
        {['all','unread'].map(f=>(
          <button key={f} className={`btn btn-sm ${filter===f?'btn-primary':'btn-ghost'}`}
            onClick={()=>setFilter(f)}>{f==='unread'?`Unread (${unreadCnt})`:'All'}</button>
        ))}
        <div style={{ marginLeft:'auto', display:'flex', alignItems:'center', gap:8 }}>
          <Filter size={12} style={{color:'var(--c-muted)'}}/>
          <select className="input" style={{width:'auto',padding:'5px 10px',fontSize:12}} value={typeF} onChange={e=>setTypeF(e.target.value)}>
            {types.map(t=><option key={t} value={t}>{t==='ALL'?'All Types':t.replace(/_/g,' ')}</option>)}
          </select>
        </div>
      </div>

      <div className="card">
        {loading ? <div style={{display:'flex',justifyContent:'center',padding:60}}><div className="spinner" style={{width:28,height:28}}/></div>
        : visible.length===0 ? (
          <div className="empty-state"><Bell size={32} style={{color:'var(--c-online)'}}/><h3>No alerts</h3><p>{filter==='unread'?'All caught up!':'No alerts yet'}</p></div>
        ) : (
          <div>
            {visible.map(a=>(
              <div key={a.alert_id} style={{padding:'12px 20px',borderBottom:'1px solid var(--c-border)',display:'flex',alignItems:'flex-start',gap:14,background:!a.is_read?'rgba(249,115,22,0.04)':'transparent',opacity:a.is_read?0.65:1}}>
                <div style={{paddingTop:4,flexShrink:0}}><div className={`dot ${!a.is_read?'dot-warn':'dot-muted'}`}/></div>
                <div style={{flex:1,minWidth:0}}>
                  <div style={{display:'flex',alignItems:'center',gap:8,marginBottom:4,flexWrap:'wrap'}}>
                    <span className={`badge ${TYPE_BADGE[a.alert_type]||'badge-warn'}`}>{a.alert_type?.replace(/_/g,' ')||'ALERT'}</span>
                    {a.hostname && <span style={{fontSize:11,color:'var(--c-muted)'}}>{a.hostname}</span>}
                    <span style={{fontSize:11,color:'var(--c-dim)',marginLeft:'auto'}}>{new Date(a.created_at).toLocaleString()}</span>
                  </div>
                  <div style={{fontSize:13,color:!a.is_read?'var(--c-text)':'var(--c-muted)'}}>{a.message}</div>
                </div>
                {!a.is_read && <button className="btn btn-ghost btn-xs" onClick={()=>handleRead(a.alert_id)}>✓ Read</button>}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
