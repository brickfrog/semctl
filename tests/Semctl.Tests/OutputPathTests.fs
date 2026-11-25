/// `--format bim` to a path without .bim: init, convert and data embed add the extension, so sm
/// can read what it wrote (it tells a .bim file by its extension).
module Semctl.Tests.OutputPathTests

open System.IO
open Expecto
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli.Common

let private outputPath (r: Run) =
    use doc = json r
    doc.RootElement.GetProperty("output").GetProperty("path").GetString()

[<Tests>]
let tests =
    testList "--format bim output paths" [
        test "destinationFor adds .bim, keeps one in any case, and refuses a folder" {
            use t = new TempDir()
            let bim = ModelIO.BimFile
            Expect.equal (ModelIO.OutputFormat.destinationFor bim (t.Combine "m")) (Ok(t.Combine "m.bim")) "added"
            Expect.equal (ModelIO.OutputFormat.destinationFor bim (t.Combine "m.BIM")) (Ok(t.Combine "m.BIM")) "kept"

            Expect.equal
                (ModelIO.OutputFormat.destinationFor bim (t.Combine "m.json"))
                (Ok(t.Combine "m.json.bim"))
                "another extension"

            Expect.isError (ModelIO.OutputFormat.destinationFor bim t.Path) "an existing folder"
            Expect.isError (ModelIO.OutputFormat.destinationFor bim (t.Combine "new/")) "a trailing separator"

            for f in [ ModelIO.TmdlFolder; ModelIO.PbipFolder; ModelIO.DatabaseJsonFolder ] do
                Expect.equal (ModelIO.OutputFormat.destinationFor f (t.Combine "m")) (Ok(t.Combine "m")) "folders"
        }

        test "init writes <path>.bim, which sm reads back, and re-running it is a no-op" {
            use t = new TempDir()
            let r = semctl [ "init"; t.Combine "m"; "--format"; "bim"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal (outputPath r) (t.Combine "m.bim") r.Out
            Expect.isFalse (File.Exists(t.Combine "m")) "no file without the extension"

            use doc = json r
            Expect.equal (doc.RootElement.GetProperty("model").GetProperty("name").GetString()) "m" "named after it"

            let v = semctl [ "validate"; t.Combine "m.bim" ]
            Expect.equal v.Code ExitCode.ok (v.Out + v.Err)

            let again = semctl [ "init"; t.Combine "m"; "--format"; "bim" ]
            Expect.equal again.Code ExitCode.ok again.Err
            Expect.stringContains again.Out "Nothing to do" again.Out
        }

        test "init --format bim to a folder is a usage error" {
            use t = new TempDir()
            Directory.CreateDirectory(t.Combine "dir") |> ignore
            let r = semctl [ "init"; t.Combine "dir"; "--format"; "bim" ]
            Expect.equal r.Code ExitCode.usage r.Err
            Expect.stringContains r.Err "is a folder" r.Err
        }

        test "convert and data embed add .bim too" {
            use t = new TempDir()
            let model = t.Combine "m.bim"
            Expect.equal (semctl [ "init"; model ]).Code ExitCode.ok "init"

            let c = semctl [ "convert"; model; t.Combine "out"; "--format"; "bim"; "--json" ]
            Expect.equal c.Code ExitCode.ok c.Err
            Expect.equal (outputPath c) (t.Combine "out.bim") c.Out
            Expect.equal (semctl [ "validate"; t.Combine "out.bim" ]).Code ExitCode.ok "convert's output reads"

            Directory.CreateDirectory(t.Combine "fixture") |> ignore

            let e =
                semctl [
                    "data"
                    "embed"
                    model
                    t.Combine "fixture"
                    t.Combine "embedded"
                    "--format"
                    "bim"
                ]

            Expect.equal e.Code ExitCode.ok (e.Out + e.Err)
            Expect.isTrue (File.Exists(t.Combine "embedded.bim")) e.Out
            Expect.equal (semctl [ "validate"; t.Combine "embedded.bim" ]).Code ExitCode.ok "embed's output reads"
        }

        test "the overwrite check applies to the path with .bim" {
            use t = new TempDir()
            Expect.equal (semctl [ "init"; t.Combine "m"; "--format"; "bim" ]).Code ExitCode.ok "first"

            let other = semctl [ "init"; t.Combine "m"; "--format"; "bim"; "--name"; "Other" ]

            Expect.equal other.Code ExitCode.output other.Err

            let forced =
                semctl [ "init"; t.Combine "m"; "--format"; "bim"; "--name"; "Other"; "--force" ]

            Expect.equal forced.Code ExitCode.ok forced.Err
        }
    ]
