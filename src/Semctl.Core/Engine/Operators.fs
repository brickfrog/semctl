namespace Semctl.DaxEngine

// The operators on values: what the evaluator (Eval.fs) does once it has an operator's operands,
// shared with the evaluation of an iterator's rows from their buckets (GroupAgg.fs).

open Semctl
open Semctl.DaxAst

module Operators =

    /// A binary operator other than AND, OR and IN (which read their right operand themselves)
    /// on its operands' values. `span`: the right operand's.
    let binary (ctx: Ctx) (op: BinaryOp) (span: Span) (l: DaxValue) (r: DaxValue) : DaxValue =
        let coll = ctx.Model.Collation

        match op with
        | BinaryOp.Add
        | BinaryOp.Subtract
        | BinaryOp.Multiply
        | BinaryOp.Divide
        | BinaryOp.Power ->
            Convert.checkOperand ctx span l
            Convert.checkOperand ctx span r
        | _ -> ()

        match op with
        // A date times a number follows the documentation's multiplication table where it
        // settles the result; `DaxValue.multiply` refuses the rest (a date on the left, a whole
        // number times a date with a time of day).
        | BinaryOp.Add -> Fail.dax ctx span (DaxValue.add l r)
        | BinaryOp.Subtract -> Fail.dax ctx span (DaxValue.subtract l r)
        | BinaryOp.Multiply -> Fail.dax ctx span (DaxValue.multiply l r)
        | BinaryOp.Divide -> Fail.dax ctx span (DaxValue.divide l r)
        | BinaryOp.Power -> Fail.dax ctx span (DaxValue.power l r)
        | BinaryOp.Concat ->
            // "With two arguments it works as the CONCATENATE function" (dax.guide, & operator),
            // and for CONCATENATE "If both arguments are BLANK, the result is BLANK".
            if DaxValue.isBlank l && DaxValue.isBlank r then
                DaxValue.Blank
            else
                DaxValue.String(Convert.toText ctx span l + Convert.toText ctx span r)
        | BinaryOp.Equal -> DaxValue.Boolean(Fail.dax ctx span (DaxValue.equals coll l r))
        | BinaryOp.StrictEqual -> DaxValue.Boolean(Fail.dax ctx span (DaxValue.strictEquals coll l r))
        | BinaryOp.NotEqual -> DaxValue.Boolean(not (Fail.dax ctx span (DaxValue.equals coll l r)))
        | BinaryOp.Less -> DaxValue.Boolean(Fail.dax ctx span (DaxValue.compare coll l r) < 0)
        | BinaryOp.LessOrEqual -> DaxValue.Boolean(Fail.dax ctx span (DaxValue.compare coll l r) <= 0)
        | BinaryOp.Greater -> DaxValue.Boolean(Fail.dax ctx span (DaxValue.compare coll l r) > 0)
        | BinaryOp.GreaterOrEqual -> DaxValue.Boolean(Fail.dax ctx span (DaxValue.compare coll l r) >= 0)
        | _ -> Fail.invalid ctx span "unexpected operator"

    /// A unary operator on its operand's value. `span`: the whole expression's.
    let unary (ctx: Ctx) (op: UnaryOp) (span: Span) (v: DaxValue) : DaxValue =
        match op with
        | UnaryOp.Plus -> v
        | UnaryOp.Minus -> Fail.dax ctx span (DaxValue.negate v)
        | UnaryOp.Not -> Fail.dax ctx span (DaxValue.logicalNot v)
