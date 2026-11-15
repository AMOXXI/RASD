import { useState, useCallback } from 'react';
import axios from 'axios';
import { PowerOff, Usb, Eye, Loader2, CheckCircle2, XCircle } from 'lucide-react';
import ForensicsModal from './ForensicsModal.jsx';

// ---- Relative-time formatter ----
function timeAgo(ts) {
  if (!ts) return '—';
  const sec = Math.floor((Date.now() - new Date(ts)) / 1000);
  if (sec < 5)   return 'just now';
  if (sec < 60)  return `${sec}s ago`;
  if (sec < 3600)return `${Math.floor(sec/60)}m ago`;
  return `${Math.floor(sec/3600)}h ago`;
}

// ---- Command feedback button ----
function CmdButton({ label, icon: Icon, className, onClick, loading, done, failed }) {
  return (
    <button
      onClick={onClick}
      disabled={loading || done}
      className={`btn-cmd flex items-center gap-1.5 ${className}`}
      title={label}
    >
      {loading ? <Loader2 className="w-3 h-3 animate-spin" /> :
       done    ? <CheckCircle2 className="w-3 h-3 text-online" /> :
       failed  ? <XCircle className="w-3 h-3 text-offline" /> :
                 <Icon className="w-3 h-3" />}
      <span className="hidden lg:inline">{label}</span>
    </button>
  );
}

// ---- Single fleet row ----
function NodeRow({ node, onNodeUpdate }) {
  const [usbLoading, setUsbLoading]     = useState(false);
  const [usbDone, setUsbDone]           = useState(false);
  const [restartLoading, setRestartLoad]= useState(false);
  const [restartDone, setRestartDone]   = useState(false);
  const [forensicsOpen, setForensicsOpen] = useState(false);

  const handleToggleUsb = useCallback(async () => {
    setUsbLoading(true); setUsbDone(false);
    const endpoint = node.usb_status === 'BLOCKED' ? 'allow-usb' : 'block-usb';
    try {
      await axios.post(`/api/command/${endpoint}/${node.id}`);
      onNodeUpdate(node.id, {
        usb_status: node.usb_status === 'BLOCKED' ? 'ALLOWED' : 'BLOCKED',
      });
      setUsbDone(true);
      setTimeout(() => setUsbDone(false), 3000);
    } catch (err) {
      console.error('[USB]', err.message);
    } finally {
      setUsbLoading(false);
    }
  }, [node, onNodeUpdate]);

  const handleRestart = useCallback(async () => {
    if (!window.confirm(`Restart ${node.hostname}?`)) return;
    setRestartLoad(true); setRestartDone(false);
    try {
      await axios.post(`/api/command/restart/${node.id}`);
      setRestartDone(true);
      setTimeout(() => setRestartDone(false), 5000);
    } catch (err) {
      console.error('[RESTART]', err.message);
    } finally {
      setRestartLoad(false);
    }
  }, [node, onNodeUpdate]);

  const isOnline = node.online;
  const hasAlerts = parseInt(node.critical_alerts) > 0;

  return (
    <>
      <tr className="group animate-fade-in">
        {/* Status dot */}
        <td className="pl-6">
          <span className={`inline-block w-2.5 h-2.5 rounded-full glow-dot ${isOnline ? 'bg-online' : 'bg-offline'}`} />
        </td>

        {/* Hostname */}
        <td>
          <div className="flex items-center gap-2">
            <span className={`font-mono font-semibold text-sm ${isOnline ? 'text-white' : 'text-muted'}`}>
              {node.hostname || 'Unknown'}
            </span>
            {hasAlerts && (
              <span className="px-1.5 py-0.5 text-[10px] font-mono font-bold rounded bg-offline/20 text-offline border border-offline/30">
                {node.critical_alerts} ALERT{node.critical_alerts > 1 ? 'S' : ''}
              </span>
            )}
          </div>
          <div className="text-[11px] text-muted font-mono mt-0.5">{node.mac_address}</div>
        </td>

        {/* IP */}
        <td>
          <span className="font-mono text-sm text-ghost">{node.ip_address || '—'}</span>
        </td>

        {/* OS */}
        <td className="hidden xl:table-cell">
          <span className="text-xs text-dim">{node.os_version || '—'}</span>
        </td>

        {/* USB Status */}
        <td>
          <span className={[
            'inline-flex items-center gap-1.5 px-2.5 py-1 rounded text-[11px] font-mono font-medium border',
            node.usb_status === 'BLOCKED'
              ? 'bg-offline/10 border-offline/30 text-offline'
              : 'bg-online/10  border-online/30  text-online',
          ].join(' ')}>
            <Usb className="w-3 h-3" />
            {node.usb_status}
          </span>
        </td>

        {/* Last Seen */}
        <td className="hidden md:table-cell">
          <span className="text-xs font-mono text-dim">{timeAgo(node.last_seen)}</span>
        </td>

        {/* Actions */}
        <td className="pr-6">
          <div className="flex items-center gap-2">
            <CmdButton
              label="Restart"
              icon={PowerOff}
              className="btn-danger"
              onClick={handleRestart}
              loading={restartLoading}
              done={restartDone}
            />
            <CmdButton
              label={node.usb_status === 'BLOCKED' ? 'Unlock USB' : 'Lock USB'}
              icon={Usb}
              className={node.usb_status === 'BLOCKED' ? 'btn-primary' : 'btn-neutral'}
              onClick={handleToggleUsb}
              loading={usbLoading}
              done={usbDone}
            />
            <CmdButton
              label="Forensics"
              icon={Eye}
              className="btn-neutral"
              onClick={() => setForensicsOpen(true)}
              loading={false}
              done={false}
            />
          </div>
        </td>
      </tr>

      {/* Forensics Drawer */}
      {forensicsOpen && (
        <ForensicsModal
          node={node}
          onClose={() => setForensicsOpen(false)}
        />
      )}
    </>
  );
}

