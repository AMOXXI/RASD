import React, { useState, useEffect, createContext, useContext } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { io } from 'socket.io-client';
import { getUnreadCount } from './services/api';

import Layout       from './components/Layout';
import LandingPage  from './pages/LandingPage';
import LoginPage    from './pages/LoginPage';
import OverviewPage from './pages/OverviewPage';
import EndpointsPage    from './pages/EndpointsPage';
import EndpointDetailPage from './pages/EndpointDetailPage';
import AlertsPage   from './pages/AlertsPage';
import RemotePage   from './pages/RemotePage';
import SecurityPage from './pages/SecurityPage';
import ReportsPage  from './pages/ReportsPage';
import SettingsPage from './pages/SettingsPage';
import LogsPage     from './pages/LogsPage';

// ── App Context (socket + theme + alerts) ──────────────────────
export const AppCtx = createContext({});
export const useApp = () => useContext(AppCtx);

function Protected({ children }) {
  const token = localStorage.getItem('token');
  if (!token) return <Navigate to="/login" replace />;
  return children;
}

export default function App() {
  const [socket,       setSocket]       = useState(null);
  const [unread,       setUnread]       = useState(0);
  const [theme,        setTheme]        = useState(() => localStorage.getItem('theme') || 'dark');
  const [toasts,       setToasts]       = useState([]);

  // Apply theme to <html>
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme === 'light' ? 'light' : '');
    localStorage.setItem('theme', theme);
  }, [theme]);

  const toggleTheme = () => setTheme(t => t === 'dark' ? 'light' : 'dark');

  // Toast helper
  const toast = (msg, type = 'info') => {
    const id = Date.now();
    setToasts(prev => [...prev, { id, msg, type }]);
    setTimeout(() => setToasts(prev => prev.filter(t => t.id !== id)), 4000);
  };

  // Socket connection
  useEffect(() => {
    const token = localStorage.getItem('token');
    if (!token) return;

    const s = io(window.location.origin || 'http://localhost:3000', {
      query: { role: 'dashboard' },
      transports: ['websocket', 'polling'],
    });

    s.on('connect',    () => console.log('[WS] Dashboard connected'));
    s.on('disconnect', () => console.log('[WS] Dashboard disconnected'));
    s.on('alert:new',  (data) => {
      setUnread(prev => prev + 1);
      toast(`Alert: ${data?.message || 'New security alert'}`, 'warn');
    });
    s.on('node:update', () => {}); // pages subscribe individually

    setSocket(s);
    return () => s.disconnect();
  }, []);

  // Poll unread count
  useEffect(() => {
    const token = localStorage.getItem('token');
    if (!token) return;
    const fetch = async () => {
      try { const d = await getUnreadCount(); if (d.success) setUnread(Number(d.count)); }
      catch {}
    };
    fetch();
    const iv = setInterval(fetch, 30000);
    return () => clearInterval(iv);
  }, []);

  const ctx = { socket, unread, setUnread, theme, toggleTheme, toast };

  return (
    <AppCtx.Provider value={ctx}>
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<LandingPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/*" element={
            <Protected>
              <Layout>
                <Routes>
                  <Route path="/dashboard"        element={<OverviewPage />} />
                  <Route path="/endpoints"        element={<EndpointsPage />} />
                  <Route path="/endpoints/:id"    element={<EndpointDetailPage />} />
                  <Route path="/alerts"           element={<AlertsPage />} />
                  <Route path="/remote"           element={<RemotePage />} />
                  <Route path="/security"         element={<SecurityPage />} />
                  <Route path="/reports"          element={<ReportsPage />} />
                  <Route path="/settings"         element={<SettingsPage />} />
                  <Route path="/logs"             element={<LogsPage />} />
                  <Route path="*" element={<Navigate to="/dashboard" replace />} />
                </Routes>
              </Layout>
            </Protected>
          } />
        </Routes>
      </BrowserRouter>

      {/* Global toast panel */}
      <div role="status" aria-live="polite" style={{ position:'fixed', bottom:20, right:20, zIndex:9999, display:'flex', flexDirection:'column', gap:8 }}>
        {toasts.map(t => (
          <div key={t.id} className="toast"
               style={{ borderLeft: `3px solid ${t.type==='warn'?'var(--c-warn)':t.type==='error'?'var(--c-offline)':'var(--c-info)'}` }}>
            {t.msg}
          </div>
        ))}
      </div>
    </AppCtx.Provider>
  );
}
