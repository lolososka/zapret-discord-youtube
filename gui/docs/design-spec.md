## 1. Direction

ZapretGUI reads as the front panel of a machined network instrument: a near-black achromatic chassis, 1px hairlines instead of borders, tabular monospace for every number, and exactly one hue of light in the whole application — the user-selectable accent — which appears only where it carries meaning (bypass armed, traffic flowing, strategy selected). Behind the chassis sits a single slow, GPU-cached ambient wash so the room feels alive at idle, and the one theatrical moment in the product is the power dial arming: a 780 ms tick-ring ignition that says "traffic is passing now" and then goes quiet.

Spine = the precision-instrument direction (achromatic surfaces, signal-only colour, short mechanical motion). Grafted in: the cached aurora ambience + dither tile from «Полярная станция», and the OpacityMask tick-ignition + ShockRing from «Реактор». Everything else in those two proposals (violet/teal rainbow fields, neon glow budget of 6 shadows, letter-wide neon lists) is discarded.

---

## 2. Colour tokens

All brushes are `SolidColorBrush` in `Themes/Colors.xaml`, declared with `x:Key` exactly as written. Everything in the **Accent** block is a `DynamicResource` swapped at runtime by the accent picker; everything else is static and frozen.

| XAML resource key | hex (ARGB where alpha matters) | Where it is used |
|---|---|---|
| `BrushBgVoid` | `#05070A` | Nav rail background, window title bar, deepest plane |
| `BrushBgBase` | `#090C11` | Window root `Border` background, dialog page background |
| `BrushBgSunken` | `#06080C` | Content canvas behind cards, log viewport, power-well interior, scope strip |
| `BrushSurfaceRaised` | `#10141B` | All cards, metric tiles, strategy rows, toggle panels, chips |
| `BrushSurfaceOverlay` | `#171C25` | Popups, context menus, tooltips, ComboBox drop-downs, modal dialog card |
| `BrushSurfaceHover` | `#161B23` | Pointer-over fill for nav items, list rows, chrome buttons |
| `BrushSurfacePressed` | `#0C1017` | Pressed fill for the same elements |
| `BrushCardHoverOverlay` | `#0DFFFFFF` | Additive overlay `Rectangle` inside cards on hover (animated Opacity 0→1) |
| `BrushInputBackground` | `#0A0E14` | TextBox / search field / combo field fill, slider track |
| `BrushHairlineWeak` | `#151A21` | 1px internal dividers: toggle rows, log lines, card sections, rail right edge |
| `BrushHairlineStrong` | `#262D37` | 1px card, window, dial-face and chip borders; the dial ring track |
| `BrushTextPrimary` | `#EAEFF6` | Page titles, metric values, active nav label, selected strategy name |
| `BrushTextSecondary` | `#94A1B2` | Body copy, inactive nav labels, card sub-lines, log message text |
| `BrushTextTertiary` | `#5E6B7C` | 11px uppercase micro-labels, timestamps, version string, placeholders |
| `BrushTextDisabled` | `#3E4753` | Disabled labels, unavailable strategies, dial glyph when winws.exe is missing |
| `BrushDisabledSurface` | `#0D1116` | Disabled button/field fill (border drops to `BrushHairlineWeak`) |
| `BrushAccentStart` | `#26E0F2` *(default: Cyan)* | Stop 0.0 of every accent `LinearGradientBrush` (StartPoint 0,0 → EndPoint 1,1) |
| `BrushAccentMid` | `#29C4FA` | Stop 0.5; also the flat accent fill for glyphs, ticks, sparklines |
| `BrushAccentEnd` | `#2FA8FF` | Stop 1.0 |
| `BrushAccentGlow` | `#8C29C4FA` | `Color` of the two permitted `DropShadowEffect`s (dial ambient, running status dot) |
| `BrushAccentWash` | `#1A29C4FA` | Selected list row / selected strategy card background, selected nav tint |
| `BrushAccentDim` | `#5229C4FA` | Ignited bezel ticks, sparkline stroke, log-line accent tick after fade |
| `BrushStateRunning` | = `BrushAccentMid` | Power glyph, dial face wash, status dot, title-bar mark — **while running only** |
| `BrushStateStopped` | `#6E7A85` | Power glyph, status dot, state caption when stopped. Grey, never red |
| `BrushStateArming` | `#F0B441` | Status dot and caption during «ЗАПУСК…» |
| `BrushSuccess` | `#35E0A1` | Diagnostics PASS rows, success toasts, «служба установлена» |
| `BrushWarning` | `#F0B441` | Diagnostics WARN rows, «служба не установлена», ALT-strategy caution chip |
| `BrushDanger` | `#FF5F6D` | winws.exe crash, WinDivert blocked, remove-service button, close-button hover @0.9 |
| `BrushInfo` | `#4FA8FF` | Informational log level, «доступно обновление» badge, hint callouts |
| `BrushNavIndicator` | vertical `LinearGradientBrush` `#26E0F2`→`#2FA8FF` | The single 2×20 nav rail indicator rectangle |
| `BrushScrollThumb` | `#2A313B` | Scrollbar thumb at rest |
| `BrushScrollThumbHover` | `#3B4插`→ use `#3B4450` | Scrollbar thumb on pointer-over / drag |
| `BrushScrollTrack` | `#00000000` | Scrollbar track (fully transparent) |
| `BrushFocusRing` | `#66FFFFFF` | 1px keyboard focus outline, drawn 3px outside the control's own border |
| `BrushScrim` | `#C4050709` | Full-window dimmer under modal dialogs |
| `BrushShadow` | `#CC000000` | `Color` of dialog / popup / toast `DropShadowEffect` |
| `BrushScopeGrid` | `#12161C` | 1px graticules and baseline in the packet-rate scope and sparklines |
| `BrushDitherDot` | `#0AFFFFFF` | 3×3 px tiled `DrawingBrush` (two 1×1 dots) over the ambient wash — kills banding |

**Correction for the one typo above:** `BrushScrollThumbHover` = `#3B4450`.

### Selectable accent gradients

Picker in Настройки writes these three keys. `BrushAccentGlow` = AccentMid @ `8C` alpha, `BrushAccentWash` = AccentMid @ `1A`, `BrushAccentDim` = AccentMid @ `52`. Nothing else in the app changes.

| Preset | Start (0,0) | Mid (0.5) | End (1,1) |
|---|---|---|---|
| Cyan **(default)** | `#26E0F2` | `#29C4FA` | `#2FA8FF` |
| Violet | `#6E5CFF` | `#8B57FF` | `#A855F7` |
| Emerald | `#35E8A6` | `#23D3A2` | `#14B8A6` |
| Rose | `#FF7A9C` | `#FF5479` | `#F0356A` |
| Amber | `#FFC94B` | `#FFA644` | `#FF853D` |

