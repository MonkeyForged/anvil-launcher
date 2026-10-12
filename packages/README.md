# packages/

A local NuGet source holding exactly one package: the design package
`MonkeyForged.Design.Avalonia`, in the version `src/Anvil.App/Anvil.App.csproj` asks for.

## Why it is in the repository

Anvil Launcher builds without an account and without a token. The package is not on
nuget.org, so it travels with the source instead of coming from a private feed. The root
`nuget.config` lists this folder as a package source and maps `MonkeyForged.*` to it and to
nothing else; every other package still comes from nuget.org. With this folder missing, a
restore fails and names `MonkeyForged.Design.Avalonia` instead of searching elsewhere.

## Licence

Anvil's own code is MIT (see `LICENSE` in the repository root). The package in this folder
is not part of it:

- `MonkeyForged.Design.Avalonia` is © MonkeyForged, all rights reserved. It is included
  only to build and run Anvil Launcher.
- The fonts bundled in the package are licensed under the SIL Open Font License 1.1. Their
  licence texts ship inside the package, under `licenses/` in the `.nupkg` (a `.nupkg` is
  a zip archive).

## Bringing in a new version

1. Put the new `.nupkg` into this folder and delete the old one. Exactly one version stays.
2. Set the exact version in `src/Anvil.App/Anvil.App.csproj`, in the property
   `MonkeyForgedDesignVersion` (a fixed version and not a range). The package reference and
   the check for the file both read it.
3. Run the gates: restore, build without warnings, tests, format.
4. Render the manual pictures again and look at them: a new package version can change the
   look.

A version in this folder that differs from the one the project asks for makes the restore
fail and name the package.
