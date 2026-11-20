# BoxMate 1.6.2

## Added

- Adds an **Update all** action when installed mods, tools, or required dependencies
  have newer releases available.
- Shows the number of available package updates beside the catalogue summary.

## Behaviour

- Updates only packages recorded as installed and outdated.
- Preserves each package's selected Stable, Beta, or Alpha release channel.
- Resolves required dependencies, verifies checksums, and uses BoxMate's existing
  backup and rollback flow for every package.
- Leaves current, uninstalled, modified, and deprecated packages unchanged.