---

## 3. Typography

Font stacks (declared once as `FontFamily` resources, Cyrillic-complete on Win10+):
- `FontUI` = `"Segoe UI Variable Text, Segoe UI, Arial"`
- `FontDisplay` = `"Segoe UI Variable Display, Segoe UI, Arial"`
- `FontMono` = `"Consolas, Cascadia Mono, Courier New"`

Letter-spacing is **not** a WPF property. It is produced by an attached behaviour `Type.Tracking="N"` (N in 1/1000 em) that rewrites the `Text` inserting `U+2009` THIN SPACE between characters. **Apply it only to ALL-CAPS Latin/Cyrillic labels of ≤ 24 characters.** Never on body copy, never on log text, never on anything selectable.

| Role | Family | Size (DIP) | Weight | Tracking | Line height | Notes |
|---|---|---|---|---|---|---|
| Page title | `FontDisplay` | 26 | SemiBold (600) | +10 | 32 | e.g. «Панель». `TextFormattingMode=Ideal` |
| Page subtitle | `FontUI` | 12.5 | Regular (400) | 0 | 17 | `BrushTextSecondary`, sits 4px under title |
| Section title | `FontUI` | 15 | SemiBold (600) | +20 | 20 | Card headers («Живой журнал») |
| Card micro-label | `FontUI` | 11 | SemiBold (600) | +110 | 14 | UPPERCASE, `BrushTextTertiary` («ПРОЦЕСС») |
| Card title | `FontUI` | 13 | SemiBold (600) | 0 | 18 | Strategy name in a row, dialog heading |
| Body | `FontUI` | 13 | Regular (400) | 0 | 19 | Default `TextElement.FontSize` of the window |
| Caption | `FontUI` | 11.5 | Regular (400) | 0 | 15 | Secondary row line, hints, tooltip text |
| Numeric readout (hero) | `FontMono` | 28 | Regular (400) | 0 | 32 | Uptime. `Typography.NumeralAlignment=Tabular`, `TextFormattingMode=Ideal` |
| Numeric value (tile) | `FontMono` | 22 | Regular (400) | 0 | 26 | Tile values; unit suffix 12 `FontUI` `BrushTextSecondary`, 8px gap |
| Numeric inline | `FontMono` | 11.5 | Regular (400) | 0 | 18 | Log timestamps, PID chips, raw argument lines |
| Nav item | `FontUI` | 13 | 400 inactive / 600 active | 0 | 16 | Colour, not size, changes on hover |
| Button | `FontUI` | 13 | SemiBold (600) | +20 | 16 | Same for primary, secondary, danger |
| State caption (dial) | `FontUI` | 13 | SemiBold (600) | +140 | 16 | UPPERCASE, «ЗАЩИТА АКТИВНА» |

Global: `TextOptions.TextFormattingMode=Display` for everything ≤ 14 DIP, `Ideal` for ≥ 20 DIP, `TextRenderingMode=ClearType` on the window root, `RenderOptions.ClearTypeHint=Enabled` on the log viewport. No `TextBlock` may be a descendant of an element carrying an `Effect`.

---

## 4. Geometry

**Corner radii** — `CornerRadius` values, use no others:
- `0` — title-bar chrome buttons, scope strip, log rows
- `4` — chips, badges, small inputs, focus ring (control radius + 2)
- `6` — nav items, list rows, buttons, text boxes
- `8` — cards, metric tiles, panels, popups
- `12` — modal dialog card, toast
- `999` (pill) — status pills, toggle track, toggle knob

**Spacing scale** — only these values: `4, 8, 12, 16, 20, 24, 32, 40, 56`. Card internal padding is `16` (tiles) or `20` (large panels). Page padding is `32,24,32,24`.

**Border widths**: every border is exactly `1` DIP. The two exceptions are the dial ring track and the dial sweep arc, both `2` DIP, and the nav indicator, a `2×20` filled rectangle. No double borders, no bevels, no inset/outset pairs.

**Elevation & shadows** — elevation is expressed by surface lightness first; shadows exist at only three levels:

| Level | Surface | Border | Shadow |
|---|---|---|---|
| E0 canvas | `BrushBgSunken` | none | none |
| E1 card | `BrushSurfaceRaised` | 1px `BrushHairlineWeak` | **none** |
| E1-hover | `BrushSurfaceRaised` + `BrushCardHoverOverlay` | 1px `BrushHairlineStrong` | none |
| E2 popup / toast | `BrushSurfaceOverlay` | 1px `BrushHairlineStrong` | `DropShadowEffect` Color `#CC000000`, BlurRadius 24, ShadowDepth 6, Direction 270, Opacity 0.55 |
| E3 modal dialog | `BrushSurfaceOverlay` | 1px `BrushHairlineStrong` | `DropShadowEffect` Color `#CC000000`, BlurRadius 40, ShadowDepth 8, Direction 270, Opacity 0.60 |
| Accent glow (dial only) | — | — | see §6 layer (a): no `DropShadowEffect` at all; the glow is a `RadialGradientBrush` ellipse |

Window root: `UseLayoutRounding=True`, `SnapsToDevicePixels=True`.

---

## 5. Window & layout

**Window.** `Width=1180`, `Height=760`, `MinWidth=1024`, `MinHeight=680`. `WindowStyle=None`, `ResizeMode=CanResize`, **`AllowsTransparency=False`** (non-negotiable — it would kill ClearType and GPU acceleration). `WindowChrome`: `CaptionHeight=44`, `ResizeBorderThickness=6`, `GlassFrameThickness=1`, `CornerRadius=0`, `UseAeroCaptionButtons=False`. Rounded corners come from DWM: on `SourceInitialized` call `DwmSetWindowAttribute(hwnd, 33, ref 2, 4)` (`DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`, ≈8px on Win11; Win10 stays square, which is correct). Root `Border`: `Background=BrushBgBase`, `BorderThickness=1`, `BorderBrush=BrushHairlineStrong`.

