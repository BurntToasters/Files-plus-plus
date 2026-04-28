# Commenting Policy

This repository enforces strict comment hygiene for production code in `src/`, `tests/`, and `scripts/`.

## Allowed comment prefixes

- `Why:` explains non-obvious rationale, constraints, or failure handling.
- `Section:` marks major structural boundaries in long files.

## Disallowed comment styles

- Comments that do not start with `Why:` or `Section:`
- Decorative separators and banner lines
- Redundant comments that restate obvious code
- XML documentation comments (`///`) in scoped code files

## Examples

Allowed:

- `// Why: Persisted geometry may be invalid across monitor or DPI changes.`
- `// Section: Window layout helpers`
- `<!-- Why: Transparent root keeps native backdrop rendering consistent. -->`
- `# Section: Build with retry`

Disallowed:

- `// Ignore cancellation.`
- `// ----------`
- `/// <summary>...`
- `<!-- TITLE BAR -->`
