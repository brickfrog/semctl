# semctl

[![CI](https://github.com/brickfrog/semctl/actions/workflows/ci.yml/badge.svg)](https://github.com/brickfrog/semctl/actions/workflows/ci.yml)

`sm` is a command-line tool for Power BI and Analysis Services semantic models. Use it to inspect DAX dependencies, preview model edits, and test measures against local fixture data. It works offline with PBIP projects, TMDL, `.bim` files, and Tabular Editor folders on Linux, macOS, and Windows.

## Install

Download the archive for your platform, unpack it, and put `sm` (`sm.exe` on Windows) on your `PATH`. The binaries include the .NET runtime. [Releases](https://github.com/brickfrog/semctl/releases) · [Installation guide](docs/installation.md)

## Try it on a model

From the directory containing your `.pbip` file, list its measures and check what depends on one of them. This example uses the [SpaceParts sample model](https://github.com/TabularEditor/spaceparts-analytics).

```sh
model="Invoiced Sales.pbip"
sm list "$model" -k measure
sm deps "$model" "[Revenue]" --referenced-by
sm rename "$model" "[Revenue]" "Net Revenue"
```

The last command previews the rename and the references it would update. Add `--save` to apply it.

## Documentation

- [CLI guide and command reference](docs/README.md)
- [Testing a model with DAX](docs/model-tests.md)
- [Building and contributing](docs/development.md)

Semctl is pre-1.0. Its source is [MIT licensed](LICENSE). Bundled components have their own [license notices](THIRD-PARTY-NOTICES.md).
