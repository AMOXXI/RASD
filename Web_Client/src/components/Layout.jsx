import React, { createContext, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useLocation } from 'react-router-dom';
import Sidebar from './Sidebar';
import ThemeToggle from './ThemeToggle';

/** Shared shell state for PageHeader: the scroll container and the nav-bar title. */
export const ShellCtx = createContext(null);

const prefersReducedMotion = () =>
  typeof window !== 'undefined' && window.matchMedia &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/** Compact title in the nav bar; owns its own state so pages never re-render for it. */
function NavTitle({ register }) {
  const [nav, setNav] = useState({ title: '', collapsed: false });
  useEffect(() => register(setNav), [register]);
  return <div className={`navbar-title${nav.collapsed && nav.title ? ' show' : ''}`} aria-hidden={!nav.collapsed}>{nav.title}</div>;
}

export default function Layout({ children }) {
  const { pathname } = useLocation();
  const scrollRef = useRef(null);
  const navRef = useRef(null);
  const contentRef = useRef(null);
  const navSetter = useRef(null);

  const user = (() => { try { return JSON.parse(localStorage.getItem('user') || '{}'); } catch { return {}; } })();

  const register = useCallback(fn => { navSetter.current = fn; return () => { navSetter.current = null; }; }, []);
  const shell = useMemo(() => ({
    scrollRef,
    setNav: v => { if (navSetter.current) navSetter.current(v); },
  }), []);

  // Nav bar fill/blur fades in over the first 80px of scroll (one CSS variable, rAF-throttled).
  useEffect(() => {
    const el = scrollRef.current, nav = navRef.current;
    if (!el || !nav) return;
    let frame = 0;
    const update = () => { frame = 0; nav.style.setProperty('--nav-p', Math.min(el.scrollTop / 80, 1).toFixed(3)); };
    const onScroll = () => { if (!frame) frame = requestAnimationFrame(update); };
    update();
    el.addEventListener('scroll', onScroll, { passive: true });
    return () => { el.removeEventListener('scroll', onScroll); if (frame) cancelAnimationFrame(frame); };
  }, []);

  // New page: start at the top.
  useEffect(() => { if (scrollRef.current) scrollRef.current.scrollTop = 0; }, [pathname]);

  // Cards ease in as they enter the viewport (transform + opacity only).
  useEffect(() => {
    const root = scrollRef.current, content = contentRef.current;
    if (!root || !content || prefersReducedMotion() || typeof IntersectionObserver === 'undefined') return;

    const io = new IntersectionObserver(entries => {
      entries.forEach(e => {
        if (!e.isIntersecting) return;
        e.target.setAttribute('data-rv', '1');
        io.unobserve(e.target);
      });
    }, { root, rootMargin: '0px 0px -6% 0px', threshold: 0.04 });

    const tag = () => {
      const fresh = content.querySelectorAll('.card:not([data-rv]), .stat-card:not([data-rv])');
      fresh.forEach((el, i) => {
        el.setAttribute('data-rv', '0');
        el.style.transitionDelay = Math.min(i * 40, 240) + 'ms';
        io.observe(el);
      });
    };
    tag();
    const mo = new MutationObserver(tag);
    mo.observe(content, { childList: true, subtree: true });
    return () => { mo.disconnect(); io.disconnect(); };
  }, [pathname]);

  return (
    <ShellCtx.Provider value={shell}>
      <div className="app-shell">
        <Sidebar />

        <div className="main-area">
          <div className="scroll-area" ref={scrollRef}>
            <div className="navbar" ref={navRef}>
              <div className="navbar-bg" aria-hidden="true" />
              <NavTitle register={register} />
              <div className="navbar-actions">
                <ThemeToggle />
                <div className="user-chip">
                  <div className="user-avatar">{(user.username || 'A')[0].toUpperCase()}</div>
                  <div>
                    <div className="user-name">{user.username || 'Admin'}</div>
                    <div className="user-role">{user.role || 'admin'}</div>
                  </div>
                </div>
              </div>
            </div>

            <main className="page-content" ref={contentRef}>
              {children}
            </main>
          </div>
        </div>
      </div>
    </ShellCtx.Provider>
  );
}
