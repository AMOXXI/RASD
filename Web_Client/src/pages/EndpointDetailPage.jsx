import React, { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ChevronLeft, Usb, RotateCw, Power, DownloadCloud, Terminal, RefreshCw, X } from 'lucide-react';
import { getNodes, getNodeForensics, getCommandLog, blockUsb, allowUsb, restartDevice, shutdownDevice, updateWindows, execPowerShell } from '../services/api';
import { useApp } from '../App';

function DetailRow({ label, value, mono }) {
  return (
    <div style={{ display:'flex', flexDirection:'column', gap:3 }}>
      <div style={{ fontSize:10, fontWeight:600, textTransform:'uppercase', letterSpacing:'0.08em', color:'var(--c-muted)' }}>{label}</div>
      <div style={{ fontSize:13, color:'var(--c-text)' }} className={mono?'mono':''}>{value||'—'}</div>
    </div>
  );
}

export default function EndpointDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { socket, toast } = useApp();
  const [node,     setNode]     = useState(null);
  const [forensics,setForensics]= useState([]);
  const [cmdLog,   setCmdLog]   = useState([]);
  const [tab,      setTab]      = useState('overview');
  const [psModal,  setPsModal]  = useState(false);
  const [psScript, setPsScript] = useState('');
  const [loading,  setLoading]  = useState(true);
  const [busy,     setBusy]     = useState(false);

  const load = useCallback(async () => {
    try {
      const [nd, f, cl] = await Promise.all([
        getNodes(),
        getNodeForensics(id, 100),
        getCommandLog(id, 100),
      ]);
      const found = (nd.nodes||[]).find(n => n.id === id);
      setNode(found || null);
      setForensics(f.events || []);
      setCmdLog(cl.log || []);
    } catch {}
    setLoading(false);
  }, [id]);

  useEffect(() => { load(); const iv = setInterval(load, 8000); return () => clearInterval(iv); }, [load]);

  useEffect(() => {
    if (!socket) return;
    const fn = () => load();
    socket.on('node:update', fn); socket.on('forensic:event', fn); socket.on('command:ack', fn);
    return () => { socket.off('node:update', fn); socket.off('forensic:event', fn); socket.off('command:ack', fn); };
  }, [socket, load]);

  const cmd = async (fn, label) => {
    setBusy(true);
    try { await fn(id); toast(`${label} sent to ${node?.hostname}`, 'info'); await load(); }
    catch (e) { toast(`Failed: ${e.response?.data?.error||e.message}`, 'error'); }
    setBusy(false);
  };

  const sendPS = async () => {
    if (!psScript.trim()) return;
    setBusy(true);
    try { await execPowerShell(id, psScript); toast('PowerShell script queued', 'info'); setPsModal(false); setPsScript(''); await load(); }
    catch (e) { toast(`Failed: ${e.response?.data?.error||e.message}`, 'error'); }
    setBusy(false);
  };

  const sevColor = s => s==='CRITICAL'?'var(--c-offline)':s==='WARN'?'var(--c-warn)':'var(--c-info)';
  const statusBadge = s => s==='DELIVERED'?'badge-online':s==='FAILED'?'badge-critical':'badge-warn';

  if (loading) return <div style={{ display:'flex', justifyContent:'center', padding:80 }}><div className="spinner" style={{ width:32, height:32 }}/></div>;
  if (!node) return (
    <div className="empty-state">
      <h3>Endpoint not found</h3>
      <p>ID: {id}</p>
      <button className="btn btn-ghost btn-sm" onClick={() => navigate('/endpoints')}>Back to Endpoints</button>
    </div>
  );

  return (
    <div>
      {/* Header */}
      <div className="page-header">
        <div style={{ display:'flex', alignItems:'center', gap:12 }}>
          <button className="btn btn-ghost btn-sm" onClick={() => navigate('/endpoints')}>
            <ChevronLeft size={14}/> Back
          </button>
          <div>
            <div style={{ display:'flex', alignItems:'center', gap:10 }}>
              <div className={`dot dot-${node.online?'online':'offline'}`} style={{ width:10, height:10 }}/>
              <h1 className="page-title" style={{ margin:0 }}>{node.hostname||node.ip_address}</h1>
              <span className={`badge ${node.usb_status==='BLOCKED'?'badge-warn':'badge-online'}`}>{node.usb_status}</span>
            </div>
            <p className="page-sub">{node.ip_address} · {node.os_version}</p>
          </div>
        </div>
        <div style={{ display:'flex', gap:8 }}>
          <button className="btn btn-ghost btn-sm" onClick={load}><RefreshCw size={13}/></button>
          {node.usb_status === 'ALLOWED'
            ? <button className="btn btn-warn btn-sm" onClick={() => cmd(blockUsb, 'Block USB')} disabled={busy}>
                <Usb size={13}/> Block USB
              </button>
            : <button className="btn btn-success btn-sm" onClick={() => cmd(allowUsb, 'Allow USB')} disabled={busy}>
                <Usb size={13}/> Allow USB
              </button>}
          <button className="btn btn-danger btn-sm" onClick={() => cmd(restartDevice, 'Restart')} disabled={busy}>
            <RotateCw size={13}/> Restart
          </button>
          <button className="btn btn-danger btn-sm" onClick={() => cmd(shutdownDevice, 'Shutdown')} disabled={busy}>
            <Power size={13}/> Shutdown
          </button>
          <button className="btn btn-warn btn-sm" onClick={() => cmd(updateWindows, 'Windows Update')} disabled={busy}>
            <DownloadCloud size={13}/> Update Windows
          </button>
          <button className="btn btn-ghost btn-sm" onClick={() => setPsModal(true)} disabled={busy}>
            <Terminal size={13}/> Run Script
          </button>
        </div>
      </div>

      {/* Tabs */}
      <div className="tabs">
        {[['overview','Overview'],['forensics','Forensics'],['commands','Command History']].map(([k,l]) => (
          <div key={k} className={`tab${tab===k?' active':''}`} onClick={() => setTab(k)}>{l}</div>
        ))}
      </div>

      {tab === 'overview' && (
        <div className="card">
          <div className="card-body" style={{ display:'grid', gridTemplateColumns:'repeat(auto-fill,minmax(200px,1fr))', gap:20 }}>
            <DetailRow label="Hostname"      value={node.hostname}      />
            <DetailRow label="IP Address"    value={node.ip_address}    mono />
            <DetailRow label="MAC Address"   value={node.mac_address}   mono />
            <DetailRow label="OS Version"    value={node.os_version}    />
            <DetailRow label="Agent Version" value={node.agent_version} mono />
            <DetailRow label="USB Status"    value={node.usb_status}    />
            <DetailRow label="Status"        value={node.online ? 'Online' : 'Offline'} />
            <DetailRow label="Last Seen"     value={node.last_seen ? new Date(node.last_seen).toLocaleString() : '—'} />
            <DetailRow label="Enrolled At"   value={node.enrolled_at ? new Date(node.enrolled_at).toLocaleString() : '—'} />
            <DetailRow label="Total Events"  value={String(node.total_events||0)} mono />
            <DetailRow label="Critical Alerts" value={String(node.critical_alerts||0)} mono />
          </div>
        </div>
      )}

      {tab === 'forensics' && (
        <div className="card">
          {forensics.length === 0 ? (
            <div className="empty-state"><p>No forensic events recorded</p></div>
          ) : (
            <div style={{ overflowX:'auto' }}>
              <table className="data-table">
                <thead><tr><th>Severity</th><th>Event Type</th><th>File</th><th>Hash</th><th>Details</th><th>Time</th></tr></thead>
                <tbody>
                  {forensics.map(e => (
                    <tr key={e.id}>
                      <td><span className={`badge badge-${e.severity==='CRITICAL'?'critical':e.severity==='WARN'?'warn':'info'}`}>{e.severity}</span></td>
                      <td style={{ fontWeight:500 }}>{e.event_type}</td>
                      <td className="mono truncate-cell" style={{ fontSize:11 }}>{e.file_name||'—'}</td>
                      <td className="mono" style={{ fontSize:10, color:'var(--c-muted)' }}>{e.file_hash?.substring(0,16)||'—'}</td>
                      <td style={{ fontSize:12, color:'var(--c-muted)' }} className="truncate-cell">{e.details||'—'}</td>
                      <td style={{ fontSize:12, color:'var(--c-muted)' }}>{new Date(e.logged_at).toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'commands' && (
        <div className="card">
          {cmdLog.length === 0 ? (
            <div className="empty-state"><p>No commands issued to this endpoint</p></div>
          ) : (
            <div style={{ overflowX:'auto' }}>
              <table className="data-table">
                <thead><tr><th>Command</th><th>Status</th><th>Issued By</th><th>Issued At</th><th>Delivered At</th></tr></thead>
                <tbody>
                  {cmdLog.map(c => (
                    <tr key={c.id}>
                      <td style={{ fontWeight:600 }} className="mono">{c.command}</td>
                      <td><span className={`badge ${statusBadge(c.status)}`}>{c.status}</span></td>
                      <td style={{ fontSize:12, color:'var(--c-muted)' }}>{c.issued_by||'admin'}</td>
                      <td style={{ fontSize:12, color:'var(--c-muted)' }}>{new Date(c.issued_at).toLocaleString()}</td>
                      <td style={{ fontSize:12, color:'var(--c-muted)' }}>{c.delivered_at ? new Date(c.delivered_at).toLocaleString() : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {/* PowerShell modal */}
      {psModal && (
        <div className="modal-overlay" onClick={() => setPsModal(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span style={{ fontWeight:600 }}>Run PowerShell on {node.hostname}</span>
              <button onClick={() => setPsModal(false)} className="btn btn-ghost btn-sm"><X size={14}/></button>
            </div>
            <div className="modal-body">
              <label className="input-label">Script</label>
              <textarea className="ps-editor" rows={8} value={psScript}
                onChange={e => setPsScript(e.target.value)}
                placeholder="# Write PowerShell here&#10;Get-Process | Select-Object Name, CPU" />
              <p style={{ fontSize:11, color:'var(--c-muted)', marginTop:8 }}>
                Script is queued and will execute when the agent next connects.
              </p>
            </div>
            <div className="modal-footer">
              <button className="btn btn-ghost" onClick={() => setPsModal(false)}>Cancel</button>
              <button className="btn btn-primary" onClick={sendPS} disabled={busy||!psScript.trim()}>
                {busy ? 'Sending…' : 'Send Script'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
