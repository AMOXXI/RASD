import { useEffect, useState } from 'react';
import axios from 'axios';
import { X, FileSearch, AlertTriangle, Info, ShieldAlert } from 'lucide-react';

const SEVERITY_META = {
  CRITICAL: { color: 'text-offline', bg: 'bg-offline/10 border-offline/30', icon: ShieldAlert },
  WARN:     { color: 'text-warn',    bg: 'bg-warn/10    border-warn/30',    icon: AlertTriangle },
  INFO:     { color: 'text-dim',     bg: 'bg-white/5    border-border',     icon: Info },
};

function timeStamp(ts) {
  return ts ? new Date(ts).toLocaleString() : '—';
}

export default function ForensicsModal({ node, onClose }) {
  const [events, setEvents] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    const fetchEvents = async () => {
      try {
        const { data } = await axios.get(`/api/nodes/${node.id}/forensics?limit=100`);
        setEvents(data.events || []);
      } catch (err) {
        setError('Failed to load forensic data.');
      } finally {
        setLoading(false);
      }
    };
    fetchEvents();
  }, [node.id]);

  return (
    <tr>
      <td colSpan={7} className="p-0">
        {/* Modal overlay using a positioned div inside the table cell */}
        <div className="fixed inset-0 z-50 flex items-end sm:items-center justify-center p-4 bg-black/70 backdrop-blur-sm animate-fade-in">
          <div className="w-full max-w-3xl max-h-[80vh] flex flex-col bg-panel border border-border rounded-xl shadow-2xl animate-slide-up overflow-hidden">

            {/* Header */}
            <div className="flex items-center justify-between px-6 py-4 border-b border-border bg-surface flex-shrink-0">
              <div className="flex items-center gap-3">
                <div className="w-8 h-8 rounded-md bg-accent/10 flex items-center justify-center">
                  <FileSearch className="w-4 h-4 text-accent" />
                </div>
                <div>
                  <h3 className="text-sm font-mono font-semibold text-white">
                    Forensic Event Log
                  </h3>
                  <p className="text-xs text-dim mt-0.5 font-mono">{node.hostname} · {node.ip_address}</p>
                </div>
              </div>
              <button
                onClick={onClose}
                className="w-8 h-8 rounded-md border border-border flex items-center justify-center text-dim hover:text-white hover:border-muted transition-colors"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            {/* Body */}
            <div className="overflow-y-auto flex-1 p-4">
              {loading && (
                <div className="flex items-center justify-center py-16 gap-3 text-dim">
                  <div className="w-4 h-4 border-2 border-accent/40 border-t-accent rounded-full animate-spin" />
                  <span className="text-sm font-mono">Loading events...</span>
                </div>
              )}

              {error && (
                <div className="text-center py-16 text-offline text-sm font-mono">{error}</div>
              )}

              {!loading && !error && events.length === 0 && (
                <div className="text-center py-16 space-y-2">
                  <span className="text-4xl block">🛡</span>
                  <p className="text-sm font-mono text-dim">No forensic events recorded</p>
                  <p className="text-xs text-muted">This node is operating cleanly</p>
                </div>
              )}

              {!loading && events.length > 0 && (
                <div className="space-y-2">
                  {events.map(ev => {
                    const meta = SEVERITY_META[ev.severity] || SEVERITY_META.INFO;
                    const Icon = meta.icon;
                    return (
                      <div
                        key={ev.id}
                        className={`flex items-start gap-3 p-4 rounded-lg border ${meta.bg} transition-all`}
                      >
                        <Icon className={`w-4 h-4 mt-0.5 flex-shrink-0 ${meta.color}`} />
                        <div className="flex-1 min-w-0">
                          <div className="flex flex-wrap items-center gap-2 mb-1">
                            <span className={`text-xs font-mono font-bold ${meta.color}`}>
                              {ev.event_type}
                            </span>
                            <span className="text-[11px] text-muted font-mono">
                              {timeStamp(ev.logged_at)}
                            </span>
                          </div>
                          {ev.file_name && (
                            <p className="text-xs font-mono text-ghost truncate">{ev.file_name}</p>
                          )}
                          {ev.file_hash && (
                            <p className="text-[10px] font-mono text-muted mt-0.5 truncate">
                              SHA-256: {ev.file_hash}
                            </p>
                          )}
                          {ev.source_url && (
                            <p className="text-[10px] font-mono text-muted mt-0.5 truncate">
                              Source: {ev.source_url}
                            </p>
                          )}
                          {ev.details && (
                            <p className="text-xs text-dim mt-1">{ev.details}</p>
                          )}
                        </div>
                        {ev.file_size && (
                          <span className="text-[10px] font-mono text-muted flex-shrink-0">
                            {(ev.file_size / 1024).toFixed(1)} KB
                          </span>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>

            {/* Footer */}
            <div className="px-6 py-3 border-t border-border bg-surface flex-shrink-0">
              <p className="text-xs text-muted font-mono">
                {events.length} events · Showing latest 100
              </p>
            </div>
          </div>
        </div>
      </td>
    </tr>
  );
}
