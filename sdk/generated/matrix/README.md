# Generator matrix artifacts

This directory contains issue #221 generator proof artifacts. Generated raw clients are prototype evidence only:
they are not stable package surfaces and must remain behind handwritten SDK facades.

Regenerate the matrix from the repository root:

```powershell
pwsh ./sdk/scripts/invoke-generator-matrix.ps1
```

The matrix intentionally records tool-version evidence, generator acceptance or rejection, deterministic regeneration
evidence, compile/import probes, transport policy support, and facade-fit notes.

The `openapi-generator/**` subtree is raw third-party generator output. It is covered by path-local Git attributes so
repository whitespace checks do not require hand edits to generated files.
