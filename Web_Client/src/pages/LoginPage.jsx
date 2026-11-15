import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { User, Lock, ArrowLeft } from 'lucide-react';
import { login } from '../services/api';
import AmbientField from '../components/AmbientField';
import ThemeToggle from '../components/ThemeToggle';

export default function LoginPage() {
  const navigate = useNavigate();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error,    setError]    = useState('');
  const [loading,  setLoading]  = useState(false);

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError(''); setLoading(true);
    try {
      const data = await login(username, password);
      if (data.success) {
        localStorage.setItem('token', data.token);
        localStorage.setItem('user',  JSON.stringify(data.user));
        navigate('/dashboard');
      } else {
        setError(data.message || 'Authentication failed');
      }
    } catch (err) {
      setError(err.response?.data?.message || 'Cannot connect to RASD server');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="lp">
      <AmbientField />

      <div className="auth-top"><ThemeToggle /></div>
      <button className="btn btn-ghost"
        style={{ position:'fixed', top:20, left:20, zIndex:40 }}
        onClick={() => navigate('/')}>
        <ArrowLeft size={14} /> Back to site
      </button>

      <div className="auth-wrap">
        <div className="auth-card glass"><div className="spec" />
          <div style={{ textAlign:'center' }}>
            <div style={{ width:44, height:44, borderRadius:12, margin:'0 auto 4px', display:'grid', placeItems:'center',
              fontWeight:800, fontSize:20, color:'var(--on-accent)',
              background:'linear-gradient(135deg,var(--accent),var(--accent-2))',
              boxShadow:'0 0 20px color-mix(in srgb,var(--accent) 50%,transparent)' }}>
              <img src="/rasd-logo-192.png" alt="RASD" style={{ width:'100%', height:'100%', borderRadius:'inherit', display:'block' }} />
            </div>
          </div>
          <h2 style={{ textAlign:'center', fontSize:24, margin:'16px 0 0' }}>Sign in to Operations</h2>
          <p style={{ textAlign:'center', color:'var(--c-muted)', fontSize:14, margin:'8px 0 24px' }}>
            Wired-LAN access · authenticate to continue
          </p>

          {error && (
            <div role="alert" style={{ display:'flex', alignItems:'center', gap:8, background:'var(--c-offline-bg)',
              border:'1px solid color-mix(in srgb,var(--crit) 40%,transparent)', color:'var(--crit)',
              borderRadius:'var(--r-md)', padding:'10px 12px', fontSize:12.5, marginBottom:14,
              fontFamily:'var(--font-mono)' }}>
              {error}
            </div>
          )}

          <form onSubmit={handleSubmit}>
            <div style={{ marginBottom:14 }}>
              <label className="input-label" htmlFor="login-user">Username</label>
              <div className="search-box">
                <User size={16} style={{ color:'var(--c-dim)', flexShrink:0 }} aria-hidden="true" />
                <input id="login-user" name="username" placeholder="admin" value={username}
                  onChange={e => setUsername(e.target.value)} autoComplete="username" spellCheck={false} autoFocus />
              </div>
            </div>
            <div style={{ marginBottom:20 }}>
              <label className="input-label" htmlFor="login-pass">Password</label>
              <div className="search-box">
                <Lock size={16} style={{ color:'var(--c-dim)', flexShrink:0 }} aria-hidden="true" />
                <input id="login-pass" name="password" type="password" placeholder="••••••••" value={password}
                  onChange={e => setPassword(e.target.value)} autoComplete="current-password" />
              </div>
            </div>
            <button type="submit" className="btn btn-primary"
              style={{ width:'100%', justifyContent:'center', padding:'13px' }} disabled={loading}>
              {loading ? <><span className="spinner" style={{ width:14, height:14 }} /> Authenticating…</> : 'Enter dashboard'}
            </button>
          </form>

          <div style={{ textAlign:'center', marginTop:18, fontSize:11, color:'var(--c-dim)' }}>
            RASD Secure Operations · Graduation Project
          </div>
        </div>
      </div>
    </div>
  );
}
