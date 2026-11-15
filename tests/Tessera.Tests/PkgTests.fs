/// `tess pkg`: DAX packages installed the way Tabular Editor 3's DAX Package Manager installs them.
/// Every source here is local (folders and zips in a temp dir, the vendored daxlib package in
/// pkg/daxlib) or a fake of daxlib.org's API: no test touches the network.
module Tessera.Tests.PkgTests

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Tests.Fixtures
open Tessera.Tests.CliHarness

module ExitCode = Tessera.Cli.Common.ExitCode
module PkgCommand = Tessera.Cli.PkgCommand

// ---- fixtures ----------------------------------------------------------------------------------

/// TimeSeries.MovingAverage 0.1.1, copied unmodified from github.com/daxlib/daxlib (MIT; NOTICE.md).
let private vendored = Path.Combine(__SOURCE_DIRECTORY__, "pkg", "daxlib")

/// What TE3 recorded when it installed TimeSeries.MovingAverage 0.1.1 into spaceparts-analytics
/// (Invoiced Sales.SemanticModel/definition/functions.tmdl, commit a74c5833): function, content hash.
let private spacepartsHashes = [
    "TimeSeries.MovingAverage.Simple", "21AF551C2CCE977F02F27303DDC7794E8EB37870774CB204C15E1A7BC8BB1CD3"
    "TimeSeries.MovingAverage.Weighted", "32F2040E570735BF4C4CA16B0A77BF96C1A3924CD8F8B75663850AEBBB1332CC"
    "TimeSeries.MovingAverage.LinearWeighted", "82D7816188A9C72A3E1590D70A9656A67F3A05BECBF9819A224930C65EC7E2E5"
    "TimeSeries.MovingAverage.Exponential", "2D41B4908432500507F4E95DAA87F153AB839CAEB8266B4EF6565E28502C9A36"
    "TimeSeries.MovingAverage.DoubleExponential", "A77BE391A2DD151A73F7361B94B96AEBB50B928DFDA0EC51A39DE2A4FD093913"
    "TimeSeries.MovingAverage.Triangular", "E4CF42C5E1C11C37FE6B4A56F35CC1AC6C088F483B9A0173132E9B7CAF5F0335"
    "TimeSeries.MovingAverage.Geometric", "BCADA2DC39BE3A30AFBB9B633E26778CAD9C3D4455164CFCB3D08749BCB15A0A"
]

/// spaceparts' TabularEditor_ModelDaxPkgTable value, as TOM reads it from the TMDL TE3 saved.
let private spacepartsTable =
    String.concat "\n" [
        "{"
        "  \"1\": {"
        "    \"Provider\": \"https://api.daxlib.org\","
        "    \"PackageId\": \"DaxLib.FormatString\","
        "    \"Version\": \"0.1.2\""
        "  },"
        "  \"2\": {"
        "    \"Provider\": \"https://api.daxlib.org\","
        "    \"PackageId\": \"TimeSeries.MovingAverage\","
        "    \"Version\": \"0.1.1\""
        "  }"
        "}"
    ]

let private manifestJson (id: string) (version: string) (extra: string) =
    $"{{\"id\": \"{id}\", \"version\": \"{version}\", \"authors\": \"A, B\", \"description\": \"The {id} package\", \"tags\": \"DAX,TEST\"{extra}}}"

/// A TMDL document of functions `name = expression` (single-line expressions).
let private functionsTmdl (functions: (string * string) list) =
    let body (expression: string) =
        match expression.Split '\n' with
        | [| one |] -> " " + one
        | lines -> lines |> Array.map (fun l -> "\n\t\t" + l) |> String.concat ""

    functions
    |> List.map (fun (name, expression) -> $"/// {name} does things\nfunction '{name}' ={body expression}\n")
    |> String.concat "\n"

/// Write a package folder under `root`, daxlib-checkout style: <root>/<id>/<version>/.
let private writePackage (root: string) (id: string) (version: string) (functions: (string * string) list) =
    let dir = Path.Combine(root, id.ToLowerInvariant(), version)
    write (Path.Combine(dir, "manifest.daxlib")) (manifestJson id version "")
    write (Path.Combine(dir, "lib", "functions.tmdl")) (functionsTmdl functions)
    dir

/// A .daxpkg's bytes.
let private zipBytes (entries: (string * string) list) =
    use ms = new MemoryStream()

    do
        use zip = new ZipArchive(ms, ZipArchiveMode.Create, true)

        for name, text in entries do
            let e = zip.CreateEntry name
            use s = e.Open()
            let bytes = Text.Encoding.UTF8.GetBytes text
            s.Write(bytes, 0, bytes.Length)

    ms.ToArray()

let private ok r =
    match r with
    | Ok x -> x
    | Error e -> failtestf "unexpected error: %s" (TessError.message e)

let private err r =
    match r with
    | Ok _ -> failtest "expected an error"
    | Error e -> e

let private packageOf (id: string) (version: string) (functions: (string * string) list) : Pkg.Package = {
    Manifest =
        ok (
            Pkg.parseManifest (manifestJson id version "")
            |> Result.mapError (fun r -> InvalidPackage("test", r))
        )
    Functions =
        ok (
            Pkg.parseFunctions (functionsTmdl functions)
            |> Result.mapError (fun r -> InvalidPackage("test", r))
        )
    Origin = "test"
}

