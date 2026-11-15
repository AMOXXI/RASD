import { useEffect, useState } from 'react';
import { ShieldCheck, Database, Wifi, AlertTriangle, RefreshCw } from 'lucide-react';

const STEPS = [
  { id: 'api',    label: 'Establishing API Connection',   icon: Wifi },
  { id: 'db',     label: 'Verifying Database Integrity',  icon: Database },
  { id: 'fleet',  label: 'Loading Managed Fleet',         icon: ShieldCheck },
];

export default function LoadingScreen({ state, onRetry }) {
  const [step, setStep]       = useState(0);
  const [stepState, setStepState] = useState('running'); // running | done | error
  const isError = state === 'error';

  useEffect(() => {
    if (isError) { setStepState('error'); return; }
    setStep(0); setStepState('running');
    const timers = STEPS.map((_, i) =>
      setTimeout(() => {
        setStep(i);
        if (i === STEPS.length - 1) setStepState('done');
      }, i * 900)
    );
    return () => timers.forEach(clearTimeout);
  }, [state, isError]);

  return (
    <div className="min-h-screen bg-void flex flex-col items-center justify-center relative overflow-hidden">
      {/* Scanline overlay */}
      <div className="scanline" aria-hidden />

      {/* Grid background */}
      <div
        className="absolute inset-0 opacity-[0.04]"
        style={{
          backgroundImage: 'linear-gradient(#F97316 1px, transparent 1px), linear-gradient(90deg, #F97316 1px, transparent 1px)',
          backgroundSize: '48px 48px',
        }}
        aria-hidden
      />

      {/* Radial glow */}
      <div
        className="absolute top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 w-[600px] h-[600px] rounded-full pointer-events-none"
        style={{ background: 'radial-gradient(circle, rgba(249,115,22,0.06) 0%, transparent 70%)' }}
        aria-hidden
      />

      <div className="relative z-10 flex flex-col items-center gap-12 px-6 max-w-md w-full animate-fade-in">

        {/* LOGO */}
        <div className="flex flex-col items-center gap-4">
          <div className="relative">
            <div className="w-20 h-20 border border-accent/30 rounded-full flex items-center justify-center bg-accent/5">
              <ShieldCheck className="w-9 h-9 text-accent" />
            </div>
            {!isError && (
              <div className="absolute inset-0 rounded-full border border-accent/20 animate-ping" aria-hidden />
            )}
          </div>
          <div className="text-center">
            <h1 className="text-3xl font-bold tracking-[0.15em] text-white font-mono">RASD</h1>
            <p className="text-xs text-dim font-mono tracking-widest mt-1 uppercase">
              Remote Administration Security Dashboard
            </p>
          </div>
        </div>

        {/* STEP LIST */}
        <div className="w-full space-y-3">
          {STEPS.map((s, i) => {
            const Icon = s.icon;
            const isDone   = !isError && i < step;
            const isActive = !isError && i === step;
            const isFailed = isError && i === step;

            return (
              <div
                key={s.id}
                className={[
                  'flex items-center gap-4 px-5 py-4 rounded-lg border transition-all duration-500',
                  isDone   ? 'bg-white/[0.03] border-border opacity-60'  : '',
                  isActive ? 'bg-accent/5 border-accent/30'              : '',
                  isFailed ? 'bg-offline/5 border-offline/30'            : '',
                  !isDone && !isActive && !isFailed ? 'bg-surface border-border opacity-30' : '',
                ].join(' ')}
              >
                {/* Icon area */}
                <div className={[
                  'w-8 h-8 rounded-md flex items-center justify-center flex-shrink-0',
                  isDone   ? 'bg-online/10'  : '',
                  isActive ? 'bg-accent/10'  : '',
                  isFailed ? 'bg-offline/10' : '',
                  !isDone && !isActive && !isFailed ? 'bg-border' : '',
                ].join(' ')}>
                  {isDone   ? <Icon className="w-4 h-4 text-online" /> :
                   isFailed ? <AlertTriangle className="w-4 h-4 text-offline" /> :
                              <Icon className={`w-4 h-4 ${isActive ? 'text-accent' : 'text-muted'}`} />}
                </div>

                {/* Label */}
                <span className={[
                  'text-sm font-mono flex-1',
                  isDone   ? 'text-dim'    : '',
                  isActive ? 'text-white'  : '',
                  isFailed ? 'text-offline': '',
                  !isDone && !isActive && !isFailed ? 'text-muted' : '',
                ].join(' ')}>
                  {s.label}
                </span>

                {/* Status indicator */}
                <span className="text-xs font-mono">
                  {isDone   && <span className="text-online">✓ OK</span>}
                  {isActive && !isError && (
                    <span className="flex gap-1">
                      {[0,1,2].map(d => (
                        <span
                          key={d}
                          className="w-1 h-1 rounded-full bg-accent animate-bounce"
                          style={{ animationDelay: `${d * 0.15}s` }}
                        />
                      ))}
                    </span>
                  )}
                  {isFailed && <span className="text-offline">✗ FAIL</span>}
                </span>
              </div>
            );
          })}
        </div>

        {/* ERROR STATE */}
        {isError && (
          <div className="w-full animate-slide-up">
            <div className="p-5 bg-offline/5 border border-offline/30 rounded-lg space-y-3">
              <div className="flex items-center gap-2 text-offline">
                <AlertTriangle className="w-4 h-4" />
                <span className="text-sm font-mono font-medium">CONNECTION FAILURE</span>
              </div>
              <p className="text-xs text-dim leading-relaxed">
                Cannot reach the RASD API Server. Ensure the backend is running on port 3000
                and PostgreSQL is active.
              </p>
              <code className="block text-xs font-mono text-muted bg-void px-3 py-2 rounded border border-border">
                cd API_Server &amp;&amp; npm start
              </code>
              <button
                onClick={onRetry}
                className="w-full flex items-center justify-center gap-2 px-4 py-2.5 bg-white/5 border border-border rounded text-sm font-mono text-ghost hover:bg-white/10 hover:text-white transition-all active:scale-[0.98]"
              >
                <RefreshCw className="w-3.5 h-3.5" />
                RETRY CONNECTION
              </button>
            </div>
          </div>
        )}

        {/* VERSION */}
        <p className="text-xs text-muted font-mono tracking-widest">
          RASD ENTERPRISE v2.0 — OPERATIONS CENTER
        </p>
      </div>
    </div>
  );
}
