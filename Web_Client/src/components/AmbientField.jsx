import React, { useEffect, useRef } from 'react';

/**
 * Self-contained ambient "security mesh" — drifting nodes + links on a canvas.
 * No external assets. Adapts to the active theme, honours reduced-motion, and
 * caps node count for 60fps on integrated graphics.
 */
export default function AmbientField() {
  const ref = useRef(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    const rm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const DPR = Math.min(window.devicePixelRatio || 1, 2);
    let W = 0, H = 0, pts = [], t = 0, raf = 0;
    let accent = '#F2C070', dim = '#6C8B83';

    const readColors = () => {
      const s = getComputedStyle(document.documentElement);
      accent = (s.getPropertyValue('--accent') || accent).trim();
      dim = (s.getPropertyValue('--c-dim') || dim).trim();
    };
    const hex = (c, a) => {
      c = (c || '').replace('#', '');
      if (c.length === 3) c = c[0]+c[0]+c[1]+c[1]+c[2]+c[2];
      const n = parseInt(c || '6C8B83', 16);
      return `rgba(${(n>>16)&255},${(n>>8)&255},${n&255},${a})`;
    };
    const size = () => {
      W = canvas.width = window.innerWidth * DPR;
      H = canvas.height = window.innerHeight * DPR;
      canvas.style.width = window.innerWidth + 'px';
      canvas.style.height = window.innerHeight + 'px';
    };
    const build = () => {
      const N = Math.min(56, Math.round(window.innerWidth / 26));
      pts = Array.from({ length: N }, () => ({
        x: Math.random()*W, y: Math.random()*H,
        vx: (Math.random()-0.5)*0.12*DPR, vy: (Math.random()-0.5)*0.12*DPR,
      }));
    };

    readColors(); size(); build();

    const frame = () => {
      ctx.clearRect(0, 0, W, H); t++;
      for (const p of pts) { p.x+=p.vx; p.y+=p.vy; if(p.x<0||p.x>W)p.vx*=-1; if(p.y<0||p.y>H)p.vy*=-1; }
      const md = 140*DPR, md2 = md*md;
      for (let a=0;a<pts.length;a++) for (let b=a+1;b<pts.length;b++){
        const dx=pts[a].x-pts[b].x, dy=pts[a].y-pts[b].y, d2=dx*dx+dy*dy;
        if (d2<md2){ ctx.strokeStyle=hex(dim,(1-d2/md2)*0.2); ctx.lineWidth=DPR;
          ctx.beginPath(); ctx.moveTo(pts[a].x,pts[a].y); ctx.lineTo(pts[b].x,pts[b].y); ctx.stroke(); }
      }
      for (let i=0;i<pts.length;i++){ const p=pts[i], pulse=(Math.sin((t+i*40)*0.02)+1)/2;
        ctx.fillStyle=hex(accent,0.22+pulse*0.4); ctx.beginPath();
        ctx.arc(p.x,p.y,(1.2+pulse*1.1)*DPR,0,6.283); ctx.fill(); }
      raf = requestAnimationFrame(frame);
    };

    const staticPaint = () => {
      ctx.clearRect(0,0,W,H); ctx.fillStyle=hex(dim,0.15);
      for (const p of pts){ ctx.beginPath(); ctx.arc(p.x,p.y,1.4*DPR,0,6.283); ctx.fill(); }
    };

    if (rm) staticPaint(); else frame();

    const onResize = () => { size(); build(); if (rm) staticPaint(); };
    window.addEventListener('resize', onResize);
    const mo = new MutationObserver(() => { readColors(); if (rm) staticPaint(); });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    return () => { cancelAnimationFrame(raf); window.removeEventListener('resize', onResize); mo.disconnect(); };
  }, []);

  return (
    <>
      <canvas ref={ref} className="lp-field" aria-hidden="true" />
      <div className="lp-field" aria-hidden="true" style={{
        background:
          'radial-gradient(56vw 46vh at 16% 6%, color-mix(in srgb,var(--field) 62%,transparent), transparent 70%),' +
          'radial-gradient(48vw 42vh at 86% 16%, color-mix(in srgb,var(--s3) 55%,transparent), transparent 72%),' +
          'radial-gradient(64vw 54vh at 50% 108%, color-mix(in srgb,var(--field) 48%,transparent), transparent 76%)',
        filter: 'blur(10px)',
      }} />
    </>
  );
}
