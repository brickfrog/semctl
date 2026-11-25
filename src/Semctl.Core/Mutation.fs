/// Editing a model safely: snapshot it, apply an edit, and report exactly what changed and which
/// errors the edit introduced. Commands decide whether to save (dry run by default).
module Semctl.Mutation

open Microsoft.AnalysisServices.Tabular

/// What an edit reports besides the model changes themselves.
type Effect = {
    /// Things the edit didn't (or couldn't) do, for the user to check.
    Warnings: string list
    /// A one-line description of what was done, e.g. "Renamed ... ; updated 3 expressions".
    Notes: string list
    /// Objects the edit renamed (old ref, new ref), so they're reported as moves, not delete + create.
    Renamed: (ObjectRef * ObjectRef) list
}

module Effect =
    let none = {
        Warnings = []
        Notes = []
        Renamed = []
    }

type Applied = {
    /// Changes in diff form (for line-diff rendering) ...
    Diff: Diff.Change list
    /// ... and in the shared change-set shape.
    Changes: ChangeSet.ObjectChange list
    /// Errors present after the edit that weren't before it.
    IntroducedErrors: Validate.Issue list
    Effect: Effect
}

/// Validate rules an edit can break. Others (relationship data types, sort-by, empty expressions)
/// describe the model as the user left it, not something an edit typically causes.
let private gatedRules =
    set [
        "unresolved-reference"
        "circular-reference"
        "duplicate-name"
        "invalid-token"
        "tom-validation"
        "unknown-function"
    ]

/// Checks the gate runs besides Validate's gated rules; only their error-severity issues gate.
/// (ValidateSemantic's rules compile after the engine, so they can't be named here: SaveGate passes
/// them in.)
type Checks = Deps.Graph -> Database -> Validate.Issue list

module Checks =
    let none: Checks = fun _ _ -> []

let private errorsWith (extra: Checks) (db: Database) =
    let g = Deps.build db

    (Validate.validateGraph g db
     |> List.filter (fun i -> i.Severity = Validate.Severity.Error && gatedRules.Contains i.Rule))
    @ (extra g db |> List.filter (fun i -> i.Severity = Validate.Severity.Error))

/// `apply`, gating on `extra` too.
let applyWith (extra: Checks) (db: Database) (edit: Database -> Result<Effect, 'err>) : Result<Applied, 'err> =
    let errors = errorsWith extra
    let before = ModelIO.clone db
    let beforeErrors = errors before

    edit db
    |> Result.map (fun effect ->
        let diff =
            Diff.compare
                {
                    Diff.Options.none with
                        KnownRenames = effect.Renamed
                }
                before
                db

        let changes = ChangeSet.ofDiff diff

        let oldRefOf =
            changes
            |> List.choose (fun c -> c.MovedFrom |> Option.map (fun old -> c.Ref, old))
            |> Map.ofList

        let key (i: Validate.Issue) =
            i.Rule, Map.tryFind i.Ref oldRefOf |> Option.defaultValue i.Ref, i.Message

        let known = beforeErrors |> List.map key |> Set.ofList

        {
            Diff = diff
            Changes = changes
            IntroducedErrors = errors db |> List.filter (fun i -> not (known.Contains(key i)))
            Effect = effect
        })

/// Apply `edit` to `db` (mutating it). Before-and-after comparison runs on a snapshot, so the
/// change set is exact. Issues of renamed objects are matched to their old names, so they don't
/// count as new. Gates on Validate's rules only; SaveGate.apply adds ValidateSemantic's.
let apply (db: Database) (edit: Database -> Result<Effect, 'err>) : Result<Applied, 'err> =
    applyWith Checks.none db edit

/// `gatedErrors`, with `extra` too.
let gatedErrorsWith (extra: Checks) (db: Database) = errorsWith extra db

/// The errors an edit is judged by (the save gate's): errors of the rules an edit can break.
let gatedErrors (db: Database) = gatedErrorsWith Checks.none db

/// `applyResolving`, gating on `extra` too.
let applyResolvingWith
    (extra: Checks)
    (db: Database)
    (edit: Database -> Result<Effect, 'err>)
    : Result<Applied * Validate.Issue list, 'err> =
    let errors = errorsWith extra
    let before = errors db

    applyWith extra db edit
    |> Result.map (fun applied ->
        let oldRefOf =
            applied.Changes
            |> List.choose (fun c -> c.MovedFrom |> Option.map (fun old -> c.Ref, old))
            |> Map.ofList

        let after =
            errors db
            |> List.map (fun i -> i.Rule, Map.tryFind i.Ref oldRefOf |> Option.defaultValue i.Ref, i.Message)
            |> Set.ofList

        applied, before |> List.filter (fun i -> not (after.Contains(i.Rule, i.Ref, i.Message))))

/// `apply`, also returning the gated errors present before the edit that are gone after it
/// ("resolved"). Issues of renamed objects are matched under their old names, as in `apply`.
let applyResolving
    (db: Database)
    (edit: Database -> Result<Effect, 'err>)
    : Result<Applied * Validate.Issue list, 'err> =
    applyResolvingWith Checks.none db edit
