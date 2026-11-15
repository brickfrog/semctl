module Tessera.Tests.InitCommandTests

open System.IO
open Expecto
open Tessera.Tests.Fixtures
open Tessera.Tests.CliHarness
open Tessera.Cli.Common

let private errorCode (r: Run) =
    use doc = json r
    doc.RootElement.GetProperty("error").GetProperty("code").GetString()

[<Tests>]
let tests =
    testList "CLI init" [
        testCase "--help lists init"
        <| fun () -> Expect.stringContains (tess [ "--help" ]).Out "init" ""

        testCase "infers the format from the path, and names the model after it"
        <| fun () ->
            use t = new TempDir()

            for path, format in
                [
                    t.Combine "Sales.SemanticModel", "pbip"
                    t.Combine "Sales.bim", "bim"
                    t.Combine "Sales", "tmdl"
                ] do
                let r = tess [ "init"; path; "--json" ]
                Expect.equal r.Code ExitCode.ok r.Err
                use doc = json r
                let root = doc.RootElement
                Expect.equal (root.GetProperty("output").GetProperty("format").GetString()) format path
                Expect.equal (root.GetProperty("model").GetProperty("name").GetString()) "Sales" path
                Expect.equal (root.GetProperty("model").GetProperty("compatibilityLevel").GetInt32()) 1702 path

                let info = tess [ "info"; path; "--json" ]
                Expect.equal info.Code ExitCode.ok info.Err
                use idoc = json info
                let i = idoc.RootElement
                Expect.equal (i.GetProperty("name").GetString()) "Sales" path
                Expect.equal (i.GetProperty("compatibilityLevel").GetInt32()) 1702 path
                Expect.equal (i.GetProperty("compatibilityMode").GetString()) "PowerBI" path
                Expect.equal (i.GetProperty("culture").GetString()) "en-US" path

        testCase "--format overrides inference"
        <| fun () ->
            use t = new TempDir()
            let r = tess [ "init"; t.Combine "out"; "--format"; "pbip" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.isTrue (File.Exists(t.Combine("out", "definition.pbism"))) ""
            Expect.isTrue (File.Exists(t.Combine("out", "definition", "model.tmdl"))) ""

        testCase "--name, --compatibility-level and --culture"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "x.bim"

            let r =
                tess [
                    "init"
                    path
                    "--name"
                    "Finance"
                    "--compatibility-level"
                    "1600"
                    "--culture"
                    "da-dk"
                ]

            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "model Finance, compatibility level 1600, culture da-DK" ""
            let info = tess [ "info"; path; "--json" ]
            use doc = json info
            Expect.equal (doc.RootElement.GetProperty("name").GetString()) "Finance" ""
            Expect.equal (doc.RootElement.GetProperty("compatibilityLevel").GetInt32()) 1600 ""
            Expect.equal (doc.RootElement.GetProperty("culture").GetString()) "da-DK" ""

        testCase "a new model can be listed, converted and diffed"
        <| fun () ->
            use t = new TempDir()
            let sm = t.Combine "Sales.SemanticModel"
            Expect.equal (tess [ "init"; sm ]).Code ExitCode.ok ""
            Expect.equal (tess [ "list"; sm ]).Code ExitCode.ok ""
            Expect.equal (tess [ "convert"; sm; t.Combine "copy.bim" ]).Code ExitCode.ok ""
            Expect.equal (tess [ "diff"; sm; t.Combine "copy.bim" ]).Code ExitCode.ok "no differences"

        testCase "refuses a non-empty destination unless --force"
        <| fun () ->
            use t = project ()
            let sm = t.Combine "Sales.SemanticModel"
            let r = tess [ "init"; sm; "--json" ]
            Expect.equal r.Code ExitCode.output ""
            Expect.equal (errorCode r) "destination-not-empty" ""
            Expect.equal (tess [ "info"; sm; "--json" ]).Code ExitCode.ok "existing model untouched"

            let forced = tess [ "init"; sm; "--force" ]
            Expect.equal forced.Code ExitCode.ok forced.Err
            let info = tess [ "info"; sm; "--json" ]
            use doc = json info
            Expect.equal (doc.RootElement.GetProperty("counts").GetProperty("table").GetInt32()) 0 "replaced"

        testCase "re-running init on the model it created: exit 0, a JSON reason, nothing written"
        <| fun () ->
            use t = new TempDir()

            for path in
                [
                    t.Combine "Sales.SemanticModel"
                    t.Combine "Sales.bim"
                    t.Combine "Sales"
                    t.Combine "te/database.json"
                ] do
                Expect.equal (tess [ "init"; path; "--culture"; "da-DK" ]).Code ExitCode.ok path

                let stamps () =
                    let root =
                        if Directory.Exists path then
                            path
                        else
                            Path.GetDirectoryName path

                    [
                        for f in Directory.GetFiles(root, "*", SearchOption.AllDirectories) ->
                            f, File.GetLastWriteTimeUtc f, File.ReadAllBytes f
                    ]

                let before = stamps ()
                let r = tess [ "init"; path; "--culture"; "da-DK"; "--json" ]
                Expect.equal r.Code ExitCode.ok (path + r.Out + r.Err)
                use doc = json r
                let root = doc.RootElement
                Expect.isFalse (root.GetProperty("created").GetBoolean()) path
                Expect.stringContains (root.GetProperty("reason").GetString()) "already holds this model" path
                Expect.equal (stamps ()) before $"{path}: nothing written"

                let text = tess [ "init"; path; "--culture"; "da-DK" ]
                Expect.equal text.Code ExitCode.ok text.Err
                Expect.stringContains text.Out "Nothing to do" ""

                // Another model at the destination is still refused.
                for other in
                    [
                        [ "init"; path ]
                        [ "init"; path; "--culture"; "da-DK"; "--name"; "Other" ]
                        [ "init"; path; "--culture"; "da-DK"; "--compatibility-level"; "1601" ]
                    ] do
                    let r = tess (other @ [ "--json" ])
                    Expect.equal r.Code ExitCode.output (String.concat " " other)
                    Expect.equal (errorCode r) "destination-not-empty" ""

                Expect.equal (stamps ()) before $"{path}: still nothing written"

        testCase "a model with more in it, or in another format, is not the same model"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "Sales.bim"
            Expect.equal (tess [ "init"; path ]).Code ExitCode.ok ""

            let added =
                tess [ "add"; path; "table:Numbers"; "-e"; "GENERATESERIES(1, 3)"; "--save" ]

            Expect.equal added.Code ExitCode.ok added.Err
            Expect.equal (tess [ "init"; path ]).Code ExitCode.output "a table was added"

            let dir = t.Combine "Finance"
            Expect.equal (tess [ "init"; dir ]).Code ExitCode.ok ""
            Expect.equal (tess [ "init"; dir; "-f"; "database.json" ]).Code ExitCode.output "TMDL, not database.json"

        testCase "an empty existing folder is fine without --force"
        <| fun () ->
            use t = new TempDir()
            let dir = t.Combine "empty"
            Directory.CreateDirectory dir |> ignore
            Expect.equal (tess [ "init"; dir ]).Code ExitCode.ok ""

        testCase "validates its options"
        <| fun () ->
            use t = new TempDir()

            for bad in
                [
                    [ "--format"; "xml" ]
                    [ "--compatibility-level"; "1500" ]
                    [ "--culture"; "en" ]
                    [ "--culture"; "nope-NOPE" ]
                    [ "--name"; " " ]
                ] do
                let r = tess ([ "init"; t.Combine "o"; "--json" ] @ bad)
                Expect.equal r.Code ExitCode.usage (String.concat " " bad)
                Expect.equal (errorCode r) "usage" (String.concat " " bad)
                Expect.isFalse (Directory.Exists(t.Combine "o")) "nothing written"
    ]
