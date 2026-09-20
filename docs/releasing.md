# Release procedure

Maintainer: [bbauti](https://github.com/bbauti). The repository's source, tests and documentation are public; generated ZIPs belong in release assets rather than Git history.

## Prepare

1. Review the [testing guide](testing.md), [verification history](verification.md) and open issues. State which OS/game versions were actually exercised.
2. Set the same version in `Directory.Build.props` and `src/Ember.Mod/modinfo.json`; update `CHANGELOG.md` and the README's current install example. Do not reuse a version for a changed ZIP after it has been publicly released.
3. Run the applicable checks. Public CI covers game-independent code and the synthetic viewer. Full packaging and gameplay checks require a locally installed, licensed copy of Vintage Story.
4. Build on a trusted local machine:

   ```powershell
   pwsh -File scripts/build.ps1 -GamePath 'C:\path\to\Vintagestory'
   ```

5. Inspect the package using the [package checklist](verification.md#package-inspection). Preserve `.ignore` with `*tracer/*`, include the complete worker publish output and licenses, and exclude game assemblies.
6. Install the resulting ZIP into a separate game data profile. Check startup, `.ember health`, recording/stop/export and the offline report. For lifecycle changes, rerun the relevant lifecycle scenarios.
7. Record the SHA-256 and exact build/test scope. Keep private captures and local paths out of published evidence. Viewer-only checks are not a new gameplay soak.

## Publish on GitHub

- Commit the release source and create a matching `v<version>` tag.
- Create a GitHub Release with the mod ZIP and its `.sha256` file. Optionally attach a byte-for-byte copy of `viewer/report.html` as `ember-viewer-<version>.html` with its own checksum; it can open older `report.json` exports.
- Use a prerelease when collecting early feedback. Clearly describe runtime prerequisites, platform coverage, known issues and changes.
- The repository's AI disclosure and license apply to releases too. The packaging scripts include the relevant root documents and `docs/`.

## Publish on Vintage Story ModDB

Follow the current [ModDB terms](https://mods.vintagestory.at/terms), [mod metadata reference](https://wiki.vintagestory.at/Modding:Modinfo) and [upload guide](https://www.vintagestory.at/forums/topic/18633-upload-your-mod-to-the-moddb/).

- Publish as **Client** and select only game versions that have been verified.
- Provide a clear English description, installation/command instructions, actual screenshots and the repository link.
- Credit [spark](https://spark.lucko.me/) and [Lithos Probe](https://mods.vintagestory.at/probe) as the principal motivations.
- State that the implementation was written entirely with AI assistance using **OpenAI GPT-6 Astra**. Explain that AI is not required at runtime and recordings are not automatically uploaded.
- Explain the external .NET 10 worker and that finalization can continue after game exit. State current Windows validation and unvalidated Linux/macOS workflows.
- Do not advertise zero overhead, exact CPU attribution, guaranteed anonymity or automatic identification of a culpable mod.

## Repository settings

Before inviting external reports, enable Issues and GitHub private vulnerability reporting, verify Actions can run, and set the About description/homepage. Consider protecting `main` against force pushes and requiring the CI checks on pull requests. These are GitHub settings, not files that a clone can enable.

Suggested About description (the full linked credits and AI disclosure remain in the README):

> Client-side performance diagnostics for Vintage Story with offline reports. Written entirely with AI assistance (OpenAI GPT-6 Astra). Inspired by spark https://spark.lucko.me/ and Lithos Probe https://mods.vintagestory.at/probe.

Suggested topics: `vintage-story`, `vintagestory-mod`, `profiling`, `diagnostics`, `dotnet`, `ai-assisted`.