let private installOpts: Pkg.InstallOptions = {
    OnConflict = Pkg.RefuseConflicts
    IgnoreDependencies = false
    Provider = Pkg.daxlibProvider
}

let private emptyModel () =
    let db = new Database(Name = "M", ID = "M", CompatibilityLevel = 1702)
    db.Model <- new Model(Name = "Model")
    db

let private ext (props: seq<ExtendedProperty>) (name: string) =
    props
    |> Seq.tryFind (fun p -> p.Name = name)
    |> Option.map (function
        | :? StringExtendedProperty as s -> s.Value
        | :? JsonExtendedProperty as j -> j.Value
        | _ -> "")

let private modelExt (db: Database) name =
    ext (Seq.cast<ExtendedProperty> db.Model.ExtendedProperties) name

let private fnExt (f: Function) name =
    ext (Seq.cast<ExtendedProperty> f.ExtendedProperties) name

let private vendoredSource () = ok (PkgSource.local vendored)

let private timeSeries () =
    ok ((vendoredSource ()).Get "TimeSeries.MovingAverage" "0.1.1")

// ---- tests -------------------------------------------------------------------------------------

[<Tests>]
let versionTests =
    testList "pkg versions" [
        test "SemVer precedence, pre-releases before their release" {
            let ordered = [
                "0.1.0-alpha"
                "0.1.0-alpha.1"
                "0.1.0-beta"
                "0.1.0"
                "0.1.2"
                "0.10.0"
                "1.0.0"
            ]

            let shuffled = [
                "0.10.0"
                "0.1.0"
                "1.0.0"
                "0.1.0-beta"
                "0.1.2"
                "0.1.0-alpha.1"
                "0.1.0-alpha"
            ]

            Expect.equal (shuffled |> List.sortWith Pkg.Version.compareStrings) ordered ""
        }

        test "latest is the highest stable version unless pre-releases are wanted" {
            let vs = [ "0.1.0-beta"; "0.1.1"; "0.2.0-beta" ]
            Expect.equal (Pkg.Version.latest false vs) (Some "0.1.1") ""
            Expect.equal (Pkg.Version.latest true vs) (Some "0.2.0-beta") ""
            Expect.equal (Pkg.Version.latest false [ "0.1.0-beta" ]) None "only pre-releases: no stable latest"
        }

        testProperty "compare is antisymmetric"
        <| fun (a: uint16, b: uint16, c: uint16, d: uint16) ->
            let x = $"{a % 5us}.{b % 5us}.0"
            let y = $"{c % 5us}.{d % 5us}.0-beta"
            sign (Pkg.Version.compareStrings x y) = -(sign (Pkg.Version.compareStrings y x))
    ]

[<Tests>]
let formatTests =
    testList "pkg format" [
        test "a manifest: comma-separated authors and tags, dependencies" {
            let m =
                Pkg.parseManifest (
                    manifestJson
                        "DaxLib.Sample"
                        "0.1.8"
                        ", \"dependencies\": [{\"id\": \"DaxLib.Records\", \"version\": \"0.9.0-beta\"}]"
                )
                |> Result.mapError (fun r -> InvalidPackage("t", r))
                |> ok

            Expect.equal m.Authors [ "A"; "B" ] ""
            Expect.equal m.Tags [ "DAX"; "TEST" ] ""

            Expect.equal
                m.Dependencies
                [
                    {
                        Id = "DaxLib.Records"
                        Version = "0.9.0-beta"
                    }
                ]
                ""
        }

        test "a manifest without an id or with a bad id or version is refused" {
            for json in
                [
                    "{\"version\": \"1.0.0\"}"
                    manifestJson "has space" "1.0.0" ""
                    manifestJson "Ok.Id" "1.0" ""
                    "[1]"
                    "{not json"
                ] do
                Expect.isError (Pkg.parseManifest json) json
        }

        test "functions.tmdl may hold only functions" {
            Expect.isError (Pkg.parseFunctions "table T\n\tcolumn C\n\t\tdataType: string\n") "a table"
            Expect.isOk (Pkg.parseFunctions (functionsTmdl [ "A.B", "() => 1" ])) "a function"
        }

        test "a CRLF functions.tmdl reads the same expressions" {
            let tmdl = functionsTmdl [ "A.B", "() => 1"; "A.C", "(x: INT64) => x + 1" ]

            let lf =
                Pkg.parseFunctions tmdl
                |> Result.mapError (fun r -> InvalidPackage("t", r))
                |> ok

            let crlf =
                Pkg.parseFunctions (tmdl.Replace("\n", "\r\n"))
                |> Result.mapError (fun r -> InvalidPackage("t", r))
                |> ok

            Expect.equal
                (crlf |> List.map (fun f -> f.Name, f.Expression))
                (lf |> List.map (fun f -> f.Name, f.Expression))
                ""

            Expect.isFalse (crlf |> List.exists (fun f -> f.Expression.Contains '\r')) "no CR in expressions"
        }

        test "a .daxpkg zip is read in memory" {
            let bytes =
                zipBytes [
                    "manifest.daxlib", manifestJson "Z.Pkg" "1.0.0" ""
                    "lib/functions.tmdl", functionsTmdl [ "Z.Pkg.One", "() => 1" ]
                    "icon.png", "not really"
                ]

            let p = ok (Pkg.readZip "z.daxpkg" bytes)
            Expect.equal p.Manifest.Id "Z.Pkg" ""
            Expect.equal (p.Functions |> List.map _.Name) [ "Z.Pkg.One" ] ""
        }

        test "a zip without a manifest, or not a zip, is an invalid package" {
            let noManifest =
                zipBytes [ "lib/functions.tmdl", functionsTmdl [ "A.B", "() => 1" ] ]

            match err (Pkg.readZip "x" noManifest) with
            | InvalidPackage(_, reason) -> Expect.stringContains reason "manifest" ""
            | e -> failtestf "%A" e

            match err (Pkg.readZip "x" (Text.Encoding.UTF8.GetBytes "hello")) with
            | InvalidPackage _ -> ()
            | e -> failtestf "%A" e
        }

        test "the package table JSON is written as TE3 writes it" {
            let node = Nodes.JsonNode.Parse(spacepartsTable).AsObject()
            Expect.equal (Pkg.tableJson node) spacepartsTable "byte for byte"
        }
    ]

