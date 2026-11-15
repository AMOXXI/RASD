import React from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import {
  LayoutDashboard, Monitor, Bell, Terminal,
  Shield, FileBarChart2, Settings2, ScrollText, LogOut
} from 'lucide-react';
import { useApp } from '../App';

export default function Sidebar() {
  const { unread } = useApp();
  const navigate = useNavigate();

  const handleLogout = () => {
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    navigate('/login');
  };

  const nav = [
    { to: '/dashboard', icon: LayoutDashboard, label: 'Overview' },
    { to: '/endpoints', icon: Monitor,         label: 'Endpoints' },
    { to: '/alerts',    icon: Bell,             label: 'Alerts',     badge: unread },
    { to: '/remote',    icon: Terminal,         label: 'Remote Admin' },
    { to: '/security',  icon: Shield,           label: 'Security' },
    { to: '/reports',   icon: FileBarChart2,    label: 'Reports' },
    { to: '/settings',  icon: Settings2,        label: 'Settings' },
    { to: '/logs',      icon: ScrollText,       label: 'Logs' },
  ];

  return (
    <aside className="sidebar glass">
      {/* Brand */}
      <div style={{ padding:'20px 16px 16px', borderBottom:'1px solid var(--c-border)' }}>
        <div style={{ display:'flex', alignItems:'center', gap:11 }}>
          <div style={{ width:32, height:32, borderRadius:9, display:'grid', placeItems:'center', fontWeight:800,
            color:'var(--on-accent)', background:'linear-gradient(135deg,var(--accent),var(--accent-2))',
            boxShadow:'0 0 16px color-mix(in srgb,var(--accent) 45%,transparent)' }}>
            <img src="/rasd-logo-192.png" alt="" style={{ width:'100%', height:'100%', borderRadius:'inherit', display:'block' }} />
          </div>
          <div>
            <div style={{ fontSize:16, fontWeight:700, color:'var(--c-text)', letterSpacing:'0.05em' }}>RASD</div>
            <div style={{ fontSize:10, color:'var(--c-accent)', letterSpacing:'0.06em', fontFamily:'var(--font-mono)' }}>SECURE OPS</div>
          </div>
        </div>
      </div>

      {/* Nav */}
      <nav style={{ flex:1, padding:'10px 0' }}>
        {nav.map(({ to, icon: Icon, label, badge }) => (
          <NavLink key={to} to={to}
            className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}>
            <Icon size={16} />
            <span>{label}</span>
            {badge > 0 && <span className="count">{badge > 99 ? '99+' : badge}</span>}
          </NavLink>
        ))}
      </nav>

      {/* Footer */}
      <div style={{ borderTop:'1px solid var(--c-border)', padding:'12px 16px' }}>
        <button
          onClick={handleLogout}
          className="nav-item"
          style={{ width:'100%', border:'none', background:'none', cursor:'pointer', padding:'8px 10px' }}>
          <LogOut size={15} />
          <span>Logout</span>
        </button>
      </div>
    </aside>
  );
}