**Layer stack inside the root Border (bottom → top):**
1. `BrushBgBase` fill.
2. `Grid x:Name="AmbientHost"`, `ClipToBounds=True`, `IsHitTestVisible=False`, `CacheMode="BitmapCache RenderAtScale=0.30 EnableClearType=False"`, `Opacity=0.55`. Contains **two** ellipses only: **A** 900×700 at (−140, −180), `RadialGradientBrush` stop 0 = AccentMid @ `24` alpha → stop 0.72 = AccentMid @ `00`; **B** 1100×820 at (620, 240), `RadialGradientBrush` stop 0 = `#241E2A6B` → stop 0.8 transparent. `BlurEffect Radius=110` on A, `Radius=140` on B — these are the **only two BlurEffects in the application**.
3. `Rectangle` filled with the `BrushDitherDot` `DrawingBrush` (`Viewport 0,0,3,3`, `ViewportUnits=Absolute`, `TileMode=Tile`), `IsHitTestVisible=False`.
4. Content grid: title bar (44) / body (rail + content host).

**Title bar** — height 44, `Background=BrushBgVoid`, bottom edge 1px `BrushHairlineWeak`, `WindowChrome.IsHitTestVisibleInChrome=True` on all interactive children. Contents left→right: 16px inset; 16×16 `Path` app mark (a shield outline, `StrokeThickness=1.5`) — stroke `BrushTextTertiary` when stopped, `BrushStateRunning` when running (the *only* coloured element in the chrome); 12px gap; `ZAPRET` 11px `FontUI` SemiBold `BrushTextTertiary` tracking +160; 8px gap; `1.10.0` 11px `FontMono` `BrushTextDisabled`; 14px gap; status pill: height 24, pill radius, `Background=BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 10,0, containing an 8px `Ellipse` (`BrushStateStopped` / `BrushStateArming` / `BrushStateRunning`) + 6px gap + 11px SemiBold caps text «ОСТАНОВЛЕН» / «ЗАПУСК» / «АКТИВЕН». Right side: three 46×32 buttons, radius 0, 0px gap, 6px right inset; glyphs are 10×10 `Path`s, `StrokeThickness=1`, `BrushTextSecondary`; hover fill `BrushSurfaceHover`; close hover fill `BrushDanger` at `Opacity=0.9` with `BrushTextPrimary` glyph. No shadow, no gradient, no acrylic in the chrome.

**Nav rail** — fixed width **208**, `Background=BrushBgVoid`, right edge 1px `BrushHairlineWeak`, not collapsible (dropped from the source proposals: a 5-item rail has nothing to gain). Layout:
- y 0–56: brand block. 20px left inset, 24×24 accent-gradient-filled shield `Path`, 12px gap, «ZAPRET» 15px `FontDisplay` SemiBold `BrushTextPrimary` tracking +180.
- y 56: 1px `BrushHairlineWeak` divider, inset 16 left/right.
- Items begin at y 64. Item box: full-bleed width, **height 40**, **pitch 44** (4px gap), horizontal margin 8, radius 6, content inset 12. Icon 18×18 `Path`, `StrokeThickness=1.5`, `StrokeLineJoin=Round`, `Fill=null` inactive; 10px gap; label 13px.
- States: inactive `BrushTextSecondary`; hover `Background=BrushSurfaceHover`, `Foreground=BrushTextPrimary`; active `Background=BrushAccentWash`, `Foreground=BrushTextPrimary`, weight 600, icon swaps to the filled variant of the same geometry (`Fill=BrushTextPrimary`, `Stroke=null`).
- Indicator: **one** shared `Rectangle` 2×20, radius 1, `Fill=BrushNavIndicator`, pinned x=0, vertically centred on the active item, moved by `TranslateTransform.Y` (never five instances toggling).
- Icons: Панель = 8r circle + three radial ticks; Стратегии = three horizontal sliders with a knob circle; Диагностика = pulse polyline inside a rounded rect; Журнал = rounded rect + three text lines; Настройки = 8-tooth gear `Path`.
- Footer: 1px divider at `ActualHeight−56` (inset 16), then a 40px row with 20px inset: 8px status `Ellipse` + 10px gap + 11px caps `BrushTextTertiary` «ОБХОД АКТИВЕН» / «ОСТАНОВЛЕНО».

**Content host** — width `1180 − 208 = 972`. Page root `Background=BrushBgSunken`, `Padding=32,24,32,24` → usable **908 × 668** at default size (`760 − 44 − 48`). All pages centre their content with `MaxWidth=908`.

**Page header** (identical on all 5 pages, height 56, followed by a 20px gap):
- Left: page title 26px + 4px gap + 12.5px subtitle `BrushTextSecondary`.
- Right, bottom-aligned to the title baseline: one contextual control — Панель: strategy chip (260×32, radius 16, `BrushSurfaceRaised`, 1px `BrushHairlineStrong`, «Стратегия:» 12px tertiary + name 12px `FontMono` primary + 8px chevron). Стратегии: 240×32 search box. Диагностика: «Проверить всё» primary button 132×32. Журнал: 3 filter chips 26px + «Очистить» 96×32. Настройки: «Сбросить» 108×32 secondary.

**Scrollbars** — custom `ScrollBar` template. Vertical: track width 10, `Background=Transparent`, no arrow repeat buttons, no line-up/down. Thumb width 6, centred, radius 3, `Fill=BrushScrollThumb`, min height 32, margin 2. Hover/drag → `BrushScrollThumbHover` over 120 ms Linear. Horizontal is the same rotated. `ScrollViewer.PanningMode=VerticalOnly` where applicable.

---

## 6. The power control

The dial assembly is a `Grid` of **224 × 224** DIP, centred horizontally in the power well, with all layers as siblings in the same `Grid` cell (each `HorizontalAlignment=Center VerticalAlignment=Center`) so they are concentric by construction. Implement as `Controls/PowerDial.xaml` with a `State` DP of enum `{ Stopped, Arming, Running, Fault }`.

**Required custom shape.** `ArcSegmentShape : Shape` with `double Angle` DP (0–360), `FrameworkPropertyMetadataOptions.AffectsRender`, `Radius` DP, `Thickness` DP. `DefiningGeometry` builds a `StreamGeometry`: start at 12 o'clock, `ArcTo` clockwise, `IsLargeArc = Angle > 180`, `SweepDirection=Clockwise`. **`ArcSegment` cannot be Storyboard-animated any other way** — without this class the signature moment degrades into a fade.

Layers, **bottom → top**:

**(a) Ambient pool.** `Ellipse` 300×300, `Fill` = `RadialGradientBrush` (`GradientOrigin 0.5,0.5`, `Center 0.5,0.5`, `RadiusX/Y 0.5`): stop 0.0 = AccentMid @ `59` alpha, stop 0.55 = AccentMid @ `1F`, stop 1.0 = AccentMid @ `00`. `Opacity=0` when Stopped, `0.46` when Running. `IsHitTestVisible=False`. `CacheMode=BitmapCache RenderAtScale=1`. **No `DropShadowEffect`, no `BlurEffect` — the gradient is the glow.** Its own `ScaleTransform` (origin 0.5,0.5) is animated by `LockPulse`.

**(b) Idle bezel ticks.** One `Rectangle` 240×240 filled with a **frozen** `DrawingBrush`: 60 ticks, each 1×6 DIP, at radius 112 from centre, rotated i×6°, `Fill=BrushHairlineStrong`. Authored as a single `GeometryDrawing` with one `GeometryGroup` — **never 60 separate visuals**. `IsHitTestVisible=False`, brush and geometry `.Freeze()`d at startup.

**(c) Ignited bezel ticks.** A pixel-identical copy of (b) with `Fill=BrushAccentDim`. Its `OpacityMask` is a locally-declared `RadialGradientBrush` (`Center 0.5,0.5`, stops: 0.0 `#FFFFFFFF`, 0.85 `#FFFFFFFF`, 1.0 `#00FFFFFF`) whose `RadiusX`/`RadiusY` are animated `0.0 → 0.62` during `Ignite`. At rest (Stopped) `RadiusX=RadiusY=0` so the layer is invisible. This is what makes the ticks light up outward in a wave.

