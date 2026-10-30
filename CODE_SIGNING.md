# Code signing policy — WinForge

**Free code signing provided by SignPath.io, certificate by SignPath Foundation.**

This page describes which WinForge artifacts are code signed, how they are built, and who
is allowed to approve a signing request. It applies to every release published at
<https://github.com/Maximiliano-Fallini/WinForge/releases>.

> **Release exceptions — v0.6.6 and v0.6.7:** both releases were published without a digital
> signature. v0.6.6 had no SignPath connection available for its signing request; v0.6.7 was
> built and published from the developer machine at the maintainer's request, outside the
> GitHub Actions workflow that SignPath requires as its trusted build. Their GitHub release
> notes and the README warn users that the installer is unsigned. The signing process below
> remains the policy for subsequent releases unless another exception is explicitly recorded.

## What is signed

| Artifact | Description |
|---|---|
| `WinForge-<version>.msi` | The 64-bit Windows Installer package, the file users download and run. |

The signature is applied with **deep signing**: the installer itself and the
`WinForge.exe` it contains are both signed, so the executable is verified whether it is
inspected before installation or after it has been installed.

Third-party components that WinForge downloads at the user's request (for example
vendor-supplied drivers) are not covered by this subscription and are not signed by it.
They are verified against a published SHA-256 hash instead.

## How the signed artifact is built

The MSI is built by a public, auditable GitHub Actions workflow —
[`.github/workflows/build-installer.yml`](.github/workflows/build-installer.yml) — on
**GitHub-hosted runners** (`windows-latest`), never on a developer machine and never by
uploading a locally produced file.

The job performs, in order:

1. `actions/checkout` of this repository at the commit being released.
2. Installation of the pinned .NET 9 SDK and WiX Toolset 7.0.0.
3. A check that the version number is identical in `src/WHPO.Core/WHPO.Core.csproj`,
   `src/WHPO.UI/WHPO.UI.csproj`, `installer/Product.wxs` and
   `installer/build-installer.ps1`.
4. `dotnet publish` of the application (Release, `win-x64`, Windows App SDK
   self-contained) and the compilation of the MSI from the sources in `installer/`.
5. Publication of the resulting MSI and its SHA-256 checksum as a workflow artifact.

SignPath verifies that the artifact it signs was produced by this workflow in a
GitHub-hosted runner, and signs that exact file. No other path produces a signed
WinForge build.

## Who can request and approve a signing

WinForge is maintained by a single person. The roles SignPath requires are all held by
that maintainer, and every signing request is approved manually:

| Role | Who | Responsibility |
|---|---|---|
| **Author / committer** | Maximiliano Fallini | Writes the code and pushes the commit and tag being released. |
| **Reviewer** | Maximiliano Fallini | Confirms the tagged commit is the intended release, that the workflow run was triggered from this repository, and that the version in the build matches the release version. |
| **Approver** | Maximiliano Fallini | Grants the signing request in SignPath only after the review above. |

Because the project has one maintainer, the review is a documented self-check rather than
an independent code review. What it guarantees is that **no signing request is approved
automatically**: a signature is only issued after a human has confirmed that the artifact
being signed came from a run of the workflow above, on the commit being released.

A signing request is rejected if any of the following is true:

- the run did not come from this repository, or not from a GitHub-hosted runner;
- the commit or tag does not match the release being published;
- the version inside the MSI differs from the version in the repository;
- the artifact was modified after the workflow produced it (the SHA-256 recorded by the
  workflow no longer matches).

## How to verify a download

Windows shows the publisher on the installer's UAC prompt. To check the signature
manually, in PowerShell:

```powershell
Get-AuthenticodeSignature .\WinForge-<version>.msi | Format-List Status, SignerCertificate
```

`Status` must be `Valid` and the signer must be **SignPath Foundation**. The SHA-256 of
the file is published next to the MSI in the release, and can be compared with:

```powershell
Get-FileHash .\WinForge-<version>.msi -Algorithm SHA256
```

## Privacy

Signing does not change what the application collects or sends anywhere: nothing. The
details are in the [privacy policy](PRIVACY.md).

## Contact

Questions about this policy, or a signature that does not verify, can be reported at
<https://github.com/Maximiliano-Fallini/WinForge/issues>.
