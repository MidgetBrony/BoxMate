# BoxMate v1.4.3

- Prevent ZIP packages that already contain their declared destination folder from being installed into a duplicated path such as `Mods/Mods`.
- Keep support for packages whose ZIP contents still need the manifest destination prepended.
- Together with the corrected ModsPanel manifest, reinstalling ModsPanel now places `ModsPanel.dll` directly in BOXROOM's `Mods` folder.

The Windows and native Linux archives contain the complete self-contained runtime. Live Steam Deck/Proton interaction remains untested for this release.
