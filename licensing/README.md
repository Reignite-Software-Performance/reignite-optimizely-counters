# Reignite NuGet licensing template

Reusable license/notice wiring for every Reignite NuGet package, including
future Sitecore and other-platform packages.

This folder is a **template**. It is not packed into any package. Copy from it;
do not reference it at build time.

## The files

| File | Purpose |
| --- | --- |
| `LICENSE` | Canonical Reignite Source-Available License 1.0. Copy to the repo root as `LICENSE`. |
| `NOTICE.template` | Notice skeleton. Replace the `<PLACEHOLDERS>` and save as the repo-root `NOTICE`. |
| `Directory.Build.props.snippet` | MSBuild properties that make NuGet surface and enforce the license. |

## Why this license, and what it can and cannot do

The license grants free use in development, test, staging, evaluation and
proof-of-concept environments, and **requires an active Reignite plan for any
Production Environment**. "Production Environment" is defined in section 1.1 so
it covers live sites, live traffic, live business data, and paid third-party
offerings.

Two facts to keep in mind:

1. **A license is a contract, not DRM.** NuGet cannot stop someone running a
   package. The enforceable surfaces are the license text, the `NOTICE`, and
   `PackageRequireLicenseAcceptance` (which shows an acceptance dialog in Visual
   Studio — `dotnet restore` does not prompt). Runtime license checks are
   deliberately not included: they would add a failure mode to a diagnostics
   package and are easy to strip anyway.
2. **Prior grants cannot be revoked.** Any release already published under
   Apache-2.0 (or any other license) stays available under that license to
   whoever received it. This license applies to **new** releases only, which is
   why `NOTICE` says so explicitly.

## Applying it to a new package repo

1. Copy `LICENSE` to the repo root.
2. Copy `NOTICE.template` to the repo root as `NOTICE` and fill it in. Remove
   the derivation block if the code is original rather than derived from an
   Apache-2.0 upstream.
3. Add the properties from `Directory.Build.props.snippet` to the repo's
   `Directory.Build.props` (or each `.csproj` if the repo has no props file).
4. Make sure both files are packed into every package:

   ```xml
   <ItemGroup>
     <None Include="$(MSBuildThisFileDirectory)LICENSE" Pack="true" PackagePath="\" Visible="false" />
     <None Include="$(MSBuildThisFileDirectory)NOTICE" Pack="true" PackagePath="\" Visible="false" />
   </ItemGroup>
   ```

   `PackageLicenseFile` must name the file as it appears at the package root
   (`LICENSE`), not its path on disk.
5. Update the README: replace any "free to use" wording with the free
   non-production / paid production split, and link to the license.
6. Update install-time surfaces (`install.ps1`, MSBuild `.targets` messages,
   `README_AFTER_INSTALL.md`) to state the production-plan requirement.
7. Bump the package version. A license change must never reuse a published
   version.
8. Have counsel review the final `LICENSE` and `NOTICE` before publishing.

## Fields that matter for NuGet

| Property | Value | Effect |
| --- | --- | --- |
| `PackageLicenseFile` | `LICENSE` | Puts the license file in the `.nupkg` and shows it on nuget.org. |
| `PackageRequireLicenseAcceptance` | `true` | Visual Studio shows a license acceptance dialog; PackageReference installs record acceptance. |
| `PackageLicenseExpression` | *(remove)* | Mutually exclusive with `PackageLicenseFile`. An SPDX expression cannot express a custom production restriction. |
| `Copyright` | `Copyright (c) Reignite Software Performance. All rights reserved.` | Keeps the reservation visible in package metadata. |

## Checklist before publishing

- [ ] `LICENSE` at repo root and packed, `PackageLicenseFile=LICENSE`
- [ ] `NOTICE` at repo root and packed, with upstream attributions intact
- [ ] `PackageLicenseExpression` removed
- [ ] `PackageRequireLicenseAcceptance=true`
- [ ] Version bumped
- [ ] README, install script, build-target message and after-install doc all
      state the production-plan requirement
- [ ] Existing Apache-2.0 releases called out in `NOTICE`
- [ ] Counsel review
