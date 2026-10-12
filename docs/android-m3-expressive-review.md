# M3 Expressive design review — user-selected skill

Used the full entry point and relevant references from
https://github.com/abhixv/m3-expressive-design-skill at commit
5e7a9cd90d62bbff2dbe7a708206aeb4003e8a4e, not just its README/frontmatter.
Read tokens, component geometry, containment/lists, input selection, layout, color and
platform availability. Material guidance © Google LLC (CC BY 4.0), AndroidX tokens © AOSP
(Apache-2.0); authored skill organization © abhixv (MIT). No reference images copied.

## From the references

- Card baseline corners 12dp and horizontal padding 16dp; smaller corners for dense information.
- Corner scale 4/8/12/16/20/28/32/48/full, not fabricated intermediate named tokens.
- Compact margin 16dp, medium/expanded 24dp, expanded threshold 840dp; retained nav bar/rail swap.
- Lists preferred over repeated big cards on small screens; consistent leading/content/trailing slots.
- Single selection uses radio with one row action; highlight covers the entire row.
- Emphasized title-medium retains 16sp/24sp/0.15sp but uses Bold; reserved for selected nodes.
- Body-medium 14sp/20sp for node support; primaryContainer paired with onPrimaryContainer.
- Filled-card surfaceContainerHighest; outlineVariant for divider grouping, not per-item heavy rules.
- Contained lists use spacing; dividers group major sections. Removed per-node/per-app dividers.
- Compose is appropriate for Android; existing Expressive spring/shape APIs remain opt-in.

## Product decisions, not claims of additional M3 specs

- Profile layout: group chips, one neutral active summary, compact node selection rows.
- Connection status is the high-emphasis region; not every panel is an oversized hero.
- Selected node rectangles are inset in their card; no reused parent/child corner radius.
- Section gap 24dp, node batches 20 and capped reading column 680dp are product decisions.
- Eight seed colors and package-VPN switches are product features; accents feed semantic
  MaterialKolor schemes, not fixed foreground/background pairs. Switches represent independent
  app VPN settings rather than a batch multi-selection operation.
- Custom node-slot wrapper uses actual Compose Surface/RadioButton semantics. This does not
  claim that the newer December-2025 Expressive List API exists in pinned alpha08.
- Material3 stays 1.5.0-alpha08 compatible with SDK35; no untested API upgrade or guessed
  shape/spring names. The external skill does not override security behavior or release gates.

Visual QA renders actual Compose: populated groups/nodes, blue seed, Persian dark, app policy,
settings and tablet. Host tests are not physical-device VPN acceptance tests.
