# Cortex version tags

Cortex has its own version sequence, independent of the [imported upstream baseline](cortex-upstream-sync.md). The root `package.json`, Cortex applications, and Cortex packages share one version. An annotated `cortex-v<version>` Git tag identifies the commit containing that version on `main`. Native Testy and vendored dependencies retain their independent versions. The separate `desktop-v` convention belongs to packaged desktop releases.

## Prepare a version

Start from a clean checkout with an empty staging area and the repository's declared Node.js and pnpm versions. Create a release branch from current `main`. For example, prepare a beta version with:

```sh
pnpm run release:cortex 0.3.0-beta.1 --dry-run
pnpm run release:cortex 0.3.0-beta.1
```

The first command previews the manifest changes. The second updates the root, application, and package versions and refreshes the lockfile with installation scripts disabled. It rejects a dirty checkout and leaves the changes unstaged; it does not commit, tag, push, or publish packages.

Review the diff and run `pnpm run release:verify --family cortex` plus checks relevant to any accompanying code changes. Keep dependency upgrades and unrelated changes separate. Commit the reviewed preparation and merge it into `main`; repository Git hooks run their configured checks.

## Tag the merged commit

Update the local `main` with a fast-forward pull. Confirm the checkout is clean and its root, application, and package versions match the intended tag. Do not reuse a tag for a different commit. For the example version:

```sh
git switch main
git pull --ff-only origin main
git status --short
pnpm run release:verify --family cortex
git tag -a cortex-v0.3.0-beta.1 -m "Cortex 0.3.0-beta.1"
git push origin refs/tags/cortex-v0.3.0-beta.1
git ls-remote origin refs/tags/cortex-v0.3.0-beta.1 "refs/tags/cortex-v0.3.0-beta.1^{}"
```

The peeled `^{}` result must equal the tested commit on `main`. Push the explicit tag rather than all local tags. Published tags remain fixed; corrections get a new version and tag. Tagging requires neither a GitHub Release nor an EXE build or upload, and this repository has no tag-triggered publishing workflow.

## Choose the next version

Use `0.3.0-beta.2` for the next prerelease, `0.3.0` for the stable release, `0.3.1` for fixes, and `0.4.0` for the next feature release. Pass prerelease versions explicitly to `release:cortex`. Keep version numbers increasing so consumers can order releases consistently.
