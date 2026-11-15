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
      <div className="sidebar-brand">
        <div className="sidebar-mark">
          <img src="/rasd-logo-small.png" alt="" />
        </div>
        <div>
          <div className="sidebar-name">RASD</div>
          <div className="sidebar-tag">Secure Operations</div>
        </div>
      </div>

      {/* Nav */}
      <nav className="sidebar-nav" aria-label="Main">
        {nav.map(({ to, icon: Icon, label, badge }) => (
          <NavLink key={to} to={to}
            className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}>
            <Icon size={18} aria-hidden="true" />
            <span>{label}</span>
            {badge > 0 && <span className="count">{badge > 99 ? '99+' : badge}</span>}
          </NavLink>
        ))}
      </nav>

      {/* Footer */}
      <div className="sidebar-foot">
        <button onClick={handleLogout} className="nav-item" style={{ width: 'calc(100% - 24px)' }}>
          <LogOut size={18} aria-hidden="true" />
          <span>Logout</span>
        </button>
      </div>
    </aside>
  );
}
