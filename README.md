# Release Presser

The **Release Presser** engine (`Pressing.Core`): turns a folder of downloaded/ripped releases for one artist or label
into a clean, tagged, flat library folder. Before → Review → After, nothing lost, every run undoable.

This repo is **only the engine** (a .NET 9 class library, namespace `Pressing.Core`, assembly `ReleasePresser`).
The apps that use it keep their own UI:

| App  | Repo                    | How it uses Release Presser                     |
|------|-------------------------|-------------------------------------------------|
| MAW  | `AaronPierdon/MAW`      | RELEASE PRESSER tab (`src/MAW/Core/Presser`)    |
| MAWP | `AaronPierdon/MAWP`     | its own Presser screen                          |

## Layout: a sibling folder

Every app expects this repo **next to it**, not inside it:

```
source\repos\
    MAW\              <- the app
    MAWP\             <- the app
    ReleasePresser\   <- this repo (one copy, shared)
```

and references `..\ReleasePresser\src\ReleasePresser\ReleasePresser.csproj`. MAW's build scripts clone this repo there
if it's missing and pull it before every build, so a Presser fix lands in every app on its next build.

## Fixing a bug

1. Fix it here, build (`dotnet build ReleasePresser.sln`), commit, **push**.
2. Rebuild the app(s) - their build scripts pull the new engine.

Keep the engine app-neutral: no MAW / MAWP / UI types in here. Nullable + ImplicitUsings stay enabled (the engine's
tested compile semantics).

## History

Started as Pressing.Core in `AaronPierdon/Pressing` (the original standalone app), then carried inside MAW as
`MAW.Pressing`, where it gained main-artist inference (consolidate a folder under the artist the metadata says it is,
flag releases clearly by someone else). This repo starts from MAW's copy - the newest.
