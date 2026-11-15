import axios from 'axios';

const api = axios.create({ baseURL: '' });

api.interceptors.request.use(cfg => {
  const token = localStorage.getItem('token');
  if (token) cfg.headers.Authorization = `Bearer ${token}`;
  return cfg;
});

api.interceptors.response.use(
  r => r,
  err => {
    if (err.response?.status === 401) {
      localStorage.removeItem('token');
      localStorage.removeItem('user');
      window.location.href = '/login';
    }
    return Promise.reject(err);
  }
);

export const login            = (u, p)    => api.post('/api/v1/auth/login', { username: u, password: p }).then(r => r.data);
export const getHealth        = ()        => api.get('/api/health').then(r => r.data);
export const getNodes         = ()        => api.get('/api/v1/nodes').then(r => r.data);
export const getNodeForensics = (id, l=100) => api.get(`/api/nodes/${id}/forensics?limit=${l}`).then(r => r.data);
export const getAllForensics   = (sev='ALL',l=200) => api.get(`/api/nodes/forensics/all?severity=${sev}&limit=${l}`).then(r => r.data);
export const blockUsb         = id        => api.post(`/api/command/block-usb/${id}`).then(r => r.data);
export const allowUsb         = id        => api.post(`/api/command/allow-usb/${id}`).then(r => r.data);
export const restartDevice    = id        => api.post(`/api/command/restart/${id}`).then(r => r.data);
export const shutdownDevice   = id        => api.post(`/api/command/shutdown/${id}`).then(r => r.data);
export const updateWindows    = id        => api.post(`/api/command/update-windows/${id}`).then(r => r.data);
export const updateWindowsAll = ()        => api.post('/api/command/update-windows-all').then(r => r.data);
export const execPowerShell   = (id, script) => api.post(`/api/command/exec-ps/${id}`, { script }).then(r => r.data);
export const getCommandLog    = (id, l=100)  => api.get(`/api/command/log/${id}?limit=${l}`).then(r => r.data);
export const getAlerts        = (l=200, unread=false) => api.get(`/api/v1/alerts?limit=${l}&unread=${unread}`).then(r => r.data);
export const getUnreadCount   = ()        => api.get('/api/v1/alerts/unread-count').then(r => r.data);
export const markAlertRead    = id        => api.put(`/api/v1/alerts/${id}/read`).then(r => r.data);
export const markAllRead      = ()        => api.put('/api/v1/alerts/mark-all-read').then(r => r.data);
export const getLogs = (p = {}) => {
  const q = new URLSearchParams({ limit: p.limit||500, type: p.type||'all', severity: p.severity||'ALL', from: p.from||new Date(Date.now()-7*86400000).toISOString(), to: p.to||new Date().toISOString() });
  return api.get(`/api/v1/logs?${q}`).then(r => r.data);
};
export const getReportSummary   = (f,t)   => api.get(`/api/v1/reports/summary?from=${f}&to=${t}`).then(r => r.data);
export const getActivityReport  = (f,t)   => api.get(`/api/v1/reports/activity?from=${f}&to=${t}`).then(r => r.data);
export const getAlertsReport    = (f,t)   => api.get(`/api/v1/reports/alerts?from=${f}&to=${t}`).then(r => r.data);
export const getEndpointsReport = ()      => api.get('/api/v1/reports/endpoints').then(r => r.data);
export const getUsers           = ()      => api.get('/api/v1/users').then(r => r.data);
export const deleteUser         = id      => api.delete(`/api/v1/users/${id}`).then(r => r.data);
export const updateUserRole     = (id,role) => api.put(`/api/v1/users/${id}/role`, { role }).then(r => r.data);
export const resetUserPassword  = (id,pw)   => api.post(`/api/v1/users/${id}/reset-password`, { newPassword: pw }).then(r => r.data);
export const createUser         = (u,e,p,r='operator') => api.post('/api/v1/auth/register', { username:u, email:e, password:p, role:r }).then(r => r.data);

export default api;
