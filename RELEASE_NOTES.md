# BoxMate 1.0.0

The first public BoxMate release: a manifest-based BOXROOM mod manager for Windows and Linux/Steam Deck.

## Included

- Built-in official BOXROOM mod catalogue
- One-click mod installation and updates
- Automatic required-mod resolution
- Automatic verified MelonLoader installation
- GitHub sign-in for higher release lookup limits
- Safe extraction, SHA-256 verification, backups, and rollback
- Additional repository management using `OWNER/REPOSITORY` or GitHub URLs
- Windows x64 and native Linux x64 BoxMate builds

## Linux / Steam Deck setup

1. Extract `BoxMate-linux-x64.tar.gz` and run `BoxMate`.
2. Choose the BOXROOM directory containing `BOXROOM.exe`.
3. Install MelonLoader from BoxMate.
4. Paste this into **Steam → BOXROOM → Properties → General → Launch Options**:

```text
WINEDLLOVERRIDES="version=n,b" %command%
```

BoxMate displays a Linux setup card, copies this option after installing MelonLoader, and writes `BoxMate-Linux-Setup.txt` beside the game executable.

The Linux binary and archive structure were cross-build validated. Live Steam Deck/Proton interaction still needs hardware testing.

Verify downloads with `SHA256SUMS.txt`.

The downloadable archives contain the complete self-contained runtime output; no separate .NET installation is required.
