/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,jsx,ts,tsx}'],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Inter', 'system-ui', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'Consolas', 'monospace'],
      },
      colors: {
        bg:       'var(--c-bg)',
        surface:  'var(--c-surface)',
        panel:    'var(--c-panel)',
        panel2:   'var(--c-panel2)',
        border:   'var(--c-border)',
        border2:  'var(--c-border2)',
        ctext:    'var(--c-text)',
        muted:    'var(--c-muted)',
        dim:      'var(--c-dim)',
        accent:   'var(--c-accent)',
        online:   'var(--c-online)',
        offline:  'var(--c-offline)',
        warn:     'var(--c-warn)',
        info:     'var(--c-info)',
      },
      animation: {
        'pulse-slow': 'pulse 3s cubic-bezier(0.4,0,0.6,1) infinite',
        'fade-in':    'fadeIn 0.3s ease forwards',
        'slide-up':   'slideUp 0.25s ease forwards',
        'spin-slow':  'spin 3s linear infinite',
      },
      keyframes: {
        fadeIn:  { from: { opacity: 0 }, to: { opacity: 1 } },
        slideUp: { from: { opacity: 0, transform: 'translateY(10px)' }, to: { opacity: 1, transform: 'translateY(0)' } },
      },
    },
  },
  plugins: [],
};