// ================================================================
// FLEET TABLE
// ================================================================
export default function FleetTable({ nodes, setNodes }) {
  const [search, setSearch] = useState('');
  const [filterOnline, setFilterOnline] = useState('all'); // all | online | offline

  const onNodeUpdate = useCallback((id, patch) => {
    setNodes(prev => prev.map(n => n.id === id ? { ...n, ...patch } : n));
  }, [setNodes]);

  const filtered = nodes
    .filter(n => {
      if (filterOnline === 'online'  && !n.online) return false;
      if (filterOnline === 'offline' && n.online)  return false;
      if (search) {
        const q = search.toLowerCase();
        return (
          (n.hostname    || '').toLowerCase().includes(q) ||
          (n.ip_address  || '').toLowerCase().includes(q) ||
          (n.mac_address || '').toLowerCase().includes(q)
        );
      }
      return true;
    });

  return (
    <div className="panel flex flex-col overflow-hidden">
      {/* Table Header Controls */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3 p-5 border-b border-border">
        <div>
          <h2 className="text-sm font-mono font-semibold text-white tracking-wider uppercase">
            Managed Fleet
          </h2>
          <p className="text-xs text-dim mt-0.5">
            {nodes.filter(n => n.online).length} online · {nodes.length} total
          </p>
        </div>

        <div className="flex items-center gap-2 flex-wrap">
          {/* Filter tabs */}
          {['all','online','offline'].map(f => (
            <button
              key={f}
              onClick={() => setFilterOnline(f)}
              className={[
                'px-3 py-1.5 text-xs font-mono uppercase tracking-wider rounded border transition-all',
                filterOnline === f
                  ? 'bg-accent/10 border-accent/40 text-accent'
                  : 'border-border text-dim hover:border-muted hover:text-ghost',
              ].join(' ')}
            >
              {f}
            </button>
          ))}

          {/* Search */}
          <input
            type="text"
            placeholder="Search nodes..."
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="px-3 py-1.5 bg-void border border-border rounded text-xs font-mono text-ghost placeholder:text-muted focus:outline-none focus:border-accent/40 w-40 transition-colors"
          />
        </div>
      </div>

      {/* Table */}
      <div className="overflow-auto flex-1">
        {filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-20 gap-3 text-dim">
            <span className="text-4xl">⬡</span>
            <p className="text-sm font-mono">No managed nodes found</p>
            <p className="text-xs text-muted">Agents will appear here once they check in</p>
          </div>
        ) : (
          <table className="fleet-table w-full">
            <thead>
              <tr>
                <th className="pl-6 w-10">Status</th>
                <th>Hostname</th>
                <th>IP Address</th>
                <th className="hidden xl:table-cell">OS</th>
                <th>USB Policy</th>
                <th className="hidden md:table-cell">Last Seen</th>
                <th className="pr-6">Actions</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(node => (
                <NodeRow key={node.id} node={node} onNodeUpdate={onNodeUpdate} />
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
