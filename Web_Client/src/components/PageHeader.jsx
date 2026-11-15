import React, { useContext, useEffect, useRef, useState } from 'react';
import { ShellCtx } from './Layout';

/**
 * iOS-style large title. Each page renders exactly one. When the title scrolls
 * under the floating nav bar it fades out here and the nav bar shows the same
 * text as a compact title (see Layout). Presentation only.
 *
 * props: title, subtitle, actions (right-aligned controls), leading (e.g. a back
 * button), accessory (e.g. a status dot or badge next to the title).
 */
export default function PageHeader({ title, subtitle, actions, leading, accessory }) {
  const shell = useContext(ShellCtx);
  const sentinel = useRef(null);
  const [collapsed, setCollapsed] = useState(false);

  // Publish the compact title to the nav bar.
  useEffect(() => {
    if (!shell) return;
    shell.setNav({ title, collapsed });
  }, [shell, title, collapsed]);

  // Clear it when the page unmounts.
  useEffect(() => () => { if (shell) shell.setNav({ title: '', collapsed: false }); }, [shell]);

  // Collapse once the bottom of the title passes under the nav bar.
  useEffect(() => {
    const root = shell && shell.scrollRef.current;
    const el = sentinel.current;
    if (!root || !el || typeof IntersectionObserver === 'undefined') return;
    const navH = 64;
    const io = new IntersectionObserver(
      ([entry]) => setCollapsed(!entry.isIntersecting),
      { root, rootMargin: `-${navH}px 0px 0px 0px`, threshold: 0 }
    );
    io.observe(el);
    return () => io.disconnect();
  }, [shell]);

  return (
    <header className={`ph${collapsed ? ' collapsed' : ''}`}>
      <div className="ph-main">
        {leading}
        <div className="ph-titles">
          <div className="ph-titlerow">
            {accessory}
            <h1 className="ph-title">{title}</h1>
          </div>
          {subtitle && <p className="ph-sub">{subtitle}</p>}
          <span className="ph-sentinel" ref={sentinel} aria-hidden="true" />
        </div>
      </div>
      {actions && <div className="ph-actions">{actions}</div>}
    </header>
  );
}
