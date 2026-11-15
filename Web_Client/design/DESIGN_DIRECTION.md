# RASD Dashboard — Phase 4 Design Direction v0.2 "Liquid Glass" (for sign-off)

**Status:** Direction + interactive prototype only. No frontend wired, no `V13`
built. Backend (API routes, socket events, DB schema) frozen and untouched.

> Supersedes v0.1 (the flat spec board), which was rejected as too utilitarian.
> New target: **Apple-caliber craft — apple.com scroll storytelling + iOS 26
> Liquid Glass material — adapted for a security operations dashboard.**

**Deliverables this pass**
- Interactive main-dashboard prototype: `rasd_glass_dashboard.html` (published as a
  private Artifact; source also committed here for version control).
- Revised tokens: [`design-tokens.css`](design-tokens.css) / [`design-tokens.json`](design-tokens.json) (v0.2, glass layer added).

---

## 1. The material — Liquid Glass, done properly (not generic glassmorphism)

Three Apple layers, all implemented:
- **Highlight (rim-light):** a bright hairline on the top edge (`--glass-rim`) via a
  masked border-gradient — light casting along the curved edge.
- **Shadow (depth separation):** `--glass-shadow` lifts the glass off the field.
- **Illumination (material):** `backdrop-filter: blur() saturate()` frost + adaptive
  `--glass-tint`, plus a **cursor-tracked specular** gloss (`--glass-specular`) that
  follows the pointer.
- **Edge refraction (what separates it from a plain blur):** an SVG
  `feTurbulence → feGaussianBlur → feDisplacementMap` filter (`#liquid`) warps a thin
  rim overlay so content near the curved edge distorts as if passing through a
  different refractive index. Applied to **rims only**, never the whole backdrop.
- Two variants: **regular** (`--glass-tint`, adaptive) and **clear** (`--glass-tint-clear`,
  more content visible beneath — used for the floating dock).

Deliberately deviated from the skill DB's "Liquid Glass" entry, which specifies
iridescent/chromatic-aberration rainbow: **rejected** per your "restrained, refined,
not maximal" constraint. Tint stays navy; cyan is the only chromatic note (rim/specular).

## 2. Apple's constraints — followed strictly

1. **Glass = navigation layer only.** Sidebar, top bar, floating dock: glass. Node
   grid, KPIs, alerts, command log: **solid** (`--surface-*`). Enforced by which token
   a surface uses.
2. **Never glass on glass.** The three glass surfaces don't overlap; no modal-over-glass.
3. **Text/data always on solid**, never directly on glass — the legibility rule Apple's
   own beta testers flagged. Nav labels carry a solid active-pill; all data lives on cards.
4. **Adapts light/dark automatically** — glass tint/rim/specular redefined per theme.

## 3. Scroll motion (apple.com storytelling, no scroll-jacking)
- Sticky glass top bar that **frosts content passing beneath**; a razor-thin cyan
  bottom rim-light intensifies once you scroll (`.scrolled`).
- Scroll-triggered reveals: staggered fade + subtle scale, `ease-out`, **IntersectionObserver**
  (native scroll preserved — no pinning/hijack).
- Subtle parallax on the **ambient field only** (blobs + grid), small delta, `requestAnimationFrame`.
- Everything gated by `prefers-reduced-motion` (reveals resolve instantly, blobs/parallax freeze).

## 4. Palette / type (reweighted for glass)
- Base **deepened** to near-black (`#020509 / #04070D`) so glass reads luminous; RASD
  navy/teal preserved; **cyan `#00B4D8` = specular / rim tint**; status green/amber/red/blue.
- **Type reconsidered for an Apple feel:** UI in **Geist** (refined neo-grotesque; renders
  as **SF Pro** on the demo Mac). **Mono kept ONLY for data** — IPs, MACs, hashes, log lines,
  KPI values — in **JetBrains Mono** (SF Mono fallback). No mono on prose.

## 5. The dashboard adaptation (the hard part)
| Surface | Treatment |
|---|---|
| **Overview / hero** | Full Apple treatment: large gradient headline, ambient depth, scroll reveals, glass chrome. |
| **Node grid** | **Solid** cards, dense; status by **shape + colour + label** (circle/triangle/diamond), never blur. 120 cards in the prototype for the perf test; lift to cyan on hover. |
| **Alert stream / command log** | **Solid**; severity = left stripe + glyph + label. Live data pulses, never warps. |
| Floating dock | Glass **clear** variant, quick actions. |

Status is **never** conveyed by glass/blur. Live data never animates distractingly.

## 6. Performance budget (WebView2 on integrated graphics, live demo)
- Target **60fps scroll**; **≤3 concurrent `backdrop-filter`** surfaces in viewport
  (sidebar + topbar + dock). SVG refraction on nav rims only, never per-card.
- No animating filter/backdrop properties; `will-change:transform` on the parallax
  field only; specular is a CSS-variable update (no filter repaint).
- React build will **virtualize** the node grid + log (react-window); prototype renders
  120 solid DOM cards to prove the layout holds.

## 7. Skills invoked
`ui-ux-pro-max` (style=Liquid Glass + Modern Dark base + VisionOS mechanics; gsap parallax/scroll-reveal; react perf), `design-system` (glass token layer), `artifact-design` (prototype craft). `ui-styling`, `taste-skill`, `vercel-react-best-practices` and `web-design-guidelines` apply during **B2 implementation** and its final audit.

## 8. What B2 will do (on approval)
Rebuild the React views against these v0.2 tokens with shadcn/ui + Tailwind, apply the
Vercel perf patterns (memoised rows, virtualised grid/log, no re-render on socket ticks),
add the WebView2 shell (B1), then run the `web-design-guidelines` audit → **V13**.
**No API route, socket event, payload shape, or DB schema changes** — if any step needs
one, I stop and list it.
