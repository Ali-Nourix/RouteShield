# Modernist — the RouteShield design system

Everything visual in the application resolves back to `src/RouteShield/Theme/Tokens.xaml`.
Component styles live beside it in `Controls.xaml`. Change a token there and the whole
interface follows; nothing should hard-code a colour, a size, or a font.

## Ground

| Role | Value | Where it appears |
| --- | --- | --- |
| Background | `#F3F2F2` | The page, and text reversed out of the ink card |
| Surface | `#EAE9E9` | Inputs, raised cards, the mode bar |
| Text (ink) | `#201E1D` | Copy, rules, the connected card, the selected nav item |
| Accent | `#EC3013` | The connect button, live state, the armed kill switch, focus |
| Accent 2 | `#E15B47` | Reserved for a second signal |

Two tonal ramps — neutral and accent, steps 100 to 900 — sit on one shared lightness scale, so
the same step of either role carries the same visual weight.

Rules and borders are drawn as ink at 40% rather than as a fixed grey, so a divider reads the
same over paper and over a raised surface.

## Type

Archivo throughout, in three instances shipped with the app:

| Token | Instance | Used for |
| --- | --- | --- |
| `HeadingFont` | Archivo ExtraBold | Titles, figures, buttons, state words, kickers |
| `MediumFont` | Archivo SemiBold | The selected nav item, setting names |
| `BodyFont` | Archivo Regular | Everything else |
| `MonoFont` | Cascadia Mono | The configuration editor and the event log |

The scale: status headline 46, page title 30, stat figure 32, section title 16, setting name 14,
row label 13.5, help text 12.5, caption 11.5, kicker and state word 10, field label 9.

Uppercase labels are tracked. WPF has no letter-spacing property, so `Ui/Typo.Tracked` draws the
spacing with hair spaces; it is only ever applied to short decorative labels, never to body copy.

## Shape and space

Radii are 8 (small), 10 (medium, the default for buttons and inputs), 14 (cards) and a pill for
state tags. Spacing steps are 4, 8, 12, 16, 24 and 32.

Elevation is used sparingly: only dialogs, popups and the process picker carry a shadow. On the
page, hierarchy comes from the ground colour and from the rule weight — 2px under a section
heading, 1px between the rows beneath it.

## Components

- **Buttons.** Primary is accent on the ground colour; secondary is a hairline ink border;
  ghost is accent text with no border; inverse is reversed out of the ink card. All carry the
  heading font, so anything clickable reads as structural.
- **Inputs.** Surface ground, hairline border, accent on focus, placeholder from `Tag`.
- **Checkboxes** are 16×16 with a 5px radius: ink-filled with a light tick when on, a neutral
  hairline when off. **Radios** are a 16px ring that fills with accent.
- **Navigation** items carry a two-digit ordinal and turn into an ink pill when selected.
- **State tags** are pills: ink for on, accent for a state that carries risk, a hairline outline
  for off.

## Screens

The window is its own chrome: a 40px bar with the mark, the product name and the version, then a
220px sidebar over a content area. Each page opens with a kicker, a title, and its actions on the
right. The dashboard carries the status card — ink when connected, surface when not — three
figures, and two lists. Profiles, Applications, Security and Diagnostics follow the same header
and rule rhythm.
