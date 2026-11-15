import React from 'react';
import { useLocation } from 'react-router-dom';
import Sidebar from './Sidebar';
import ThemeToggle from './ThemeToggle';

const TITLES = {
  '/dashboard': ['Overview',          'System health and live summary'],
  '/endpoints': ['Endpoints',         'Connected client machines'],
  '/alerts':    ['Alerts',            'Security notifications and events'],
  '/remote':    ['Remote Admin',      'Send commands to connected endpoints'],
  '/security':  ['Security',          'Threat events and access control'],
  '/reports':   ['Reports',           'Generate and export activity reports'],
  '/settings':  ['Settings',          'Platform configuration and preferences'],
  '/logs':      ['System Logs',       'Full audit log viewer'],
};

export default function Layout({ children }) {
  const { pathname } = useLocation();

  const key   = Object.keys(TITLES).find(k => pathname.startsWith(k)) || '/dashboard';
  const [title, sub] = TITLES[key] || ['RASD', ''];

  const user = (() => { try { return JSON.parse(localStorage.getItem('user') || '{}'); } catch { return {}; } })();

  return (
    <div className="app-shell">
      <Sidebar />

      <div className="main-area">
        {/* Top bar */}
        <div className="topbar glass">
          <div style={{ flex:1 }}>
            <div style={{ fontSize:15, fontWeight:600, color:'var(--c-text)' }}>{title}</div>
            <div style={{ fontSize:11, color:'var(--c-muted)' }}>{sub}</div>
          </div>

          <div style={{ display:'flex', alignItems:'center', gap:14 }}>
            {/* Gooey liquid-glass Light/Dark toggle */}
            <ThemeToggle />

            {/* User chip */}
            <div style={{ display:'flex', alignItems:'center', gap:8, background:'var(--c-panel)', border:'1px solid var(--c-border)', borderRadius:6, padding:'5px 12px' }}>
              <div style={{ width:26, height:26, borderRadius:'50%', background:'var(--c-accent-bg)', border:'1px solid var(--c-accent)', display:'flex', alignItems:'center', justifyContent:'center', fontSize:11, fontWeight:700, color:'var(--c-accent)' }}>
                {(user.username || 'A')[0].toUpperCase()}
              </div>
              <div>
                <div style={{ fontSize:12, fontWeight:600, color:'var(--c-text)' }}>{user.username || 'Admin'}</div>
                <div style={{ fontSize:10, color:'var(--c-muted)', textTransform:'capitalize' }}>{user.role || 'admin'}</div>
              </div>
            </div>
          </div>
        </div>

        {/* Page content */}
        <div className="page-content">
          {children}
        </div>
      </div>
    </div>
  );
}
