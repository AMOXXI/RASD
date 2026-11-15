import React, { useState, useEffect, useCallback } from 'react';
import { Terminal, Monitor, Usb, RotateCw, Power, DownloadCloud, Send, RefreshCw, ChevronRight } from 'lucide-react';
import PageHeader from '../components/PageHeader';
import { getNodes, blockUsb, allowUsb, restartDevice, shutdownDevice, updateWindows, updateWindowsAll, execPowerShell, getCommandLog } from '../services/api';
import { useApp } from '../App';

export default function RemotePage() {
  const { socket, toast } = useApp();
  const [nodes,    setNodes]    = useState([]);
  const [selected, setSelected] = useState(null);
  const [cmdLog,   setCmdLog]   = useState([]);
  const [psScript, setPsScript] = useState('');
  const [busy,     setBusy]     = useState(false);
  const [loading,  setLoading]  = useState(true);

  const loadNodes = useCallback(async () => {
    try { const d = await getNodes(); setNodes(d.nodes||[]); }
    catch {} setLoading(false);
  }, []);

  const loadLog = useCallback(async () => {
    if (!selected) return;
    try { const d = await getCommandLog(selected, 50); setCmdLog(d.log||[]); }
    catch {}
  }, [selected]);

  useEffect(() => { loadNodes(); const iv=setInterval(loadNodes,10000); return ()=>clearInterval(iv); }, [loadNodes]);
  useEffect(() => { loadLog(); }, [loadLog]);

  useEffect(() => {
    if (!socket) return;
    const fn = () => { loadNodes(); loadLog(); };
    socket.on('command:ack', fn); socket.on('node:update', fn);
    return () => { socket.off('command:ack', fn); socket.off('node:update', fn); };
  }, [socket, loadNodes, loadLog]);

  const run = async (fn, label) => {
    if (!selected) { toast('Select an endpoint first', 'warn'); return; }
    const node = nodes.find(n=>n.id===selected);
    setBusy(true);
    try { await fn(selected); toast(`${label} → ${node?.hostname}`, 'info'); await loadLog(); }
    catch (e) { toast(`Failed: ${e.response?.data?.error||e.message}`, 'error'); }
    setBusy(false);
  };

  const sendPS = async () => {
    if (!psScript.trim()) return;
    await run(() => execPowerShell(selected, psScript), 'Script');
    setPsScript('');
  };

  const handleUpdateAll = async () => {
    if (!window.confirm('This will trigger Windows Update on ALL online endpoints. Continue?')) return;
    setBusy(true);
    try {
      const d = await updateWindowsAll();
      toast(`Windows Update sent to ${d.issued ?? 0} endpoint(s)`, 'info');
      await loadNodes();
    } catch (e) {
      toast(`Failed: ${e.response?.data?.error||e.message}`, 'error');
    }
    setBusy(false);
  };

  const node = nodes.find(n=>n.id===selected);

  const statusBadge = s => s==='DELIVERED'?'badge-online':s==='FAILED'?'badge-critical':'badge-warn';

  return (
    <div>
      <PageHeader
        title="Remote Administration"
        subtitle="Send commands to connected endpoints"
        actions={<>
          <button className="btn btn-warn btn-sm" onClick={handleUpdateAll} disabled={busy}>
            <DownloadCloud size={14}/> Update All Endpoints
          </button>
          <button className="btn btn-ghost btn-sm" onClick={loadNodes} aria-label="Refresh"><RefreshCw size={14}/></button>
        </>}
      />

      <div style={{ display:'grid', gridTemplateColumns:'280px 1fr', gap:16 }}>
        {/* Endpoint selector */}
        <div className="card" style={{ overflow:'visible' }}>
          <div className="card-header"><Monitor size={14} style={{color:'var(--c-accent)'}}/><span className="card-title">Endpoints ({nodes.length})</span></div>
          {loading ? <div style={{display:'flex',justifyContent:'center',padding:40}}><div className="spinner"/></div> : (
            <div style={{maxHeight:500,overflowY:'auto'}}>
              {nodes.length===0 ? (
                <div className="empty-state" style={{padding:'30px 16px'}}><p>No agents connected</p></div>
              ) : nodes.map(n=>(
                <div key={n.id}
                  onClick={()=>setSelected(n.id)}
                  style={{padding:'10px 16px',borderBottom:'1px solid var(--c-border)',cursor:'pointer',
                    background:selected===n.id?'var(--c-accent-bg)':'transparent',
                    display:'flex',alignItems:'center',gap:10}}>
                  <div className={`dot dot-${n.online?'online':'offline'}`}/>
                  <div style={{flex:1,minWidth:0}}>
                    <div style={{fontSize:13,fontWeight:600,color:selected===n.id?'var(--c-accent)':'var(--c-text)'}} className="truncate-cell">{n.hostname||n.ip_address}</div>
                    <div style={{fontSize:11,color:'var(--c-muted)'}}>{n.ip_address}</div>
                  </div>
                  {selected===n.id && <ChevronRight size={12} style={{color:'var(--c-accent)',flexShrink:0}}/>}
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Command panel */}
        <div style={{display:'flex',flexDirection:'column',gap:16}}>
          {/* Quick commands */}
          <div className="card">
            <div className="card-header">
              <Terminal size={14} style={{color:'var(--c-accent)'}}/>
              <span className="card-title">Quick Commands</span>
              {node && <span style={{marginLeft:'auto',fontSize:12,color:'var(--c-muted)'}}>{node.hostname}</span>}
            </div>
            <div className="card-body">
              {!selected ? (
                <div style={{color:'var(--c-dim)',fontSize:13}}>← Select an endpoint to send commands</div>
              ) : (
                <div style={{display:'grid',gridTemplateColumns:'repeat(auto-fit,minmax(180px,1fr))',gap:10}}>
                  <button className="btn btn-warn" onClick={()=>run(blockUsb,'Block USB')} disabled={busy||!node?.online}>
                    <Usb size={13}/> Block USB Drive
                  </button>
                  <button className="btn btn-success" onClick={()=>run(allowUsb,'Allow USB')} disabled={busy||!node?.online}>
                    <Usb size={13}/> Allow USB Drive
                  </button>
                  <button className="btn btn-danger" onClick={()=>run(restartDevice,'Restart')} disabled={busy||!node?.online}>
                    <RotateCw size={13}/> Restart Device
                  </button>
                  <button className="btn btn-danger" onClick={()=>run(shutdownDevice,'Shutdown')} disabled={busy||!node?.online}>
                    <Power size={13}/> Shutdown Device
                  </button>
                  <button className="btn btn-warn" onClick={()=>run(updateWindows,'Windows Update')} disabled={busy||!node?.online}>
                    <DownloadCloud size={13}/> Update Windows
                  </button>
                </div>
              )}
              {node && !node.online && (
                <div style={{marginTop:12,padding:'8px 12px',background:'var(--c-offline-bg)',border:'1px solid color-mix(in srgb,var(--crit) 30%,transparent)',borderRadius:10,fontSize:12,color:'var(--crit)'}}>
                  This endpoint is offline. Commands will be queued and delivered when it reconnects.
                </div>
              )}
            </div>
          </div>

          {/* PowerShell editor */}
          <div className="card">
            <div className="card-header">
              <Terminal size={14} style={{color:'var(--c-accent)'}}/>
              <span className="card-title">PowerShell Script</span>
            </div>
            <div className="card-body">
              <label className="input-label">Script (will run on target endpoint)</label>
              <textarea className="ps-editor" rows={7} value={psScript} onChange={e=>setPsScript(e.target.value)} disabled={!selected}
                placeholder={'# Example:\nGet-Process | Select-Object Name, CPU, WorkingSet | Format-Table\n\n# Get disk info:\nGet-PSDrive -PSProvider FileSystem'}/>
              <div style={{display:'flex',justifyContent:'flex-end',marginTop:10}}>
                <button className="btn btn-primary" onClick={sendPS} disabled={busy||!selected||!psScript.trim()}>
                  <Send size={13}/> {busy?'Sending…':'Send Script'}
                </button>
              </div>
            </div>
          </div>

          {/* Command history */}
          {selected && (
            <div className="card">
              <div className="card-header">
                <span className="card-title">Command History</span>
                <button className="btn btn-ghost btn-xs" style={{marginLeft:'auto'}} onClick={loadLog}><RefreshCw size={11}/></button>
              </div>
              {cmdLog.length===0 ? <div className="empty-state" style={{padding:'24px'}}><p>No commands yet</p></div> : (
                <div style={{overflowX:'auto',maxHeight:280,overflowY:'auto'}}>
                  <table className="data-table">
                    <thead><tr><th>Command</th><th>Status</th><th>Issued At</th><th>Delivered At</th></tr></thead>
                    <tbody>
                      {cmdLog.map(c=>(
                        <tr key={c.id}>
                          <td className="mono" style={{fontWeight:600,fontSize:12}}>{c.command}</td>
                          <td><span className={`badge ${statusBadge(c.status)}`}>{c.status}</span></td>
                          <td style={{fontSize:11,color:'var(--c-muted)'}}>{new Date(c.issued_at).toLocaleString()}</td>
                          <td style={{fontSize:11,color:'var(--c-muted)'}}>{c.delivered_at?new Date(c.delivered_at).toLocaleString():'—'}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
