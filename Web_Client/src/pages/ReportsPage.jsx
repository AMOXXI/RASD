import React, { useState, useCallback } from 'react';
import { FileBarChart2, Download, RefreshCw } from 'lucide-react';
import { getReportSummary, getActivityReport, getAlertsReport, getEndpointsReport } from '../services/api';
import { useApp } from '../App';

const today = () => new Date().toISOString().split('T')[0];
const daysAgo = n => new Date(Date.now()-n*86400000).toISOString().split('T')[0];

function exportCSV(rows, filename) {
  if (!rows?.length) return;
  const headers = Object.keys(rows[0]);
  const csv = [headers.join(','), ...rows.map(r=>headers.map(h=>`"${String(r[h]??'').replace(/"/g,'""')}"`).join(','))].join('\n');
  const blob = new Blob([csv], { type:'text/csv' });
  const url  = URL.createObjectURL(blob);
  const a    = Object.assign(document.createElement('a'), { href:url, download:filename });
  a.click(); URL.revokeObjectURL(url);
}

export default function ReportsPage() {
  const { toast } = useApp();
  const [from,     setFrom]     = useState(daysAgo(7));
  const [to,       setTo]       = useState(today());
  const [type,     setType]     = useState('summary');
  const [data,     setData]     = useState(null);
  const [loading,  setLoading]  = useState(false);

  const generate = useCallback(async () => {
    setLoading(true); setData(null);
    try {
      const f = new Date(from).toISOString(), t = new Date(to + 'T23:59:59').toISOString();
      let result;
      if (type==='summary')   result = await getReportSummary(f, t);
      if (type==='activity')  result = await getActivityReport(f, t);
      if (type==='alerts')    result = await getAlertsReport(f, t);
      if (type==='endpoints') result = await getEndpointsReport();
      setData(result);
    } catch (e) { toast(e.response?.data?.error||'Report generation failed','error'); }
    setLoading(false);
  }, [from, to, type, toast]);

  const doExport = () => {
    if (!data) return;
    const rows = data.data || (data.nodes ? [data.nodes, data.alerts, data.forensics, data.commands] : null);
    if (Array.isArray(rows) && rows.length) exportCSV(rows, `rasd_${type}_${from}_${to}.csv`);
    else toast('No data to export','warn');
  };

  return (
    <div>
      <div className="page-header">
        <div><h1 className="page-title">Reports</h1><p className="page-sub">Generate and export activity reports</p></div>
      </div>

      {/* Builder */}
      <div className="card" style={{marginBottom:16}}>
        <div className="card-header"><FileBarChart2 size={14} style={{color:'var(--c-accent)'}}/><span className="card-title">Report Builder</span></div>
        <div className="card-body" style={{display:'flex',gap:16,flexWrap:'wrap',alignItems:'flex-end'}}>
          <div>
            <label className="input-label">Report Type</label>
            <select className="input" style={{width:'auto'}} value={type} onChange={e=>setType(e.target.value)}>
              <option value="summary">Summary</option>
              <option value="activity">Activity Log</option>
              <option value="alerts">Alerts</option>
              <option value="endpoints">Endpoint Health</option>
            </select>
          </div>
          {type!=='endpoints' && (
            <>
              <div>
                <label className="input-label">From</label>
                <input className="input" type="date" style={{width:'auto'}} value={from} max={to} onChange={e=>setFrom(e.target.value)}/>
              </div>
              <div>
                <label className="input-label">To</label>
                <input className="input" type="date" style={{width:'auto'}} value={to} min={from} max={today()} onChange={e=>setTo(e.target.value)}/>
              </div>
              <div style={{display:'flex',gap:8}}>
                <button className="btn btn-ghost btn-sm" onClick={()=>{setFrom(daysAgo(7));setTo(today());}}>Last 7d</button>
                <button className="btn btn-ghost btn-sm" onClick={()=>{setFrom(daysAgo(30));setTo(today());}}>Last 30d</button>
              </div>
            </>
          )}
          <button className="btn btn-primary" onClick={generate} disabled={loading} style={{marginLeft:'auto'}}>
            {loading?<><div className="spinner" style={{width:13,height:13}}/> Generating…</>:<><RefreshCw size={13}/> Generate</>}
          </button>
          {data && <button className="btn btn-ghost" onClick={doExport}><Download size={13}/> Export CSV</button>}
        </div>
      </div>

      {/* Results */}
      {data && (
        <div className="card">
          {type==='summary' && <SummaryView data={data}/>}
          {type==='activity' && <TableView rows={data.data||[]} cols={['time','type','severity','hostname','ip_address','event','details']}/>}
          {type==='alerts'   && <TableView rows={data.data||[]} cols={['created_at','alert_type','message','hostname','ip_address','is_read']}/>}
          {type==='endpoints'&& <TableView rows={data.data||[]} cols={['hostname','ip_address','os_version','usb_status','online','last_seen','total_events','critical_events','total_commands']}/>}
        </div>
      )}
    </div>
  );
}

function SummaryView({ data }) {
  const { period, nodes, alerts, forensics, commands } = data;
  const sections = [
    { label:'Endpoints', icon:'📡', items: [['Total',nodes?.total],['Online',nodes?.online],['Offline',nodes?.offline],['USB Blocked',nodes?.usb_blocked]] },
    { label:`Alerts (${period?.from?.substring(0,10)} → ${period?.to?.substring(0,10)})`, icon:'🔔', items:[['Total',alerts?.total],['Unread',alerts?.unread],['Critical',alerts?.critical]] },
    { label:'Forensic Events', icon:'🔍', items:[['Total',forensics?.total],['Critical',forensics?.critical],['Warning',forensics?.warn],['Info',forensics?.info]] },
    { label:'Commands', icon:'⌨', items:[['Total',commands?.total],['Delivered',commands?.delivered],['Failed',commands?.failed],['Pending',commands?.pending]] },
  ];
  return (
    <div className="card-body">
      <div style={{display:'grid',gridTemplateColumns:'repeat(auto-fit,minmax(220px,1fr))',gap:20}}>
        {sections.map(s=>(
          <div key={s.label}>
            <div style={{fontWeight:600,marginBottom:12,color:'var(--c-text)',display:'flex',gap:8}}>{s.icon} {s.label}</div>
            {s.items.map(([k,v])=>(
              <div key={k} style={{display:'flex',justifyContent:'space-between',padding:'6px 0',borderBottom:'1px solid var(--c-border)'}}>
                <span style={{fontSize:12,color:'var(--c-muted)'}}>{k}</span>
                <span className="mono" style={{fontSize:13,fontWeight:600,color:'var(--c-text)'}}>{v??'—'}</span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

function TableView({ rows, cols }) {
  if (!rows.length) return <div className="empty-state" style={{padding:40}}><p>No data in this range</p></div>;
  return (
    <div style={{overflowX:'auto',maxHeight:500,overflowY:'auto'}}>
      <table className="data-table">
        <thead><tr>{cols.map(c=><th key={c}>{c.replace(/_/g,' ')}</th>)}</tr></thead>
        <tbody>
          {rows.map((r,i)=>(
            <tr key={i}>
              {cols.map(c=><td key={c} style={{fontSize:12,maxWidth:200,overflow:'hidden',textOverflow:'ellipsis',whiteSpace:'nowrap'}}>{String(r[c]??'—')}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
