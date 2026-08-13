namespace ProcessKit.Mutation

open System
open Mono.Cecil
open Mono.Cecil.Cil

/// One concrete rewrite of one CIL instruction: what it is called in the report, what it does in
/// human terms, and the in-place edit itself.
type Mutation =
    { Kind: string
      Description: string
      Apply: Instruction -> unit }

/// The mutation operators.
///
/// They are defined over CIL, not over F# syntax — which is the entire reason this tier works on an
/// F# codebase at all (see CONTRIBUTING.md, "Mutation testing"). The set is deliberately small and
/// boundary-focused rather than exhaustive: the question this tier answers is "do the tests actually
/// pin the limits, or do they merely execute them", so `<` vs `<=`, a negated condition, a flipped
/// arithmetic sign and an off-by-one constant carry almost all of the signal, while a large operator
/// zoo would mostly add equivalent mutants and wall-clock time.
///
/// Every operator preserves the instruction's operand shape (a branch stays a branch to the same
/// target, a constant stays a constant), so a mutant either verifies or fails to load — it can never
/// silently corrupt control flow into something that looks like a passing test run.
module Mutators =

    /// `<` <-> `<=` and `>` <-> `>=` on conditional branches, in every signed/unsigned form.
    /// Off-by-one at a limit is exactly the defect class the boundary tests in this repo exist for
    /// (retained-byte caps, line caps, backoff ceilings), so this family is the highest-signal one.
    ///
    /// Short forms are listed too even though `SimplifyMacros` expands them before enumeration —
    /// the table then stays correct if that pre-pass is ever changed.
    let private conditionalBoundary =
        [ OpCodes.Blt, OpCodes.Ble
          OpCodes.Ble, OpCodes.Blt
          OpCodes.Bgt, OpCodes.Bge
          OpCodes.Bge, OpCodes.Bgt
          OpCodes.Blt_Un, OpCodes.Ble_Un
          OpCodes.Ble_Un, OpCodes.Blt_Un
          OpCodes.Bgt_Un, OpCodes.Bge_Un
          OpCodes.Bge_Un, OpCodes.Bgt_Un
          OpCodes.Blt_S, OpCodes.Ble_S
          OpCodes.Ble_S, OpCodes.Blt_S
          OpCodes.Bgt_S, OpCodes.Bge_S
          OpCodes.Bge_S, OpCodes.Bgt_S
          OpCodes.Blt_Un_S, OpCodes.Ble_Un_S
          OpCodes.Ble_Un_S, OpCodes.Blt_Un_S
          OpCodes.Bgt_Un_S, OpCodes.Bge_Un_S
          OpCodes.Bge_Un_S, OpCodes.Bgt_Un_S ]

    /// Take the other branch. Kills a test that reaches a decision but never asserts which way it
    /// went.
    let private negateConditional =
        [ OpCodes.Brtrue, OpCodes.Brfalse
          OpCodes.Brfalse, OpCodes.Brtrue
          OpCodes.Brtrue_S, OpCodes.Brfalse_S
          OpCodes.Brfalse_S, OpCodes.Brtrue_S
          OpCodes.Beq, OpCodes.Bne_Un
          OpCodes.Bne_Un, OpCodes.Beq
          OpCodes.Beq_S, OpCodes.Bne_Un_S
          OpCodes.Bne_Un_S, OpCodes.Beq_S ]

    /// The same idea on the value-producing comparisons (`clt`/`cgt`/`ceq`), which F# emits wherever
    /// a comparison's result is a value rather than a branch.
    let private comparison =
        [ OpCodes.Clt, OpCodes.Cgt
          OpCodes.Cgt, OpCodes.Clt
          OpCodes.Clt_Un, OpCodes.Cgt_Un
          OpCodes.Cgt_Un, OpCodes.Clt_Un
          OpCodes.Ceq, OpCodes.Clt ]

    /// Flip the sign / invert the scaling. Unchecked forms only: the `.ovf` variants would mostly
    /// mutate into an OverflowException, which any test touching the line kills trivially and which
    /// therefore measures nothing about assertion strength.
    let private arithmetic =
        [ OpCodes.Add, OpCodes.Sub
          OpCodes.Sub, OpCodes.Add
          OpCodes.Mul, OpCodes.Div
          OpCodes.Div, OpCodes.Mul ]

    let private swapTable =
        [ "ConditionalBoundary", conditionalBoundary
          "NegateConditional", negateConditional
          "Comparison", comparison
          "Arithmetic", arithmetic ]
        |> List.collect (fun (kind, pairs) -> pairs |> List.map (fun (from, into) -> from.Code, (kind, from, into)))
        |> Map.ofList

    let private swapTo (target: OpCode) =
        fun (instruction: Instruction) -> instruction.OpCode <- target

    // `objnull` rather than `obj`: `box` produces the nullable-oblivious object type, and Cecil's
    // `Instruction.Operand` is itself an unannotated `object`.
    let private replaceOperand (target: OpCode) (value: objnull) =
        fun (instruction: Instruction) ->
            instruction.OpCode <- target
            instruction.Operand <- value

    /// `n` -> `n + 1` on a loaded constant. One direction only, on purpose: `n - 1` would double the
    /// catalog (and the wall-clock cost) while probing the same boundary from the other side, and a
    /// test that pins a limit exactly rejects both.
    ///
    /// The maximum value of each width is skipped rather than wrapped: a wrapped constant is a
    /// different, much cruder mutant (it flips the sign of the bound) and reads as noise in the
    /// report.
    let private constantMutation (instruction: Instruction) =
        match instruction.OpCode.Code, instruction.Operand with
        | Code.Ldc_I4, (:? int as value) when value <> Int32.MaxValue ->
            Some
                { Kind = "Constant"
                  Description = $"ldc.i4 {value} -> {value + 1}"
                  Apply = replaceOperand OpCodes.Ldc_I4 (box (value + 1)) }
        | Code.Ldc_I8, (:? int64 as value) when value <> Int64.MaxValue ->
            Some
                { Kind = "Constant"
                  Description = $"ldc.i8 {value} -> {value + 1L}"
                  Apply = replaceOperand OpCodes.Ldc_I8 (box (value + 1L)) }
        | Code.Ldc_R8, (:? float as value) when Double.IsFinite value ->
            Some
                { Kind = "Constant"
                  Description = $"ldc.r8 {value} -> {value + 1.0}"
                  Apply = replaceOperand OpCodes.Ldc_R8 (box (value + 1.0)) }
        | _ -> None

    /// Every mutation this engine can make to `instruction`, in a stable order.
    let candidates (instruction: Instruction) : Mutation list =
        let swap =
            match Map.tryFind instruction.OpCode.Code swapTable with
            | Some(kind, from, into) ->
                [ { Kind = kind
                    Description = $"{from.Name} -> {into.Name}"
                    Apply = swapTo into } ]
            | None -> []

        swap @ Option.toList (constantMutation instruction)
