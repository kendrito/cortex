# Cortex version tags

Cortex versions combine the imported DeepSeek Harness version with a positive Cortex release counter. For example, `0.2.0-rc.2.cortex.1` identifies Cortex release 1 on upstream `0.2.0-rc.2`. The root `package.json`, Cortex applications, and Cortex packages share that version. An annotated `cortex-v<version>` Git tag identifies the corresponding commit on `main`. Native Testy and vendored dependencies retain their independent versions; `desktop-v` tags belong to packaged desktop releases.

## Track the upstream baseline

[upstream.json](../upstream.json) records the upstream repository, version, official `dsh-v<version>` tag, full imported commit, and import date (`YYYY-MM-DD`). The commit is the authoritative snapshot. Before importing an upstream release, resolve its official tag and confirm the version in that commit's root manifest. Compare the recorded commit with the proposed commit using GitHub's compare view or a fetched upstream checkout. Integrate the changes while preserving the [Cortex exclusions](cortex-upstream-sync.md#excluded-services), then update the baseline file in the same commit as the completed import.

For Cortex-only releases, leave the baseline unchanged and increment the final counter: `0.2.0-rc.2.cortex.1` becomes `0.2.0-rc.2.cortex.2`. Increment per release, not per commit. When a new upstream version is imported, reset the counter to 1: an import of `0.2.0-rc.3` starts at `0.2.0-rc.3.cortex.1`. Existing Cortex changes remain part of the fork.

For a stable upstream version, use a `stable` marker before the Cortex counter, such as `0.2.0-stable.cortex.1`. This sorts after `0.2.0-rc.2.cortex.1` while retaining the upstream release numbers. These remain Cortex prereleases under SemVer, below plain `0.2.0`; a stable upstream does not declare the fork stable. Do not insert a fourth core number or use `+cortex.N`, whose build metadata does not order releases. Python package publication requires a PEP 440 mapping for the Cortex suffix before it can use this version scheme.

## Prepare a version

Start from a clean checkout with an empty staging area and the repository's declared Node.js and pnpm versions. Create a release branch from current `main`. Commit any completed upstream import and its baseline record before preparing the matching release. For example:

```sh
pnpm run release:cortex 0.2.0-rc.2.cortex.1 --dry-run
pnpm run release:cortex 0.2.0-rc.2.cortex.1
pnpm run release:verify --family cortex
```

The first command previews the manifest changes. The second updates the root, application, and package versions and refreshes the lockfile with installation scripts disabled. It rejects a dirty checkout and leaves the changes unstaged; it does not commit, tag, push, or publish packages. Pass the full version explicitly. The verification command rejects a malformed baseline, a version that does not match the upstream baseline plus Cortex counter, or inconsistent root, public, or private workspace versions. It checks local metadata; verify the official upstream tag separately during an import.

Review the diff and run checks relevant to accompanying code changes. Keep dependency upgrades and unrelated changes separate. Commit the reviewed preparation and merge it into `main`; repository Git hooks run their configured checks.

## Tag the merged commit

Update local `main` with a fast-forward pull. Confirm the checkout is clean and run release verification. The annotation contains the Cortex version and the complete baseline file, so GitHub and local Git both retain the upstream version, tag, commit, and import date:

```sh
git switch main
git pull --ff-only origin main
git status --short
pnpm run release:verify --family cortex
git tag -a cortex-v0.2.0-rc.2.cortex.1 -m "Cortex 0.2.0-rc.2.cortex.1" -m "$(node -p 'JSON.stringify(require("./upstream.json"), null, 2)')"
git push origin refs/tags/cortex-v0.2.0-rc.2.cortex.1
git ls-remote origin refs/tags/cortex-v0.2.0-rc.2.cortex.1 "refs/tags/cortex-v0.2.0-rc.2.cortex.1^{}"
```

The annotation command above uses POSIX shell quoting; on PowerShell, write the title and baseline JSON to a UTF-8 file and pass it with `git tag -a <tag> -F <file>`. The peeled `^{}` result must equal the tested commit on `main`. Push the explicit tag rather than all local tags. Published tags remain fixed; corrections get a new version and tag. Tagging requires neither a GitHub Release nor an EXE build or upload, and this repository has no tag-triggered publishing workflow.

To inspect a historical release, use `git show <cortex-tag>:upstream.json` for its pinned baseline and `git show --no-patch <cortex-tag>` for its annotation. Compare the recorded upstream commits to identify upstream changes; compare Cortex tags to identify the complete fork changes, including local work.
