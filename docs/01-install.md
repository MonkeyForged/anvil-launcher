# Install

Anvil ships two kinds of release, both **only on GitHub** — there is no AUR package and no
Flathub entry. That is deliberate: a sandboxed app cannot see the host's process list, and
Anvil needs exactly that to know whether a game is running.

Both builds are framework-dependent: they need the .NET 10 runtime already on your system, not
bundled with the app. Anvil itself has been tested on exactly one machine — CachyOS, KDE Plasma
on Wayland, native Steam. If you run something else, you are the first; reports are welcome.

Grab the files from the
[latest release](https://github.com/MonkeyForged/anvil-launcher/releases/latest).

## Arch, CachyOS, EndeavourOS

```bash
sudo pacman -U anvil-launcher-<version>-1-x86_64.pkg.tar.zst
```

This pulls in `dotnet-runtime` if you do not already have it, and adds Anvil to your
application menu. `<version>` is whatever you downloaded, for example `1.3.1`.

Prefer to build the package yourself? Download the `.tar.gz` from the same release, place it
next to `packaging/PKGBUILD` from the source repository, and run `makepkg -si`.

pacman also lists `bubblewrap` as an optional dependency — see
[Optional: bubblewrap](#optional-bubblewrap) below for what it is for.

## Other distributions (tarball)

```bash
tar xzf anvil-launcher-<version>-linux-x64.tar.gz
cd anvil-launcher-<version>-linux-x64
./anvil-launcher
```

No installation and no root access needed. You do need the .NET 10 runtime already installed —
`dotnet-runtime-10.0` on Debian, Ubuntu and Fedora, or see
[Microsoft's instructions](https://dotnet.microsoft.com/download/dotnet/10.0) for anything else.
The build is framework-dependent on purpose: about 8 MB to download instead of roughly 90.

## Optional: bubblewrap

[CE Bridge](07-ce-bridge.md) can fetch Cheat Engine from its vendor for you. The vendor's
installer is someone else's program, so Anvil opens it only inside `bubblewrap`, a small
sandbox tool, where it cannot reach your home folder.

**Without it, Anvil works as before — only `Get CE 7.7` is out of reach.** The button stays
disabled, and the one way left to give CE Bridge a Cheat Engine is to put a complete
installation into its folder yourself; see
[Without bubblewrap](07-ce-bridge.md#without-bubblewrap). If you do not use CE Bridge, or
already have a Cheat Engine folder, you can skip this section.

The package is called `bubblewrap` on Arch, CachyOS and EndeavourOS, and on Debian, Ubuntu and
Fedora too:

```bash
sudo pacman -S bubblewrap
```

Anvil looks for it again every time the `Settings` page is shown, so there is no need to
restart Anvil after installing it.

## Updating

There is no update mechanism inside Anvil; check the
[releases page](https://github.com/MonkeyForged/anvil-launcher/releases) yourself.

- **Arch package:** download the new version and run `sudo pacman -U` on it again, the same
  way as the first install. Pacman replaces the old files.
- **Tarball:** download the new tarball and extract it to a fresh folder, then run it from
  there. Delete the old folder once you are sure the new one works.

## Uninstalling

- **Arch package:** `sudo pacman -R anvil-launcher`
- **Tarball:** delete the folder you extracted.

Either way, only the program itself is removed. Your library data, settings and backups stay
where they are — see
[Where Anvil keeps its own files](02-first-run.md#where-anvil-keeps-its-own-files) if you want
to remove those too.