[<Tests>]
let installTests =
    testList "pkg install, update, remove" [
        test "installing the vendored package records exactly what TE3 recorded in spaceparts" {
            let db = emptyModel ()

            // spaceparts installed DaxLib.FormatString first (key 1); a stand-in takes that key here.
            ok (
                Pkg.install
                    db
                    (packageOf "DaxLib.FormatString" "0.1.2" [ "DaxLib.FormatString.X", "() => 1" ])
                    installOpts
            )
            |> ignore

            ok (Pkg.install db (timeSeries ()) installOpts) |> ignore

            for name, hash in spacepartsHashes do
                let f = db.Model.Functions.Find name
                Expect.isNotNull f name
                Expect.equal (fnExt f Pkg.hashProperty) (Some hash) $"{name} content hash"
                Expect.equal (fnExt f Pkg.handleProperty) (Some "2") $"{name} handle"

            Expect.equal (modelExt db Pkg.tableProperty) (Some spacepartsTable) "package table"
            Expect.equal (modelExt db Pkg.seqProperty) (Some "3") "sequence"

            Expect.isTrue
                (db.Model.ExtendedProperties.Find Pkg.tableProperty :? JsonExtendedProperty)
                "the table is a JSON extended property"

            Expect.isTrue
                (db.Model.ExtendedProperties.Find Pkg.seqProperty :? StringExtendedProperty)
                "the sequence is a string extended property"
        }

        test "it survives a TMDL round trip as TE3 reads it" {
            let db = emptyModel ()
            ok (Pkg.install db (timeSeries ()) installOpts) |> ignore
            use t = new TempDir()
            TmdlSerializer.SerializeDatabaseToFolder(db, t.Path)
            let back = TmdlSerializer.DeserializeDatabaseFromFolder t.Path
            let st = ok (Pkg.state back)

            Expect.equal
                (st.Installed |> List.map (fun i -> i.PackageId, i.Version, i.Key))
                [ "TimeSeries.MovingAverage", "0.1.1", 1 ]
                ""

            Expect.all st.Installed.Head.Functions (fun f -> f.State = Pkg.Unchanged) "unchanged after the round trip"
        }

        test "a changed expression is detected; a line-ending change isn't one" {
            let db = emptyModel ()

            ok (
                Pkg.install
                    db
                    (packageOf "P.Q" "1.0.0" [ "P.Q.A", "(x: INT64) =>\n\tx + 1"; "P.Q.B", "() => 2" ])
                    installOpts
            )
            |> ignore

            let a = db.Model.Functions.Find "P.Q.A"
            a.Expression <- a.Expression.Replace("\n", "\r\n")
            db.Model.Functions.Find("P.Q.B").Expression <- "() => 3"
            let st = ok (Pkg.state db)

            Expect.equal
                (st.Installed.Head.Functions |> List.map (fun f -> f.Name, f.State))
                [ "P.Q.A", Pkg.Unchanged; "P.Q.B", Pkg.Modified ]
                ""
        }

        test "installing twice is refused; use update" {
            let db = emptyModel ()
            let p = packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1" ]
            ok (Pkg.install db p installOpts) |> ignore

            match err (Pkg.install db p installOpts) with
            | PackageConflict(id, reason, _) ->
                Expect.equal id "P.Q" ""
                Expect.stringContains reason "update" ""
            | e -> failtestf "%A" e
        }

        test "a function name already in the model: refused, overwritten or skipped" {
            let p = packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1"; "P.Q.B", "() => 2" ]

            let withOwn () =
                let db = emptyModel ()
                db.Model.Functions.Add(new Function(Name = "P.Q.A", Expression = "() => 99", LineageTag = "keep-me"))
                db

            match err (Pkg.install (withOwn ()) p installOpts) with
            | PackageConflict(_, _, names) -> Expect.equal names [ "P.Q.A" ] ""
            | e -> failtestf "%A" e

            let over = withOwn ()

            ok (
                Pkg.install over p {
                    installOpts with
                        OnConflict = Pkg.OverwriteConflicts
                }
            )
            |> ignore

            let a = over.Model.Functions.Find "P.Q.A"
            Expect.equal a.Expression "() => 1" "overwritten"
            Expect.equal a.LineageTag "keep-me" "the model's lineage tag is kept"
            Expect.equal (fnExt a Pkg.handleProperty) (Some "1") "now part of the package"

            let skip = withOwn ()

            let effect =
                ok (
                    Pkg.install skip p {
                        installOpts with
                            OnConflict = Pkg.SkipConflicts
                    }
                )

            let a = skip.Model.Functions.Find "P.Q.A"
            Expect.equal a.Expression "() => 99" "kept"
            Expect.isNone (fnExt a Pkg.handleProperty) "not part of the package"
            Expect.isNonEmpty effect.Warnings "says what it kept"
        }

        test "missing dependencies are refused unless ignored" {
            let p = {
                packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1" ] with
                    Manifest.Dependencies = [ { Id = "Other.Lib"; Version = "1.0.0" } ]
            }

            match err (Pkg.install (emptyModel ()) p installOpts) with
            | PackageConflict(_, _, details) -> Expect.equal details [ "Other.Lib 1.0.0" ] ""
            | e -> failtestf "%A" e

            let effect =
                ok (
                    Pkg.install (emptyModel ()) p {
                        installOpts with
                            IgnoreDependencies = true
                    }
                )

            Expect.isNonEmpty effect.Warnings "warns"
        }

        test "a model below compatibility level 1702 is refused, not upgraded" {
            let db = emptyModel ()
            db.CompatibilityLevel <- 1600

            match err (Pkg.install db (timeSeries ()) installOpts) with
            | CannotAdd(_, reason) -> Expect.stringContains reason "1702" ""
            | e -> failtestf "%A" e

            Expect.equal db.CompatibilityLevel 1600 ""
        }

        test "a package can't set its own tracking properties" {
            let tmdl =
                "function 'P.Q.A' = () => 1\n\n\textendedProperty TabularEditor_ObjDaxPkgHandle = 7\n\n\textendedProperty TabularEditor_ObjDaxPkgContentHash = 00\n"

            let p = {
                packageOf "P.Q" "1.0.0" [] with
                    Functions = ok (Pkg.parseFunctions tmdl |> Result.mapError (fun r -> InvalidPackage("t", r)))
            }

            let db = emptyModel ()
            ok (Pkg.install db p installOpts) |> ignore
            let a = db.Model.Functions.Find "P.Q.A"
            Expect.equal (fnExt a Pkg.handleProperty) (Some "1") "tess's key"
            Expect.equal (fnExt a Pkg.hashProperty) (Some(Pkg.contentHash "() => 1")) "tess's hash"
        }

        test "new keys pass the sequence, the table and every function's key" {
            let db = emptyModel ()
            db.Model.ExtendedProperties.Add(StringExtendedProperty(Name = Pkg.seqProperty, Value = "2"))
            let stray = new Function(Name = "Stray", Expression = "() => 0")
            stray.ExtendedProperties.Add(StringExtendedProperty(Name = Pkg.handleProperty, Value = "5"))
            db.Model.Functions.Add stray

            ok (Pkg.install db (packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1" ]) installOpts)
            |> ignore

            Expect.equal (fnExt (db.Model.Functions.Find "P.Q.A") Pkg.handleProperty) (Some "6") ""
            Expect.equal (modelExt db Pkg.seqProperty) (Some "7") ""
            Expect.equal (ok (Pkg.state db)).Orphans [ "Stray", "5" ] "a key the table lacks is reported"
        }

        test "update: same key, new version; functions updated, added and removed" {
            let db = emptyModel ()

            ok (Pkg.install db (packageOf "A.Lib" "1.0.0" [ "Other.F", "() => 0" ]) installOpts)
            |> ignore

            ok (Pkg.install db (packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1"; "P.Q.Gone", "() => 2" ]) installOpts)
            |> ignore

            let v2 = packageOf "P.Q" "2.0.0" [ "P.Q.A", "() => 10"; "P.Q.New", "() => 3" ]
            let effect = ok (Pkg.update db v2 Pkg.RefuseModified Pkg.RefuseConflicts false)
            Expect.stringContains (List.head effect.Notes) "1.0.0 -> 2.0.0" ""
            Expect.isNull (db.Model.Functions.Find "P.Q.Gone") "removed"
            Expect.equal (db.Model.Functions.Find "P.Q.A").Expression "() => 10" "updated"
            let st = ok (Pkg.state db)
            let q = st.Installed |> List.find (fun i -> i.PackageId = "P.Q")
            Expect.equal (q.Key, q.Version) (2, "2.0.0") "key kept, version recorded"
            Expect.equal (q.Functions |> List.map _.Name |> List.sort) [ "P.Q.A"; "P.Q.New" ] ""
            Expect.all q.Functions (fun f -> f.State = Pkg.Unchanged) "hashes updated"
            Expect.equal (modelExt db Pkg.seqProperty) (Some "3") "update doesn't advance the sequence"
        }

        test "update with changed functions: refused, discarded or kept" {
            let setup () =
                let db = emptyModel ()

                ok (Pkg.install db (packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1"; "P.Q.B", "() => 2" ]) installOpts)
                |> ignore

                db.Model.Functions.Find("P.Q.A").Expression <- "() => 100"
                db

            let v2 = packageOf "P.Q" "1.1.0" [ "P.Q.A", "() => 11"; "P.Q.B", "() => 22" ]

            match err (Pkg.update (setup ()) v2 Pkg.RefuseModified Pkg.RefuseConflicts false) with
            | PackageConflict(_, _, names) -> Expect.equal names [ "P.Q.A" ] ""
            | e -> failtestf "%A" e

            let discarded = setup ()

            ok (Pkg.update discarded v2 Pkg.DiscardModified Pkg.RefuseConflicts false)
            |> ignore

            Expect.equal (discarded.Model.Functions.Find "P.Q.A").Expression "() => 11" ""

            let kept = setup ()
            ok (Pkg.update kept v2 Pkg.KeepModified Pkg.RefuseConflicts false) |> ignore
            Expect.equal (kept.Model.Functions.Find "P.Q.A").Expression "() => 100" "your change stays"
            Expect.equal (kept.Model.Functions.Find "P.Q.B").Expression "() => 22" "the rest update"
            let st = ok (Pkg.state kept)

            Expect.equal
                (st.Installed.Head.Functions |> List.find (fun f -> f.Name = "P.Q.A")).State
                Pkg.Modified
                "still shown as changed"
        }

        test "remove: its functions and its table entry go; others stay" {
            let db = emptyModel ()
            ok (Pkg.install db (timeSeries ()) installOpts) |> ignore

            ok (Pkg.install db (packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1" ]) installOpts)
            |> ignore

            ok (Pkg.remove db "timeseries.movingaverage" Pkg.RefuseModified false) |> ignore
            Expect.equal [ for f in db.Model.Functions -> f.Name ] [ "P.Q.A" ] ""
            let st = ok (Pkg.state db)
            Expect.equal (st.Installed |> List.map _.PackageId) [ "P.Q" ] ""
            Expect.equal (modelExt db Pkg.seqProperty) (Some "3") "the sequence stays"

            match err (Pkg.remove db "Nope" Pkg.RefuseModified false) with
            | PackageNotInstalled(_, installed) -> Expect.equal installed [ "P.Q" ] ""
            | e -> failtestf "%A" e
        }

        test "remove with changed functions: refused, or kept unlinked" {
            let setup () =
                let db = emptyModel ()

                ok (Pkg.install db (packageOf "P.Q" "1.0.0" [ "P.Q.A", "() => 1"; "P.Q.B", "() => 2" ]) installOpts)
                |> ignore

                db.Model.Functions.Find("P.Q.A").Expression <- "() => 100"
                db

            Expect.isError (Pkg.remove (setup ()) "P.Q" Pkg.RefuseModified false) ""
            let kept = setup ()
            ok (Pkg.remove kept "P.Q" Pkg.KeepModified false) |> ignore
            let a = kept.Model.Functions.Find "P.Q.A"
            Expect.isNotNull a "kept"
            Expect.isNone (fnExt a Pkg.handleProperty) "unlinked"
            Expect.isNull (kept.Model.Functions.Find "P.Q.B") "the unchanged one goes"
            Expect.isEmpty (ok (Pkg.state kept)).Orphans ""
        }

        test "unreadable package metadata is an error, not a guess" {
            let db = emptyModel ()
            db.Model.ExtendedProperties.Add(JsonExtendedProperty(Name = Pkg.tableProperty, Value = "{\"x\": {}}"))

            match err (Pkg.state db) with
            | InvalidPackageMetadata _ -> ()
            | e -> failtestf "%A" e
        }
    ]

/// A fake daxlib.org: answers from a map of URL -> body, records every URL asked for.
type private FakeDaxlib(answers: Map<string, byte array>) =
    let asked = Collections.Generic.List<string>()
    member _.Asked = List.ofSeq asked

    member _.Get(url: Uri) : Result<byte array option, string> =
        asked.Add url.AbsoluteUri
        Ok(Map.tryFind url.AbsoluteUri answers)

[<Tests>]
let sourceTests =
    testList "pkg sources" [
        test "a folder of packages, a daxlib-style tree, and .daxpkg files" {
            use t = new TempDir()

            writePackage (t.Combine "packages") "P.Q" "1.0.0" [ "P.Q.A", "() => 1" ]
            |> ignore

            writePackage (t.Combine "packages") "P.Q" "1.1.0-beta" [ "P.Q.A", "() => 2" ]
            |> ignore

            writePackage (t.Combine "packages") "Other.Stuff" "0.1.0" [ "Other.Stuff.X", "() => 1" ]
            |> ignore

            File.WriteAllBytes(
                t.Combine "z.daxpkg",
                zipBytes [
                    "manifest.daxlib", manifestJson "Zip.Lib" "2.0.0" ""
                    "lib/functions.tmdl", functionsTmdl [ "Zip.Lib.F", "() => 1" ]
                ]
            )

            // Hidden folders aren't searched; unreadable packages are skipped, and said so.
            writePackage (t.Combine ".git") "Hidden.Pkg" "1.0.0" [ "Hidden.Pkg.F", "() => 1" ]
            |> ignore

            write (t.Combine("bad", "manifest.daxlib")) "{"
            let src = ok (PkgSource.local t.Path)

            Expect.equal
                (ok (src.Search "" false) |> List.map (fun s -> s.Id, s.Version))
                [ "Other.Stuff", "0.1.0"; "P.Q", "1.0.0"; "Zip.Lib", "2.0.0" ]
                "latest stable of each"

            Expect.equal (ok (src.Search "" true) |> List.find (fun s -> s.Id = "P.Q")).Version "1.1.0-beta" ""
            Expect.equal (ok (src.Search "zip" false) |> List.map _.Id) [ "Zip.Lib" ] "by id"
            Expect.equal (ok (src.Search "the other" false) |> List.map _.Id) [ "Other.Stuff" ] "by description"
            Expect.equal (ok (src.Versions "p.q")) [ "1.1.0-beta"; "1.0.0" ] "newest first, any case"
            Expect.equal (ok (src.Get "Zip.Lib" "2.0.0")).Functions.Head.Name "Zip.Lib.F" ""
            Expect.equal (src.Skipped |> List.length) 1 "the bad manifest"

            match err (src.Get "P.Qx" "1.0.0") with
            | PackageNotFound(_, _, suggestions) -> Expect.contains suggestions "P.Q" "did you mean"
            | e -> failtestf "%A" e

            match err (src.Get "P.Q" "9.9.9") with
            | PackageVersionNotFound(_, _, available) -> Expect.equal available [ "1.1.0-beta"; "1.0.0" ] ""
            | e -> failtestf "%A" e
        }

        test "a zip of a daxlib checkout: every package folder in it" {
            use t = new TempDir()
            let zip = t.Combine "daxlib-main.zip"

            File.WriteAllBytes(
                zip,
                zipBytes [
                    "daxlib-main/README.md", "hi"
                    "daxlib-main/packages/a/a.one/1.0.0/manifest.daxlib", manifestJson "A.One" "1.0.0" ""
                    "daxlib-main/packages/a/a.one/1.0.0/lib/functions.tmdl", functionsTmdl [ "A.One.F", "() => 1" ]
                    "daxlib-main/packages/a/a.one/1.1.0/manifest.daxlib", manifestJson "A.One" "1.1.0" ""
                    "daxlib-main/packages/a/a.one/1.1.0/lib/functions.tmdl", functionsTmdl [ "A.One.F", "() => 2" ]
                    "daxlib-main/packages/b/b.two/0.1.0/manifest.daxlib", manifestJson "B.Two" "0.1.0" ""
                    "daxlib-main/packages/b/b.two/0.1.0/lib/functions.tmdl", functionsTmdl [ "B.Two.G", "() => 3" ]
                    "daxlib-main/.hidden/manifest.daxlib", manifestJson "Hidden.X" "1.0.0" ""
                ]
            )

            let src = ok (PkgSource.local zip)

            Expect.equal
                (ok (src.Search "" false) |> List.map (fun s -> s.Id, s.Version))
                [ "A.One", "1.1.0"; "B.Two", "0.1.0" ]
                ""

            let p = ok (src.Get "A.One" "1.0.0")
            Expect.equal p.Functions.Head.Expression "() => 1" ""
            Expect.stringContains p.Origin "!/daxlib-main/packages/a/a.one/1.0.0" "names the folder in the archive"
        }

        test "one package folder, one .daxpkg file, nothing there" {
            use t = new TempDir()
            let dir = writePackage t.Path "P.Q" "1.0.0" [ "P.Q.A", "() => 1" ]
            Expect.equal (ok ((ok (PkgSource.local dir)).Versions "P.Q")) [ "1.0.0" ] "a package folder"

            Expect.isError (PkgSource.local (t.Combine "missing")) "a missing path"
            Directory.CreateDirectory(t.Combine "empty") |> ignore

            match err (PkgSource.local (t.Combine "empty")) with
            | PackageSourceUnreadable _ -> ()
            | e -> failtestf "%A" e
        }

        test "the source: --from, then TESS_PKG_SOURCE; daxlib only when named, never offline" {
            let get (_: Uri) : Result<byte array option, string> = failwith "no network in tests"

            match err (PkgSource.choose None None false get) with
            | NoPackageSource _ -> ()
            | e -> failtestf "%A" e

            Expect.isTrue (ok (PkgSource.choose (Some "daxlib") None false get)).IsOnline "--from daxlib"
            Expect.isTrue (ok (PkgSource.choose None (Some "DAXLIB") false get)).IsOnline "the variable"
            Expect.isFalse (ok (PkgSource.choose (Some vendored) (Some "daxlib") false get)).IsOnline "--from wins"
            Expect.isError (PkgSource.choose (Some "daxlib") None true get) "--offline refuses"
            Expect.isError (PkgSource.choose None (Some "daxlib") true get) "--offline refuses the variable too"

            let here = PkgSource.chooseWith (fun s -> s = "daxlib")

            match err (here (Some "daxlib") None false get) with
            | NoPackageSource reason -> Expect.stringContains reason "./daxlib" "a folder named daxlib: ambiguous"
            | e -> failtestf "%A" e

            Expect.isTrue (ok (here (Some "https://api.daxlib.org/") None false get)).IsOnline "the URL is never a path"
        }

        test "daxlib.org: the URLs asked for, and the answers read" {
            let pkgBytes =
                zipBytes [
                    "manifest.daxlib", manifestJson "DaxLib.Fake" "1.2.0" ""
                    "lib/functions.tmdl", functionsTmdl [ "DaxLib.Fake.F", "() => 1" ]
                ]

            let search =
                "{\"data\":[{\"id\":\"DaxLib.Fake\",\"version\":\"1.2.0\",\"authors\":[\"Me\"],\"description\":\"d\",\"tags\":[\"X\"],\"published\":\"2025-09-24T19:20:41+00:00\",\"downloads\":42}]}"

            let fake =
                FakeDaxlib(
                    Map.ofList [
                        "https://api.daxlib.org/v1/query-packages?q=fake%20lib&skip=0&take=100&prerelease=false",
                        Text.Encoding.UTF8.GetBytes search
                        "https://api.daxlib.org/v1/package/daxlib.fake/versions.json",
                        Text.Encoding.UTF8.GetBytes "{\"versions\":[\"1.2.0\",\"1.10.0-beta\",\"1.1.0\"]}"
                        "https://api.daxlib.org/v1/package/daxlib.fake/1.2.0/daxlib.fake.1.2.0.daxpkg", pkgBytes
                    ]
                )

            let src = PkgSource.daxlib fake.Get
            let found = ok (src.Search "fake lib" false)
            Expect.equal (found |> List.map (fun s -> s.Id, s.Downloads)) [ "DaxLib.Fake", Some 42L ] ""
            Expect.equal (ok (src.Versions "DaxLib.Fake")) [ "1.10.0-beta"; "1.2.0"; "1.1.0" ] "newest first"
            Expect.equal (ok (src.Get "DaxLib.Fake" "1.2.0")).Manifest.Version "1.2.0" ""

            match err (src.Get "Not.There" "1.0.0") with
            | PackageNotFound _ -> ()
            | e -> failtestf "%A" e

            Expect.all fake.Asked (fun u -> u.StartsWith "https://api.daxlib.org/v1/") "only the daxlib API"
        }

        test "daxlib.org failing is a fetch error" {
            let src = PkgSource.daxlib (fun _ -> Error "connection refused")

            match err (src.Versions "A.B") with
            | PackageFetchFailed(url, reason) ->
                Expect.equal url "https://api.daxlib.org/v1/package/a.b/versions.json" ""
                Expect.equal reason "connection refused" ""
            | e -> failtestf "%A" e
        }

        test "the fetcher contacts only https://api.daxlib.org" {
            for url in
                [
                    "http://api.daxlib.org/v1/x"
                    "https://daxlib.org/v1/x"
                    "https://api.daxlib.org.evil.example/v1/x"
                    "https://api.daxlib.org:8443/v1/x"
                    "https://user:pw@api.daxlib.org/v1/x"
                ] do
                Expect.isSome (PkgFetch.refuse (Uri url)) url

            Expect.isNone (PkgFetch.refuse (Uri "https://api.daxlib.org/v1/package/a/versions.json")) ""
        }
    ]

[<Tests>]
let commandTests =
    testList "pkg command" [
        test "install is a dry run; --save writes; list reads it back" {
            use p = project ()
            let model = Path.Combine(p.Path, "Sales.SemanticModel")

            let before =
                Directory.GetFiles(model, "*", SearchOption.AllDirectories)
                |> Array.map File.ReadAllText

            let dry =
                tess [ "pkg"; "install"; model; "TimeSeries.MovingAverage"; "--from"; vendored ]

            Expect.equal dry.Code ExitCode.ok dry.Err
            Expect.stringContains dry.Out "+ function:TimeSeries.MovingAverage.Simple" ""
            Expect.stringContains dry.Err "Installed TimeSeries.MovingAverage 0.1.1 (7 functions added)" ""

            let after =
                Directory.GetFiles(model, "*", SearchOption.AllDirectories)
                |> Array.map File.ReadAllText

            Expect.equal after before "nothing written"

            let saved =
                tess [
                    "pkg"
                    "install"
                    model
                    "TimeSeries.MovingAverage@0.1.1"
                    "--from"
                    vendored
                    "--save"
                    "--json"
                ]

            Expect.equal saved.Code ExitCode.ok saved.Err
            use doc = json saved
            Expect.isFalse (doc.RootElement.GetProperty("dryRun").GetBoolean()) ""

            let list = tess [ "pkg"; "list"; model; "--json" ]
            Expect.equal list.Code ExitCode.ok list.Err
            use l = json list
            let pkgs = l.RootElement.GetProperty("packages")
            Expect.equal (pkgs.GetArrayLength()) 1 ""
            let first = pkgs.[0]
            Expect.equal (first.GetProperty("id").GetString()) "TimeSeries.MovingAverage" ""
            Expect.equal (first.GetProperty("provider").GetString()) "https://api.daxlib.org" ""
            Expect.equal (first.GetProperty("functions").GetArrayLength()) 7 ""

            for f in first.GetProperty("functions").EnumerateArray() do
                Expect.equal (f.GetProperty("state").GetString()) "unchanged" "unchanged after tess saved it"

            // Hashes as TE3 recorded them, after tess's own TMDL save and reload.
            let _, saved = ok (ModelIO.loadPath model)

            for name, hash in spacepartsHashes do
                Expect.equal (fnExt (saved.Model.Functions.Find name) Pkg.hashProperty) (Some hash) name

            let text = tess [ "pkg"; "list"; model ]
            Expect.stringContains text.Out "TimeSeries.MovingAverage 0.1.1  (7 functions)" ""
        }

        test "remove is refused while DAX calls the package's functions, unless --force" {
            use p = project ()
            let model = Path.Combine(p.Path, "Sales.SemanticModel")

            let dir =
                writePackage p.Path "My.Lib" "1.0.0" [ "My.Lib.Twice", "(x: INT64) => x * 2" ]

            let i = tess [ "pkg"; "install"; model; "My.Lib"; "--from"; dir; "--save" ]
            Expect.equal i.Code ExitCode.ok i.Err

            let add =
                tess [
                    "add"
                    model
                    "measure:Sales/Doubled"
                    "--expression"
                    "My.Lib.Twice(2)"
                    "--save"
                ]

            Expect.equal add.Code ExitCode.ok add.Err
            let r = tess [ "pkg"; "remove"; model; "My.Lib"; "--save"; "--json" ]
            Expect.equal r.Code ExitCode.input $"refused: {r.Out}{r.Err}"
            use doc = json r
            Expect.equal (doc.RootElement.GetProperty("error").GetProperty("code").GetString()) "remove-blocked" r.Out
            Expect.stringContains (tess [ "pkg"; "list"; model ]).Out "My.Lib 1.0.0" "still installed"

            let forced = tess [ "pkg"; "remove"; model; "My.Lib"; "--save"; "--force" ]
            Expect.equal forced.Code ExitCode.ok forced.Err
            Expect.stringContains (tess [ "pkg"; "list"; model ]).Out "No packages installed." "removed"
        }

        test "update shows the diff and moves the version" {
            use p = project ()
            let model = Path.Combine(p.Path, "Sales.SemanticModel")
            let src = p.Combine "src"
            writePackage src "My.Lib" "1.0.0" [ "My.Lib.F", "() => 1" ] |> ignore
            Expect.equal (tess [ "pkg"; "install"; model; "My.Lib@1.0.0"; "--from"; src; "--save" ]).Code 0 ""
            writePackage src "My.Lib" "1.1.0" [ "My.Lib.F", "() => 2" ] |> ignore
            let dry = tess [ "pkg"; "update"; model; "--from"; src ]
            Expect.equal dry.Code ExitCode.ok dry.Err
            Expect.stringContains dry.Out "~ function:My.Lib.F" ""
            Expect.stringContains dry.Err "1.0.0 -> 1.1.0" ""
            Expect.equal (tess [ "pkg"; "update"; model; "--from"; src; "--save" ]).Code 0 ""
            Expect.stringContains (tess [ "pkg"; "list"; model ]).Out "My.Lib 1.1.0" ""
            Expect.stringContains (tess [ "pkg"; "update"; model; "--from"; src ]).Err "is the latest version" ""
        }

        test "TESS_PKG_SOURCE names the source; no source is a usage error" {
            let r =
                tessWithEnv [ PkgCommand.sourceVariable, vendored ] [ "pkg"; "search"; "moving"; "--json" ]

            Expect.equal r.Code ExitCode.ok r.Err
            use doc = json r

            Expect.equal
                (doc.RootElement.GetProperty("packages").[0].GetProperty("id").GetString())
                "TimeSeries.MovingAverage"
                ""

            let none = tess [ "pkg"; "search"; "x"; "--json" ]
            Expect.equal none.Code ExitCode.usage none.Out
            use e = json none
            Expect.equal (e.RootElement.GetProperty("error").GetProperty("code").GetString()) "no-package-source" ""

            let offline =
                tessWithEnv [ PkgCommand.sourceVariable, "daxlib" ] [ "pkg"; "show"; "X.Y"; "--offline" ]

            Expect.equal offline.Code ExitCode.usage offline.Err
        }

        test "show lists versions and functions" {
            let r = tess [ "pkg"; "show"; "TimeSeries.MovingAverage"; "--from"; vendored ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "TimeSeries.MovingAverage 0.1.1" ""
            Expect.stringContains r.Out "    TimeSeries.MovingAverage.Geometric" ""

            let missing =
                tess [ "pkg"; "show"; "TimeSeries.MovingAvrage"; "--from"; vendored; "--json" ]

            Expect.equal missing.Code ExitCode.input missing.Out
            use doc = json missing
            let e = doc.RootElement.GetProperty("error")
            Expect.equal (e.GetProperty("code").GetString()) "package-not-found" ""
            Expect.equal (e.GetProperty("suggestions").[0].GetString()) "TimeSeries.MovingAverage" ""
        }
    ]

// ---- The sample model (optional) ---------------------------------------------------------------

[<Tests>]
let sampleTests =
    testList "pkg sample model (TESSERA_SAMPLE_MODEL)" [
        match Environment.GetEnvironmentVariable "TESSERA_SAMPLE_MODEL" with
        | null
        | "" ->
            testCase "not configured"
            <| fun () -> skiptest "TESSERA_SAMPLE_MODEL is not set"
        | path ->
            test "list reads what TE3 installed into spaceparts, and the functions changed since" {
                let r = tess [ "pkg"; "list"; path; "--json" ]
                Expect.equal r.Code ExitCode.ok r.Err
                use doc = json r

                let packages = [
                    for p in doc.RootElement.GetProperty("packages").EnumerateArray() ->
                        let fs = [
                            for f in p.GetProperty("functions").EnumerateArray() -> f.GetProperty("state").GetString()
                        ]

                        p.GetProperty("id").GetString(),
                        p.GetProperty("version").GetString(),
                        fs.Length,
                        fs |> List.filter (fun s -> s = "changed") |> List.length
                ]

                Expect.equal
                    packages
                    [
                        "DaxLib.FormatString", "0.1.2", 29, 3
                        "TimeSeries.MovingAverage", "0.1.1", 7, 0
                    ]
                    ""
            }

            test "installing the vendored package gives spaceparts' hashes (read from the model itself)" {
                let _, sp = ok (ModelIO.loadPath path)
                let db = emptyModel ()
                ok (Pkg.install db (timeSeries ()) installOpts) |> ignore

                for f in db.Model.Functions do
                    let theirs = sp.Model.Functions.Find f.Name
                    Expect.equal (fnExt f Pkg.hashProperty) (fnExt theirs Pkg.hashProperty) f.Name
                    Expect.equal f.Expression theirs.Expression f.Name
            }
    ]
