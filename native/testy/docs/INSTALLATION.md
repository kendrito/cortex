# Installation and release verification

The portable win-x64 package contains the Studio and CLI EXEs, owned fixture apps, documentation and the .NET runtime. Keep the directory together. No administrator rights or separate runtime installation are required for the self-contained package; only the optional background agent asks for administrator approval. The default workspace stays under `%LOCALAPPDATA%\Testy\Workspace`, outside the application installation.

## Local installation

From a trusted, extracted package, run PowerShell:

```powershell
.\install-testy.ps1 -Action Install -PackageDirectory . -AllowUnsigned
```

The default destination is `%LOCALAPPDATA%\Programs\Testy`. A Start menu shortcut points to the installed version. Use `-InstallDirectory` for an explicit dedicated location and `-NoShortcut` when verifying in a temporary directory. Unsigned development packages require the explicit flag. Omit it for a signed production release and pass the independently obtained publisher certificate thumbprint with `-PublisherThumbprint`.

Installation verifies the complete manifest, copies to a separate staging directory, verifies the copy, and activates a version directory. Existing unrelated directories and reparse-point paths are rejected. Close installed Testy processes first. It does not install a background updater, service or hidden scheduler. The optional [background agent](AGENT.md) is installed separately with `Testy.Agent.exe install` (one administrator approval): it copies the sealed bundle under Program Files and registers a sign-in task for your user. Re-run it after updating Studio.

Activation uses a durable transaction record. If it is interrupted, the next managed installer operation reconciles that record: it verifies an already activated version or restores the previous activation and removes only its owned incomplete staging/version directory. Keep the transaction file intact and rerun the installer; do not manually delete it to bypass recovery. An existing installation with a Start menu shortcut rejects `-NoShortcut` during an update or rollback, so use the same shortcut mode throughout its lifecycle.

## Updates, rollback and removal

```powershell
.\install-testy.ps1 -Action Update -PackageDirectory C:\Releases\Testy -AllowUnsigned
.\install-testy.ps1 -Action Rollback -AllowUnsigned
.\install-testy.ps1 -Action Uninstall
```

Update requires a newer version and preserves the prior version for rollback. Rollback verifies the old package again. Uninstall removes the managed application versions and owned shortcut, retaining workspaces and credentials. Back up the workspace before adopting a release with a new data format. Version 0.6 validates and adopts the existing layout as format 1; it does not alter historical evidence bytes.

`-Action Verify -PackageDirectory ...` checks a package without installing it. A SHA256 manifest proves content consistency, not publisher identity. A signed release additionally uses a detached CMS signature over the manifest, validated against Windows trust and the explicitly pinned publisher certificate. Do not obtain the trusted thumbprint from an untrusted downloaded manifest.

## Producing a release

From the source repository:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
.\scripts\seal-package.ps1 -PackageDirectory .\dist\Testy `
    -CertificateThumbprint YOUR_CODE_SIGNING_CERTIFICATE_THUMBPRINT
.\scripts\archive-package.ps1
```

Publishing uses a fresh staging directory and retains the previous bundle under `dist/history`. The default seal has integrity hashes and explicitly declares itself unsigned. Signing requires a current code-signing certificate/private key in the selected user's certificate store and Windows SDK SignTool. The signing script applies SHA256 Authenticode signatures with RFC3161 timestamps to Testy binaries, verifies them, and signs the file manifest. No certificate or private key is bundled. Re-seal after any package change and rebuild the ZIP; modification after sealing fails verification.

Trusted signing, corporate endpoint qualification, enterprise deployment policy and Windows reputation approval require the real organization's certificate and environment. The supplied development build does not claim those approvals. The customer's pilot is outside this delivery's requested scope.

The verification runtime is .NET 9.0.20 with SDK 9.0.318. Microsoft currently lists .NET 9 support through November 10, 2026; a production maintenance plan must move to a supported later runtime before then and requalify WPF behavior. Consult the [official .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) when adopting or republishing a release.

Windows Credential Manager storage follows Microsoft's [CredWriteW API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew). The binary signing process follows [SignTool documentation](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool). Those platform capabilities do not by themselves qualify this application for an enterprise deployment.
