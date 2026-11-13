# Release Presser - rules for Claude

- **ALWAYS commit and push to GitHub (`main`) at the end of every change and every build.** Aaron starts every
  session on REACTOR with a pull.
- **If you can't push, end your reply with a huge warning in big letters**, e.g.

  # ⚠️ NOT PUSHED TO GITHUB ⚠️

  and say exactly what is unpushed and why.
- This repo is the engine only. Apps (MAW, MAWP) reference it from the sibling folder `..\ReleasePresser`.
  Don't add app or UI code here; don't break the public API the apps call without updating them too.
- Nullable + ImplicitUsings stay enabled; the namespace stays `Pressing.Core`.
