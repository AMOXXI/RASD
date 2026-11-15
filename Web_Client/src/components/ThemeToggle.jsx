import React from 'react';
import { useApp } from '../App';

/**
 * Gooey glass Light/Dark switch. The sun/moon icon, "Light"/"Dark" label and the
 * knob position are all driven by [data-theme] in CSS; here we only flip the theme.
 */
export default function ThemeToggle() {
  const { theme, toggleTheme } = useApp();
  const dark = theme !== 'light';
  return (
    <button
      className="themesw"
      onClick={toggleTheme}
      role="switch"
      aria-checked={dark}
      aria-label="Toggle light and dark mode">
      <span className="tg-frost" />
      <span className="tg-goo"><span className="tg-track" /><span className="tg-knob" /></span>
      <span className="tg-icon tg-sun" aria-hidden="true">
        <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="5" /><path d="M12 1v3M12 20v3M4 12H1M23 12h-3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2" /></svg>
      </span>
      <span className="tg-icon tg-moon" aria-hidden="true">
        <svg viewBox="0 0 24 24"><path d="M20 14.5A8 8 0 019.5 4 7 7 0 1020 14.5z" /></svg>
      </span>
      <span className="tg-label" aria-hidden="true" />
    </button>
  );
}