**(d) Ring track.** `Ellipse` 208×208, `Stroke=BrushHairlineStrong`, `StrokeThickness=2`, `Fill=null`.

**(e) Sweep arc.** `ArcSegmentShape` `Radius=104`, `Thickness=2`, `StrokeStartLineCap=Round`, `StrokeEndLineCap=Round`, `Stroke` = a **locally declared** (never `StaticResource`, never frozen) `LinearGradientBrush` `StartPoint 0,0 EndPoint 1,1` with stops AccentStart 0.0 / AccentMid 0.5 / AccentEnd 1.0. `Angle=0` at rest. This is the element `ArmSweep` and `DisarmCollapse` drive.

**(f) Scanner arc.** `ArcSegmentShape` `Radius=98`, `Thickness=3`, `Angle=92` (fixed), `Stroke` = local `LinearGradientBrush` from AccentMid @ `00` to AccentMid @ `B3`. `Opacity=0` unless Running. Carries a `RotateTransform` (centre 112,112) driven by `RunPulseOrbit`. **This layer must never carry an Effect** — a rotating transform under an effect re-rasterises every frame.

**(g) Dial face.** `Ellipse` 152×152, `Stroke=BrushHairlineStrong`, `StrokeThickness=1`, `Fill` = local `RadialGradientBrush` (`GradientOrigin 0.5,0.35`): Stopped = centre `#141922` → edge `#0C1017`; Running = centre AccentMid @ `1F` over `#101820` → edge `#0C1017` (animated by `GlyphIgnite` as two `ColorAnimation`s on the two stops).

**(h) Power glyph.** One `Path` 46×46: an arc of radius 17 spanning 40°→320° plus a rounded vertical stem from (23,7) to (23,22). `StrokeThickness=2.5`, `StrokeStartLineCap=Round`, `StrokeEndLineCap=Round`, `Fill=null`. `Stroke=BrushStateStopped` when Stopped, `BrushStateRunning` when Running, `BrushDanger` on Fault, `BrushTextDisabled` when winws.exe is absent.

**(i) Shock ring.** `Ellipse` 208×208, `Stroke=BrushAccentStart`, `StrokeThickness=2`, `Fill=null`, `Opacity=0`, `CacheMode=BitmapCache`, own `ScaleTransform` origin 0.5,0.5. Fires once per ignition.

**(j) Hit target.** A `ToggleButton` with a fully custom `Template` whose only visual is a transparent `Ellipse` 224×224 (`Fill=Transparent`, so it hit-tests). `Cursor=Hand`, `Focusable=True`, Space/Enter activate, `AutomationProperties.Name="Включить обход"`. `PressDown`/`PressUp` scale the **entire dial Grid** (`RenderTransformOrigin 0.5,0.5`), not just the face.

