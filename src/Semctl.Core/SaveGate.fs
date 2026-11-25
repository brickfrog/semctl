/// The save gate and `deploy`'s error check with every validate rule that can gate: Validate's
/// (Mutation's gated rules for an edit, all its errors for a deploy) plus ValidateSemantic's
/// error-severity issues. ValidateSemantic compiles after the engine, Mutation and Deploy before it,
/// so this file joins them. The CLI's editing commands, `sm interactive`, `sm mcp` and
/// `sm deploy` all go through here.
///
/// Of ValidateSemantic's rules, two report errors and so gate: invalid-culture (a name that isn't
/// shaped like a culture name; an unknown but well-formed one is a warning) and
/// ignore-outside-summarizecolumns. calculate-variable and week-without-weekly-calendar are
/// warnings and never gate.
module Semctl.SaveGate

open Microsoft.AnalysisServices.Tabular

/// ValidateSemantic's checks, for Mutation's and Deploy's `extra`.
let semanticChecks: Mutation.Checks = ValidateSemantic.check

/// The errors an edit is judged by.
let gatedErrors (db: Database) =
    Mutation.gatedErrorsWith semanticChecks db

/// Mutation.apply with every gating rule.
let apply (db: Database) (edit: Database -> Result<Mutation.Effect, SemctlError>) =
    Mutation.applyWith semanticChecks db edit

/// Mutation.applyResolving with every gating rule.
let applyResolving (db: Database) (edit: Database -> Result<Mutation.Effect, SemctlError>) =
    Mutation.applyResolvingWith semanticChecks db edit

/// Deploy.plan with every validate rule's errors.
let deployPlan (opts: Deploy.Options) (diff: Diff.Options) (target: Deploy.Target) (local: Database) =
    Deploy.planWith semanticChecks opts diff target local
