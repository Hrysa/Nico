# Editor UI Standards

This document defines mandatory visual and composition rules for editor UI. New editor components must use these shared components and theme tokens instead of introducing local font sizes, header heights, colors, or padding values.

## Panel contract

Every persistent docked editor panel must use `ToolPanel`. A tool panel is composed as:

```text
ToolPanel
├── SectionHeader
│   └── Label
└── Content
    └── panel-specific components
```

Do not manually combine `Surface`, `Label`, and content controls to imitate a panel. Add panel-specific children to `ToolPanel.Content`. Standalone viewport sections that need a title use `SectionHeader` directly.

The following theme tokens are authoritative:

| Property | Default | Purpose |
|---|---:|---|
| `Surface` | `#121314` | Header and panel content background |
| `PanelHeaderHeight` | `36` | Height of every panel header |
| `PanelTitleFontSize` | `18` | Panel-title typography |
| `PanelHeaderPadding` | `10` | Left and right title inset |
| `ItemRowHeight` | `30` | Hierarchy and filesystem row height |
| `ItemRowPadding` | `10` | Hierarchy and filesystem row inset |
| `TreeIndent` | `14` | Additional inset per hierarchy depth |
| `TextSecondary` | theme value | Panel-title foreground |
| `Border` | theme value | Panel boundary and separator color |

Headers and content use the same `Surface` color. A panel must not introduce a raised header background, arbitrary header height, hard-coded title font, or custom title padding.

## Typography

- Panel titles use `PanelTitleFontSize` through `SectionHeader`.
- Normal control and content text use `FontSize`.
- Metadata, status text, and secondary toolbar annotations use `CaptionFontSize`.
- Dialog titles use the dialog-header component; they are not panel titles.
- Components render text through a `Label` child whenever the text is presentational. Interactive controls must not duplicate glyph measurement or text painting.
- Hierarchy and filesystem rows both use `FontSize`, `ItemRowHeight`, and `ItemRowPadding`; neither panel may define a smaller local row style.

## Box and content composition

- Visual rectangles derive from `Box` or an existing box-derived control.
- Single-child controls derive from `ContentControl` and place their visual child in `Content`.
- Use `Thickness` and the `Margin`/`Padding` box properties instead of unrelated per-control spacing calculations.
- `Button`, menu items, and list items contain a `Label`; their interaction behavior stays on the parent control.
- Child labels inside interactive controls are not hit-test targets.

## Naming

- Persistent panels use user-facing nouns: `Hierarchy`, `File System`, `Inspector`, `Game`.
- Runtime `Name` values use PascalCase without spaces: `FileSystem`, `FileSystemHeader`.
- Header implementation names are not used as visible captions.

## Review checklist

Before accepting a new or modified editor panel, verify:

1. It uses `ToolPanel`, or `SectionHeader` for a standalone viewport section.
2. Header metrics and colors come exclusively from `UITheme` panel tokens.
3. Panel content is positioned relative to `ToolPanel.Content`, not offset by header height manually.
4. Text uses the correct typography role and is rendered by `Label`.
5. No new hard-coded panel font size, header height, title padding, or panel background color was introduced.
6. Component and editor tests still pass at multiple window sizes.
