# Install

Release binaries are self-contained single files and need no .NET install. Download the archive for your platform
from the [releases page](https://github.com/brickfrog/semctl/releases) (`linux-x64`, `linux-arm64`, `win-x64`,
`osx-arm64`, `osx-x64`), unpack it, and put `sm` (`sm.exe` on Windows) on your `PATH`:

```sh
tar -xzf sm-<version>-linux-x64.tar.gz && install sm-<version>-linux-x64/sm ~/.local/bin/
```

The Linux binaries are built for glibc-based distributions and need ICU (`libicu`, e.g. `apt install libicu74`),
because sm uses real culture data rather than invariant globalization. There is no musl/Alpine build yet.
The macOS binaries are not notarized. If Gatekeeper blocks one, run `xattr -d com.apple.quarantine sm`.
Each release also lists `SHA256SUMS`.

With the .NET 10 SDK, you can install it as a .NET tool instead. Download `semctl.<version>.nupkg` from the release, then:

```sh
dotnet tool install -g semctl --add-source ./folder-with-the-nupkg
```

**For AI agents:** [SKILL.md](../SKILL.md) tells an agent how to drive sm (JSON output, exit codes, dry runs and
`--save`, no network). It ships next to the binary in every release archive, and in the .NET tool package (at its
root and next to `sm.dll`).
