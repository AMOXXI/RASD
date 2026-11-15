import React, { useState, useEffect, useCallback } from 'react';
import { Shield, Users, AlertTriangle, Plus, Trash2, RefreshCw, X } from 'lucide-react';
import { getAllForensics, getUsers, deleteUser, updateUserRole, createUser } from '../services/api';
import { useApp } from '../App';

export default function SecurityPage() {
  const { toast } = useApp();
  const [tab,       setTab]      = useState('events');
  const [events,    setEvents]   = useState([]);
  const [users,     setUsers]    = useState([]);
  const [sevFilter, setSevFilter]= useState('ALL');
  const [loading,   setLoading]  = useState(true);
  const [addModal,  setAddModal] = useState(false);
  const [newUser,   setNewUser]  = useState({ username:'', email:'', password:'', role:'operator' });
  const [busy,      setBusy]     = useState(false);

  const loadEvents = useCallback(async () => {
    try { const d = await getAllForensics(sevFilter, 300); setEvents(d.events||[]); }
    catch {} setLoading(false);
  }, [sevFilter]);

  const loadUsers = useCallback(async () => {
    try { const d = await getUsers(); setUsers(d.users||[]); }
    catch {}
  }, []);

  useEffect(() => { loadEvents(); }, [loadEvents]);
  useEffect(() => { if (tab==='access') loadUsers(); }, [tab, loadUsers]);

  const handleDelete = async id => {
    if (!confirm('Delete this user?')) return;
    try { await deleteUser(id); setUsers(p=>p.filter(u=>u.user_id!==id)); toast('User deleted','info'); }
    catch (e) { toast(e.response?.data?.error||'Failed','error'); }
  };

  const handleRoleChange = async (id, role) => {
    try { await updateUserRole(id, role); setUsers(p=>p.map(u=>u.user_id===id?{...u,role}:u)); toast('Role updated','info'); }
    catch (e) { toast('Failed','error'); }
  };

  const handleCreate = async () => {
    if (!newUser.username||!newUser.password) { toast('Username and password required','error'); return; }
    setBusy(true);
    try {
      await createUser(newUser.username, newUser.email, newUser.password, newUser.role);
      toast('User created','info'); setAddModal(false); setNewUser({username:'',email:'',password:'',role:'operator'}); loadUsers();
    } catch (e) { toast(e.response?.data?.message||'Failed','error'); }
    setBusy(false);
  };

  const sevBadge = s => `badge badge-${s==='CRITICAL'?'critical':s==='WARN'?'warn':'info'}`;

  return (
    <div>
      <div className="page-header">
        <div><h1 className="page-title">Security</h1><p className="page-sub">Threat events and access control</p></div>
        <button className="btn btn-ghost btn-sm" onClick={tab==='events'?loadEvents:loadUsers}><RefreshCw size={13}/></button>
      </div>

      <div className="tabs">
        {[['events','Security Events'],['access','Access Control']].map(([k,l])=>(
          <div key={k} className={`tab${tab===k?' active':''}`} onClick={()=>setTab(k)}>{l}</div>
        ))}
      </div>

      {tab==='events' && (
        <>
          <div className="filter-bar">
            {['ALL','CRITICAL','WARN','INFO'].map(s=>(
              <button key={s} className={`btn btn-sm ${sevFilter===s?'btn-primary':'btn-ghost'}`} onClick={()=>setSevFilter(s)}>{s}</button>
            ))}
            <span style={{marginLeft:'auto',fontSize:12,color:'var(--c-muted)'}}>{events.length} events</span>
          </div>
          <div className="card">
            {loading ? <div style={{display:'flex',justifyContent:'center',padding:60}}><div className="spinner" style={{width:28,height:28}}/></div>
            : events.length===0 ? <div className="empty-state"><Shield size={32} style={{color:'var(--c-online)'}}/><h3>No events</h3><p>All clear</p></div>
            : (
              <div style={{overflowX:'auto'}}>
                <table className="data-table">
                  <thead><tr><th>Severity</th><th>Event Type</th><th>Endpoint</th><th>IP</th><th>File</th><th>Hash</th><th>Time</th></tr></thead>
                  <tbody>
                    {events.map(e=>(
                      <tr key={e.id}>
                        <td><span className={sevBadge(e.severity)}>{e.severity}</span></td>
                        <td style={{fontWeight:600,fontSize:12}}>{e.event_type}</td>
                        <td style={{fontSize:12}}>{e.hostname||'—'}</td>
                        <td className="mono" style={{fontSize:11,color:'var(--c-muted)'}}>{e.ip_address||'—'}</td>
                        <td className="mono truncate-cell" style={{fontSize:11}}>{e.file_name||'—'}</td>
                        <td className="mono" style={{fontSize:10,color:'var(--c-dim)'}}>{e.file_hash?.substring(0,16)||'—'}</td>
                        <td style={{fontSize:11,color:'var(--c-muted)'}}>{new Date(e.logged_at).toLocaleString()}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}

      {tab==='access' && (
        <>
          <div style={{display:'flex',justifyContent:'flex-end',marginBottom:16}}>
            <button className="btn btn-primary btn-sm" onClick={()=>setAddModal(true)}><Plus size={13}/> Add User</button>
          </div>
          <div className="card">
            <div style={{overflowX:'auto'}}>
              <table className="data-table">
                <thead><tr><th>Username</th><th>Email</th><th>Role</th><th>Created</th><th></th></tr></thead>
                <tbody>
                  {users.map(u=>(
                    <tr key={u.user_id}>
                      <td style={{fontWeight:600}}>{u.username}</td>
                      <td style={{fontSize:12,color:'var(--c-muted)'}}>{u.email||'—'}</td>
                      <td>
                        <select className="input" style={{width:'auto',padding:'4px 8px',fontSize:11}}
                          value={u.role} onChange={e=>handleRoleChange(u.user_id,e.target.value)}>
                          {['admin','operator','viewer'].map(r=><option key={r} value={r}>{r}</option>)}
                        </select>
                      </td>
                      <td style={{fontSize:11,color:'var(--c-muted)'}}>{new Date(u.created_at).toLocaleDateString()}</td>
                      <td>
                        <button className="btn btn-danger btn-xs" onClick={()=>handleDelete(u.user_id)} title="Delete user">
                          <Trash2 size={11}/>
                        </button>
                      </td>
                    </tr>
                  ))}
                  {users.length===0 && <tr><td colSpan={5}><div className="empty-state" style={{padding:32}}><Users size={28}/><p>No users</p></div></td></tr>}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}

      {addModal && (
        <div className="modal-overlay" onClick={()=>setAddModal(false)}>
          <div className="modal" onClick={e=>e.stopPropagation()}>
            <div className="modal-header">
              <span style={{fontWeight:600}}>Add New User</span>
              <button onClick={()=>setAddModal(false)} className="btn btn-ghost btn-sm"><X size={14}/></button>
            </div>
            <div className="modal-body" style={{display:'flex',flexDirection:'column',gap:14}}>
              {[['Username','text','username','admin'],['Email','email','email','user@domain.com'],['Password','password','password','••••••••']].map(([l,t,k,ph])=>(
                <div key={k}>
                  <label className="input-label">{l}</label>
                  <input className="input" type={t} placeholder={ph} value={newUser[k]} onChange={e=>setNewUser(p=>({...p,[k]:e.target.value}))}/>
                </div>
              ))}
              <div>
                <label className="input-label">Role</label>
                <select className="input" value={newUser.role} onChange={e=>setNewUser(p=>({...p,role:e.target.value}))}>
                  {['admin','operator','viewer'].map(r=><option key={r} value={r}>{r}</option>)}
                </select>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn btn-ghost" onClick={()=>setAddModal(false)}>Cancel</button>
              <button className="btn btn-primary" onClick={handleCreate} disabled={busy}>{busy?'Creating…':'Create User'}</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
