import React, { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { Radar, ShieldCheck, TerminalSquare, Usb } from 'lucide-react';
import AmbientField from '../components/AmbientField';
import ThemeToggle from '../components/ThemeToggle';

function GlassSpec() { return <div className="spec" />; }

const FEATURES = [
  { tag: '01 · DISCOVERY', Icon: Radar, title: 'Authenticated discovery',
    body: 'Agents find the admin over the LAN with an HMAC-signed handshake. A spoofed "I\'m the admin" packet is rejected and logged — no hijacking.' },
  { tag: '02 · ENFORCEMENT', Icon: ShieldCheck, title: 'Wired-LAN-only',
    body: 'Wi-Fi, VPNs and virtual adapters are refused for control traffic. Every socket is bound to a real Ethernet interface, enforced on both sides.' },
  { tag: '03 · CONTROL', Icon: TerminalSquare, title: 'Real-time command tracking',
    body: 'Restart, shutdown, lock USB — every command is queued, delivered and acknowledged, with the full trail from issue to confirmation.' },
  { tag: '04 · POLICY', Icon: Usb, title: 'USB & device control',
    body: 'Block mass-storage the instant it\'s inserted, per endpoint or fleet-wide, with a forensic record of every device event.' },
];

export default function LandingPage() {
  const navigate = useNavigate();
  const navRef = useRef(null);

  useEffect(() => {
    const onScroll = () => { if (navRef.current) navRef.current.classList.toggle('scrolled', window.scrollY > 16); };
    window.addEventListener('scroll', onScroll, { passive: true }); onScroll();

    const rm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const els = Array.from(document.querySelectorAll('.reveal'));
    if (rm) { els.forEach(el => el.classList.add('in')); }
    else {
      const io = new IntersectionObserver(entries => {
        entries.forEach(e => { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } });
      }, { rootMargin: '0px 0px -6% 0px', threshold: 0.08 });
      els.forEach((el, i) => { el.style.transitionDelay = Math.min(i * 80, 560) + 'ms'; io.observe(el); });
      return () => { io.disconnect(); window.removeEventListener('scroll', onScroll); };
    }
    return () => window.removeEventListener('scroll', onScroll);
  }, []);

  const scrollTo = id => document.getElementById(id)?.scrollIntoView({ behavior: 'smooth' });

  return (
    <div className="lp">
      <AmbientField />

      <nav className="lp-nav glass" ref={navRef}><GlassSpec />
        <div className="lp-brand"><span className="lp-mark"><img src="/rasd-logo-192.png" alt="" style={{ width:'100%', height:'100%', borderRadius:'inherit', display:'block' }} /></span><span>RASD<small>SECURE OPS</small></span></div>
        <div className="lp-links">
          <button type="button" className="linkish" onClick={() => scrollTo('lp-feat')}>Platform</button>
          <button type="button" className="linkish" onClick={() => scrollTo('lp-prev')}>Dashboard</button>
          <ThemeToggle />
          <button className="btn btn-primary" onClick={() => navigate('/login')}>Go to Dashboard</button>
        </div>
      </nav>

      <div className="lp-wrap">
        <section className="lp-hero">
          <div className="lp-lozenge" aria-hidden="true"><b /><b /></div>
          <p className="lp-eyebrow reveal">Endpoint security · wired-LAN only</p>
          <h1 className="reveal">Command every endpoint <span className="grad">through one pane of glass.</span></h1>
          <p className="lp-sub reveal">RASD discovers, authenticates and controls every machine on your wired network in real time — no agents lost, no Wi-Fi trusted, no command unaccounted for.</p>
          <div className="lp-cta reveal">
            <button className="btn btn-primary" onClick={() => navigate('/login')}>Go to Dashboard</button>
            <button className="btn btn-ghost" onClick={() => scrollTo('lp-feat')}>See how it works</button>
          </div>
          <div className="lp-cue reveal"><span className="ln" />SCROLL</div>
        </section>

        <section className="lp-features" id="lp-feat">
          <div className="lp-frow">
            {FEATURES.slice(0, 2).map(f => <Feature key={f.tag} {...f} />)}
          </div>
          <div className="lp-frow">
            {FEATURES.slice(2, 4).map(f => <Feature key={f.tag} {...f} />)}
          </div>
        </section>

        <section className="lp-prev" id="lp-prev">
          <h2 className="reveal">Built for the operations floor.</h2>
          <p className="reveal">Dense where it counts, calm where it doesn't. Live status you can read across the room.</p>
          <div className="lp-frame glass reveal"><GlassSpec />
            <div className="inner">Live operations dashboard — sign in to view your fleet</div>
          </div>
        </section>

        <section className="lp-foot">
          <div className="big reveal">Your network, accounted for.</div>
          <button className="btn btn-primary reveal" onClick={() => navigate('/login')}>Go to Dashboard</button>
          <p style={{ marginTop: 34 }}>RASD Secure Operations · wired-LAN endpoint security</p>
        </section>
      </div>
    </div>
  );
}

function Feature({ tag, Icon, title, body }) {
  return (
    <div className="lp-feat reveal">
      <span className="tag">{tag}</span>
      <div className="ic"><Icon size={22} aria-hidden="true" /></div>
      <h3>{title}</h3>
      <p>{body}</p>
    </div>
  );
}