**(k) Below the assembly** (outside the 224 grid, in the well's vertical stack): 20px gap → state caption (13px SemiBold caps tracking +140: «ОТКЛЮЧЕНО» `BrushTextSecondary` / «ЗАПУСК…» `BrushStateArming` / «ЗАЩИТА АКТИВНА» `BrushStateRunning` / «ОШИБКА ЗАПУСКА» `BrushDanger`) → 12px gap → uptime `00:14:37` 28px `FontMono` `BrushTextPrimary` tabular → 4px gap → «ВРЕМЯ РАБОТЫ» micro-label.

**The signature moment, exactly.** Click → `PressDown` (70 ms) → `PressUp` (160 ms BackEase). At mouse-up the service start is issued and `ArmSweep` runs: the sweep arc's `Angle` goes `0 → 360` over **780 ms, CircleEase EaseOut**, while layer (c)'s mask radius opens `0 → 0.62` over the same 780 ms with `PowerEase Power=3 EaseOut` — the mask front deliberately lags the arc by ~12 px, so the ticks read as being *ignited by* the leading edge, decelerating into the lock like a torque wrench reaching spec. On process-confirmed: `Ignite` fires the shock ring (`Scale 0.62→1.55`, `Opacity 0.85→0`, 620 ms), `LockPulse` breathes the ambient pool once (`Opacity 0→0.72→0.46`, `Scale 0.94→1.04→1.00`, 340 ms), `GlyphIgnite` recolours the glyph and face (200 ms), and `OdometerFlip` flips the uptime from `--:--:--` to `00:00:00` one character at a time on a 30 ms stagger. Then everything stops except the 2600 ms scanner orbit. If the process fails instead, `Fault` shakes the dial and recolours the arc to `BrushDanger`.

---

## 7. Motion system

Global: `Storyboard.FillBehavior=Stop` everywhere, with the terminal value committed in `Completed`. All page storyboards are stopped in `Unloaded`. Every animated brush is a local, per-element instance — animating a shared frozen `StaticResource` brush throws.

| Name | Trigger | Animated properties (from → to) | ms | Easing |
|---|---|---|---|---|
| `WindowEnter` | First `ContentRendered` | Root Border `Opacity` 0→1; `ScaleTransform` X/Y 0.988→1.000 (origin 0.5,0.5); `AmbientHost.Opacity` 0→0.55 (over 1200 ms, `BeginTime` 0) | 260 | `CubicEase` EaseOut |
| `AmbientDrift` | Loaded, `RepeatBehavior=Forever`, `AutoReverse=True` | Ellipse A `TranslateTransform` X −60→+70, Y +40→−30, `Scale` 1.00→1.12 (period 46 s); Ellipse B X +40→−50, Y −30→+50, `Scale` 1.06→0.94 (period 61 s). **Only Transform/Opacity — never Width/Height/GradientStop.Offset** | 46000 / 61000 | `SineEase` EaseInOut |
| `PageSwap` | Nav selection change | Outgoing page `Opacity` 1→0 (90 ms, `QuadraticEase` EaseIn); incoming `Opacity` 0→1 and `TranslateTransform.Y` 8→0 at `BeginTime=00:00:00.090` | 180 | `CubicEase` EaseOut |
| `NavIndicatorSlide` | Nav selection change | Indicator `TranslateTransform.Y` old→new (Δ = 44 px per step); simultaneously `ScaleY` keyframes 1.0 → 0.42 (45 %) → 1.0 (100 %), origin 0.5,0.5 | 220 | Y `CubicEase` EaseInOut; ScaleY `SineEase` EaseInOut |
| `NavItemHover` | `MouseEnter` / `MouseLeave` on a rail item | `Background` `ColorAnimation` `#00161B23`→`#FF161B23`; `Foreground` `#94A1B2`→`#EAEFF6`. Leave = same values reversed over 140 ms | 110 | `QuadraticEase` EaseOut |
| `CardHoverLift` | `MouseEnter` / `MouseLeave` on card, tile or list row | Overlay `Rectangle.Opacity` 0→1 (`BrushCardHoverOverlay`); `BorderBrush` `ColorAnimation` `#262D37` ← `#151A21`; `TranslateTransform.Y` 0→−2. Leave over 200 ms | 150 | `CubicEase` EaseOut |
| `ButtonPress` | `PreviewMouseLeftButtonDown` / `Up` on any button | Down: `ScaleX/Y` 1.000→0.976 (origin 0.5,0.5), `Background` one step to `BrushSurfacePressed`. Up: `ScaleX/Y` →1.000 over 120 ms, no overshoot | 70 | `QuadraticEase` EaseOut |
| `PowerPressDown` | `PreviewMouseLeftButtonDown` on the dial | Dial Grid `ScaleX/Y` 1.000→0.968; dial face `Fill` centre stop → `#0E1218` | 70 | `QuadraticEase` EaseOut |
| `PowerPressUp` | `MouseLeftButtonUp` on the dial | Dial Grid `ScaleX/Y` 0.968→1.000 — **the only overshoot in the entire application** | 160 | `BackEase` Amplitude=0.35 EaseOut |
| `ArmSweep` (power on, part 1) | Start command issued | Sweep-arc `Angle` 0→360; tick-mask `RadiusX`/`RadiusY` 0.00→0.62; state caption cross-fade `Opacity` 1→0→1 with the text swap at 50 % | 780 | `Angle` `CircleEase` EaseOut; mask `PowerEase` Power=3 EaseOut |
| `Ignite` (power on, part 2) | Process confirmed running (`BeginTime` = arm completion) | ShockRing `ScaleX/Y` 0.62→1.55, `Opacity` 0.85→0; scanner arc `Opacity` 0→1 | 620 | Scale `PowerEase` Power=3 EaseOut; Opacity `QuadraticEase` EaseIn |
| `LockPulse` | With `Ignite` | Ambient pool `Opacity` 0.00→0.72 (40 %) →0.46 (100 %); `ScaleX/Y` 0.94→1.04→1.00 | 340 | `SineEase` EaseInOut on both segments |
| `GlyphIgnite` | State Stopped→Running | Glyph `Stroke` `ColorAnimation` `#6E7A85`→AccentMid; `StrokeThickness` 2.25→2.50; dial-face gradient centre stop → AccentMid @ `1F` | 200 | Colour Linear; thickness `QuadraticEase` EaseOut |
| `OdometerFlip` | Uptime goes live | Each of 8 glyph containers: `TranslateTransform.Y` 10→0, `Opacity` 0→1; 30 ms `BeginTime` stagger (total 330 ms) | 120 | `QuarticEase` EaseOut |
| `PowerOff` (`DisarmCollapse`) | Stop command issued | Sweep-arc `Angle` 360→0; tick-mask radius 0.62→0.00; ambient pool `Opacity` 0.46→0 (220 ms); scanner `Opacity` →0 (180 ms); glyph `Stroke` →`#6E7A85` (140 ms); `RunPulseOrbit` stopped at 260 ms | 260 | `QuinticEase` EaseIn |
| `RunPulseOrbit` (running loop) | State = Running, `RepeatBehavior=Forever` | Scanner-arc `RotateTransform.Angle` 0→360 (centre 112,112). **Linear — any easing reads as a mechanical fault** | 2600 | none (Linear) |
| `RunBreathe` (running loop) | State = Running, `RepeatBehavior=Forever`, `AutoReverse=True` | Ambient pool `Opacity` 0.40↔0.52. **`Opacity` only — never `BlurRadius`, never `Color`** | 2400 | `SineEase` EaseInOut |
| `StatusDotPulse` | State = Running, `Forever` | Title-bar dot `Opacity` 1.0→0.45 (`AutoReverse`); halo `Ellipse` (Ø8, 1px accent stroke) `ScaleX/Y` 1.0→2.4 and `Opacity` 0.50→0 | 1400 dot / 1800 halo | Dot `SineEase` EaseInOut; halo `CubicEase` EaseOut |
| `ToggleThrow` | Switch clicked | Knob `TranslateTransform.X` 0→16; track `Background` `ColorAnimation` `#262D37`→AccentMid; knob `Fill` `#94A1B2`→`#EAEFF6`; knob `ScaleX` 1.0→1.14 (55 %) →1.0 | 160 | X + colour `CubicEase` EaseOut; `ScaleX` `CubicEase` EaseOut |
| `ListStagger` | Стратегии / Настройки page enters | Each row `TranslateTransform.X` −12→0 and `Opacity` 0→1; `BeginTime` stagger 22 ms per row, **hard-capped at index 13** (rows 14+ all use 286 ms) | 160 | `CubicEase` EaseOut |
| `DiagRowReveal` | A diagnostics check completes | Row `Opacity` 0→1, `TranslateTransform.Y` 6→0; status glyph `ScaleX/Y` 0.6→1.0; row background flashes `BrushAccentWash`→Transparent over 700 ms | 180 | `CubicEase` EaseOut; flash Linear |
| `LogLineAppend` | New line in the log list | Line `Opacity` 0→1 (160 ms), `TranslateTransform.X` −10→0 (220 ms); 2×14 left accent tick `Opacity` 1→0 with `BeginTime` 220 ms over 900 ms | 220 | Entry `CubicEase` EaseOut; tick fade Linear |
| `ValueTick` | A monospace metric value changes | Value `TranslateTransform.Y` −2→0 (90 ms); `Foreground` keyframes `#EAEFF6` → AccentMid (15 %) → `#EAEFF6` (100 %) over 400 ms | 90 | Y `QuadraticEase` EaseOut; colour Linear |
| `Indeterminate` | Long operation (install service, update lists) | A 96-wide `Rectangle` (`LinearGradientBrush` transparent → AccentMid → transparent) `TranslateTransform.X` `−96 → TrackWidth`, `RepeatBehavior=Forever`, on a 2px 6-radius track | 1100 | Linear |
| `DiagScanBar` | Diagnostics run in progress | 2px full-width bar `TranslateTransform.Y` 0 → (cardHeight − 2), `Forever` | 1100 | Linear |
| `ModalRaise` / `ModalDismiss` | Dialog open / close | Scrim `Opacity` 0→0.85 (`BrushScrim`); card `ScaleX/Y` 0.98→1.00, `Opacity` 0→1, `TranslateTransform.Y` 10→0. Dismiss = reverse over 130 ms `QuadraticEase` EaseIn | 190 | `CubicEase` EaseOut |
| `ToastIn` / `ToastOut` | Background result reported | In: `TranslateTransform.Y` 16→0, `Opacity` 0→1. Out after 4000 ms dwell: `Opacity` 1→0, `Y` 0→−8 over 140 ms | 200 | In `CubicEase` EaseOut; out `QuadraticEase` EaseIn |
| `Fault` | winws.exe exited non-zero / WinDivert blocked | Dial `TranslateTransform.X` keyframes 0, −7, +6, −4, +2, 0 at 0/70/150/230/310/420 ms; sweep arc + glyph `ColorAnimation` → `BrushDanger` (140 ms) | 420 | Per-keyframe `CubicEase` EaseOut |
| `FocusRingFade` | `IsKeyboardFocusVisible` becomes true | Focus `Border.Opacity` 0→1. No scale, no colour, no size animation | 90 | Linear |
| **`ReducedMotion = On`** | Settings toggle, **or** auto-forced when `SystemParameters.ClientAreaAnimation == false` **or** `RenderCapability.Tier >> 16 < 2` | **Disables:** `AmbientDrift`, `RunPulseOrbit`, `RunBreathe`, `StatusDotPulse`, `DiagScanBar`, `Indeterminate` (bar becomes a static 30 % fill), `Ignite`, `LockPulse`, `OdometerFlip`, `Fault` shake, `ListStagger`, `LogLineAppend` X-slide, `CardHoverLift` Y-lift, all overshoot (`BackEase` → `CubicEase`). **Keeps, at 120 ms Linear:** `PageSwap` opacity, `NavIndicatorSlide` position, `ToggleThrow` X, `ModalRaise` opacity, `ToastIn/Out` opacity, `FocusRingFade`. `ArmSweep` becomes an instant `Angle=360` set. On Tier < 2 the two `BlurEffect`s on `AmbientHost` are also set to `null` and `AmbientHost.Opacity` drops to 0.35. | — | — |

Additionally: subscribe to `Window.Deactivated`, `Window.Activated` and `StateChanged`. On deactivate or minimise, call `Pause()` on `AmbientDrift`, `RunPulseOrbit`, `RunBreathe`, `StatusDotPulse`; `Resume()` on activate. Idle CPU must be ≈0 %.

---

## 8. Per-page composition

All pages: `Background=BrushBgSunken`, padding `32,24,32,24`, content `MaxWidth=908`, page header (56) + 20px gap as defined in §5, then the body (592 tall at default window size).

### Панель
1. **Page header** 908×56 — «Панель» / «Обход DPI · Discord и YouTube»; right = strategy chip 260×32.
2. **Body grid** 908×592 — columns `584 | 24 | 300`.
3. **Left: power well** 584×592. `Border` radius 8, `Background=#0A0D12`, 1px `BrushHairlineWeak`, padding 32 → inner 520×528. Vertical stack:
   - 16px top gap → **dial assembly 224×224** (§6), horizontally centred.
   - 20px gap → state caption (16 tall).
   - 12px gap → uptime readout (32 tall) → 4px → «ВРЕМЯ РАБОТЫ» micro-label (14 tall).
   - 28px gap → **scope strip 520×96**: `Background=BrushBgSunken`, radius 0, 1px `BrushScopeGrid` baseline at the bottom, four 1px `BrushScopeGrid` graticules at 24px vertical pitch, a 1px AccentMid `StreamGeometry` trace of 240 samples, a 3px leading-edge dot, and a 40px vertical wash under the trace (`LinearGradientBrush` AccentMid @ `2E` → transparent). Scrolled by `TranslateTransform.X` stepping −4 px per 50 ms sample; the geometry is rebuilt **only** when X crosses one sample width, driven by a 50 ms-throttled `CompositionTarget.Rendering` handler (never a `DispatcherTimer` at Render priority). Micro-label «ПАКЕТОВ / С» top-left inside, 11px.
   - Remaining space → bottom-anchored **strategy chip row 520×44**: radius 6, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 14,0 — «Стратегия» 12.5 tertiary left, name 13px `BrushTextPrimary` + 10px chevron right. Click → Стратегии page.
4. **Right column** 300×592, vertical stack, 16px gaps:
   - **Metric tile 300×112** «ПРОЦЕСС» → value `8144` 22px mono + unit «PID» 12px; second line 11.5px `BrushTextSecondary` «46 МБ · winws.exe».
   - **Metric tile 300×112** «ПАКЕТОВ / С» → value 22px mono; a 268×20 1px `BrushAccentDim` sparkline flush to the bottom-left inset.
   - **Metric tile 300×112** «СЛУЖБА» → value «Установлена» 15px `FontUI` SemiBold; second line 11.5px «автозапуск включён», with an 8px status dot.
   - **Quick-toggle panel 300×208**: 11px micro-label header «БЫСТРЫЕ ПЕРЕКЛЮЧАТЕЛИ», then three 44px rows separated by 1px `BrushHairlineWeak`: «Автозапуск», «GameFilter», «Автообновление списков» — 13px label left, 34×18 switch right (track radius 9, knob Ø14, 2px inset, 16px throw). Bottom 16px holds a 268×32 secondary button «Открыть журнал».

Tile internal spec: radius 8, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 16; micro-label on line 1; value baseline 40px below it.

### Стратегии
1. Page header — «Стратегии» / «22 профиля · выбран general (ALT4)»; right = 240×32 search box (`BrushInputBackground`, radius 6, 1px `BrushHairlineWeak`, 14×14 magnifier `Path`, 13px text, placeholder `BrushTextTertiary`).
2. 8px gap → **filter chip row 908×28**: chips «Все» / «Discord» / «YouTube» / «ALT» / «Игры», each 26 tall, radius 4, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, 11.5px; selected = `BrushAccentWash` + `BrushTextPrimary` + 1px `BrushAccentDim`.
3. 16px gap → **`VirtualizingStackPanel` list**, 908 wide, remaining height, custom scrollbar. Row: **908×64**, radius 6, 4px vertical gap, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 16,10. Line 1: strategy name 13px SemiBold `BrushTextPrimary`, plus an optional 20-tall `BrushWarning` chip «ALT» / `BrushInfo` chip «игры». Line 2: the raw `winws.exe` argument line, 11.5px `FontMono` `BrushTextTertiary`, single-line, `TextTrimming=CharacterEllipsis`. Right edge: 28×28 icon button «Подробнее» (opens the argument dialog). Selected state: `Background=BrushAccentWash`, plus a 2×24 `BrushNavIndicator` tick pinned x=0, vertically centred.
4. Bottom-anchored **action bar 908×56**, separated by a 1px `BrushHairlineWeak`: right-aligned «Применить» primary 132×36 + 12px gap + «Тест стратегии» secondary 148×36.

### Диагностика
1. Page header — «Диагностика» / «Проверка окружения»; right = «Проверить всё» primary 132×32.
2. **Summary card 908×88**: radius 8, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 20. Left: 40×40 status glyph circle (`BrushSuccess` / `BrushWarning` / `BrushDanger` 1.5px stroke), 16px gap, «Всё в порядке» 15px SemiBold + 11.5px secondary «8 из 8 проверок пройдено». Right: 200×6 progress track (radius 3, `BrushInputBackground`) with an accent-gradient fill; during a run the `DiagScanBar` 2px bar sweeps the card vertically.
3. 16px gap → **check list**, rows **908×56**, 4px gap, radius 6, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 16,0. Each row: 18×18 status `Path` (check / triangle / cross, `StrokeThickness=1.5`) → 14px gap → 13px name («WinDivert драйвер», «Служба zapret», «Целостность списков», «Порты 80/443», «Конфликт с VPN», «Версия winws.exe», «Права администратора», «Свободное место») → flexible gap → 11.5px `FontMono` `BrushTextSecondary` result value → 12px gap → 28×28 «Исправить» icon button, shown only on WARN/FAIL rows.
4. Rows reveal with `DiagRowReveal` as each check completes, in completion order.

### Журнал
1. Page header — «Журнал» / «winws.exe · сессия 02:14:37»; right = 3 level chips («Всё» / «Предупреждения» / «Ошибки», 26 tall) + 12px gap + «Очистить» secondary 96×32 + 8px + «Сохранить…» secondary 116×32.
2. **Log viewport 908 × remaining**: `Border` radius 8, 1px `BrushHairlineWeak`, `Background=BrushBgSunken`, `Padding=0`, `RenderOptions.ClearTypeHint=Enabled`, virtualised, auto-scroll-to-end with a sticky-bottom rule (stop auto-scrolling if the user scrolls up; show a 132×28 «К последним» pill at the bottom-centre while detached).
3. Row: height 20, radius 0, columns — 2px level tick (`BrushAccentDim` fading per `LogLineAppend`, or `BrushWarning` / `BrushDanger` held) | 10px gap | timestamp `21:04:57` 11.5px `FontMono` `BrushTextTertiary` | 12px gap | message 11.5px `FontMono` `BrushTextSecondary` (`BrushWarning` / `BrushDanger` for those levels), `TextWrapping=NoWrap`. Hover row → `BrushSurfaceHover`. `Ctrl+C` copies selected lines.
4. Bottom **status strip 908×32**, 1px top hairline: left «1 284 строки», right a 34×18 switch «Автопрокрутка».

### Настройки
1. Page header — «Настройки» / «Приложение и служба»; right = «Сбросить» secondary 108×32.
2. **Group card «Внешний вид» 908×176**: radius 8, `BrushSurfaceRaised`, 1px `BrushHairlineWeak`, padding 20. Header micro-label, then: a 44px row «Акцент» with the **accent picker** on the right — five 28×28 rounded squares (radius 6) filled with each preset's gradient, 8px gaps; the selected one carries a 1px `BrushTextPrimary` ring at 2px offset. Then a 1px hairline and a 44px row «Уменьшить анимацию» with a switch. Then a 44px row «Запускать свёрнутым в трей» with a switch.
3. 16px gap → **Group card «Служба» 908×220**: rows «Автозапуск при входе» (switch), «Перезапускать при сбое» (switch), «Стратегия по умолчанию» (combo 260×32), and an action row with «Установить службу» primary 160×36 / «Удалить службу» danger-outline 148×36 (1px `BrushDanger` border, `BrushDanger` text, hover fill `BrushDanger` @ 0.12).
4. 16px gap → **Group card «Списки и обновления» 908×176**: «Автообновление списков» (switch), «Проверять обновления при запуске» (switch), «Обновить сейчас» secondary 148×36 with the last-check timestamp 11.5px `FontMono` `BrushTextTertiary` beside it.
5. 16px gap → **Footer card 908×88** «О программе»: «Zapret GUI 1.0 · ядро 1.10.0» 13px, «Форк zapret-discord-youtube» 11.5px secondary, and a text-link button «Открыть папку установки».

---

## 9. Empty / error / loading states

**Empty (list has no items).** Centred in the viewport, vertical stack, `MaxWidth=320`: a 48×48 `Path` outline glyph, `StrokeThickness=1.25`, `BrushTextDisabled`; 16px gap; a 15px SemiBold `BrushTextSecondary` headline; 6px gap; a 12.5px `BrushTextTertiary` explanatory line, centred, line height 18; 20px gap; one secondary button 160×36 when an action exists. Copy: Стратегии → «Ничего не найдено» / «Измените запрос или сбросьте фильтры» + «Сбросить фильтры». Журнал → «Журнал пуст» / «Записи появятся после запуска обхода». Диагностика → «Проверка ещё не выполнялась» / «Нажмите «Проверить всё»». No animation beyond the page's own `PageSwap`.

**Loading — skeleton (structure is known).** Cards and rows render at their real sizes with `Background=#0C1017` and no border content; inside each, 1–2 `Rectangle`s at 60 % / 35 % width, height 10, radius 5, `Fill=#131820`. A single shared shimmer `LinearGradientBrush` (transparent → `#0AFFFFFF` → transparent, 240 wide) sweeps left→right across the whole skeleton container via one `TranslateTransform.X`, 1400 ms, Linear, Forever — **one animated brush for the entire page, never one per skeleton block**. Disabled under ReducedMotion (the skeletons render static).

**Loading — indeterminate (duration unknown, structure unknown).** The `Indeterminate` bar (§7) at the top of the affected card, 2px tall, full card width, radius 0, plus the button that triggered it entering a disabled state with its label replaced by «Выполняется…».

**Loading — the dial.** Never a spinner. Arming state = the `ArmSweep` sweep itself; the caption reads «ЗАПУСК…» in `BrushStateArming` and the dial hit target is disabled until the state resolves.

**Error — inline (a card's data failed).** The card keeps its frame; content is replaced by a left-aligned 20×20 `BrushDanger` triangle glyph, 12px gap, 13px `BrushTextPrimary` short cause, 11.5px `BrushTextSecondary` detail, and a 96×28 «Повторить» secondary button. The card border becomes 1px `BrushDanger` at 45 % alpha (`#73FF5F6D`). No shake, no red fill.

**Error — the dial (Fault).** `Fault` animation (§7): 420 ms shake, arc and glyph go `BrushDanger`, caption «ОШИБКА ЗАПУСКА», ambient pool stays at 0. A 908×48 error banner appears directly under the page header — radius 6, `Background=#14FF5F6D`, 1px `#73FF5F6D`, 16×16 danger glyph, 13px message, right-aligned text-button «Показать журнал». The banner is dismissible and does not auto-hide.

**Error — fatal (winws.exe or WinDivert.dll missing at startup).** The dial renders with `BrushTextDisabled` glyph, is `IsEnabled=False`, caption «БИНАРНЫЙ ФАЙЛ НЕ НАЙДЕН», and the persistent banner offers «Выбрать папку zapret…». Nav remains fully usable — Диагностика and Настройки must still work.

**Toast.** 320 wide, min-height 56, radius 12, `BrushSurfaceOverlay`, 1px `BrushHairlineStrong`, E2 shadow, bottom-right of the content host with a 24px inset. Left: 4px full-height accent bar (`BrushSuccess` / `BrushWarning` / `BrushDanger` / accent). 16px padding, 13px SemiBold title + 11.5px secondary detail. Dwell 4000 ms; hovering the toast pauses the dwell timer. Maximum three stacked, 8px gaps, oldest evicted first.

---

## 10. Performance budget

**BlurEffect — exactly two instances in the whole application**, both on the ambient ellipses inside `AmbientHost` (Radius 110 and 140). Nowhere else. `AmbientHost` carries `CacheMode="BitmapCache" RenderAtScale="0.30" EnableClearType="False"`, so the blur is rasterised once at ~⅓ resolution and the infinite drift runs purely through `RenderTransform` and `Opacity`, which do not invalidate the cache. On `RenderCapability.Tier >> 16 < 2` both effects are set to `null` at startup.

**DropShadowEffect — at most 2 concurrent in the steady state, hard ceiling 3 transient.** Permitted: (1) the E2 popup/toast shadow, (2) the E3 modal dialog shadow. **The power dial has no DropShadowEffect at all** — its glow is a `RadialGradientBrush` ellipse (§6a). The nav indicator, buttons, status dot, chips, cards and tiles get **no** shadow; their separation comes from the achromatic ladder `BgSunken → BgBase/BgVoid → SurfaceRaised → SurfaceOverlay` plus 1px hairlines. There is no modal backdrop blur — the scrim is a flat `BrushScrim` fill, which is cheaper and does not force a `VisualBrush` snapshot.

**Must carry `CacheMode="BitmapCache"`:** `AmbientHost` (RenderAtScale 0.30, ClearType off), the dial's ambient pool ellipse (a), the shock ring (i), and the modal dialog card while its open/close storyboard runs (release the cache in `Completed`). Nothing else — `BitmapCache` on text-bearing containers destroys ClearType.

**Must be frozen at startup:** every `SolidColorBrush` in `Colors.xaml`, both tick-ring `DrawingBrush`es and their `GeometryGroup`s, all icon `PathGeometry`s, all `Pen`s. Call `.Freeze()` explicitly; a static `ResourceDictionary` brush that later needs a `ColorAnimation` must instead be declared **locally inside the control template** (or `x:Shared="False"`) — animating a frozen shared brush throws `InvalidOperationException`, and animating a shared unfrozen one mutates it application-wide.

**Must never be animated:** `DropShadowEffect.BlurRadius`, `DropShadowEffect.Color`, `BlurEffect.Radius`, `Width`, `Height`, `Margin`, `Padding`, `GradientStop.Offset` on any brush inside a cached layer, `FontSize`, and any property on a layout-affecting element that would trigger a measure/arrange pass every frame. Everything animates through `RenderTransform`, `Opacity`, `Color` on a local brush, or a custom `AffectsRender` DP (`ArcSegmentShape.Angle`, `RadialGradientBrush.RadiusX/Y` on the tick mask).

**Visual-count rules:** the 60 bezel ticks are one `DrawingBrush` per ring (2 visuals total, not 120). All decorative dial layers set `IsHitTestVisible=False`; only the 224×224 transparent `Ellipse` in the `ToggleButton` template hit-tests. The strategy list and log list use `VirtualizingStackPanel` with `VirtualizationMode=Recycling` and `ScrollUnit=Pixel`. The scope keeps one 240-sample `StreamGeometry` and scrolls it with a transform, rebuilding only when the transform crosses one sample width, on a 50 ms-throttled `CompositionTarget.Rendering` handler.

**Idle target:** with the window unfocused or minimised, all looping storyboards are paused and CPU must read ≈0 %. With the window focused and the bypass running, the steady-state budget is one 2600 ms rotation, one 2400 ms opacity breathe, one 1400 ms dot pulse, and the 20 Hz scope — combined under 3 % on an integrated GPU.

**DPI:** all dimensions above are logical DIP. Every icon is `Path`/`Geometry`, never a bitmap. `UseLayoutRounding=True` and `SnapsToDevicePixels=True` on the window root; verify at 125 / 150 / 175 % that no 1 DIP hairline disappears or doubles.