# Changelog

## [1.0.0] - 2026-06-13

- Initial UPM package release as `com.rotteneagle.urp-ui-blur`
- Screen Space Camera UI blur with per-panel Kawase blur
- Scene View preview support
- Runtime material creation (no material assets required)

## [1.1.0] - 2026-08-05

### Added
- Added support for `Image.Type = Sliced` in blur panels.
- Added transparent panel culling for alpha = 0.

### Changed
- Improved blur mask rendering for sliced sprites.
- Optimized blur rendering by skipping fully transparent panels.