using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// Whether control can leave a statement: by returning from the routine (<see cref="AlwaysTerminates"/>,
/// the missing-return check) or by any exit of the current scope (<see cref="HasDefiniteExit"/>, guard-clause
/// narrowing). A <c>when</c> terminates only when the verifier found it exhaustive, which the caller passes.
/// </summary>
internal static class ControlFlowExits
{
    /// <summary>
    /// Checks if a statement always produces a return value (return, throw, absent, becomes).
    /// Used for missing-return validation (#144).
    /// Unlike <see cref="HasDefiniteExit"/>, this does not count break/continue as terminating,
    /// since they exit loops but don't return a value from the routine.
    /// </summary>
    internal static bool AlwaysTerminates(Statement statement, IReadOnlySet<WhenStatement> exhaustiveWhens)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(predicate: s =>
                AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: s)),
            IfStatement { ElseStatement: not null } ifStmt =>
                AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: ifStmt.ThenStatement) &&
                AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: ifStmt.ElseStatement),
            // A buildtime arm-expansion `when` is provably exhaustive: `expand … branchof(T)` covers
            // every payload arm and any explicit clauses (e.g. `is None =>`) cover the rest. It
            // terminates iff every explicit clause body AND the arm template body terminate.
            WhenStatement { ArmExpansion: { } armExp } armWhen => armWhen.Clauses.All(
                    predicate: c => AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: c.Body)) &&
                AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: armExp.Template.Body),

            WhenStatement whenStmt => whenStmt.Clauses.Count > 0 &&
                                      (exhaustiveWhens.Contains(item: whenStmt) ||
                                       whenStmt.Clauses.Any(predicate: c =>
                                           c.Pattern is ElsePattern or WildcardPattern)) &&
                                      whenStmt.Clauses.All(predicate: c =>
                                          AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: c.Body)),
            DangerStatement danger => AlwaysTerminates(exhaustiveWhens: exhaustiveWhens, statement: danger.Body),
            // An unconditional `loop` has a fall-through edge ONLY through a `break` that targets it.
            // With no such break, control leaves the loop only via `return`/`throw`/`absent`, so a
            // routine whose control reaches the loop always terminates through it. This does NOT prove
            // the loop halts (undecidable, deliberately not attempted) — it observes the absence of a
            // normal-exit edge, which IS decidable. `while` is excluded: its condition may be false on
            // entry, so it can fall through without ever running the body.
            LoopStatement loopStmt => !LoopBodyCanBreakOut(statement: loopStmt.Body),
            _ => false
        };
    }

    /// <summary>
    /// True when <paramref name="statement"/> contains a <c>break</c> that targets the *enclosing*
    /// loop — i.e. a break not nested inside another loop. Used by
    /// <see cref="AlwaysTerminates"/> to decide whether an unconditional <c>loop</c> has a
    /// fall-through edge. Nested loops (<c>loop</c>/<c>while</c>/<c>each</c>) are boundaries: a break
    /// inside them belongs to that inner loop, so we do not descend into them. RazorForge breaks are
    /// unlabeled, so a break always targets the nearest enclosing loop.
    /// </summary>
    private static bool LoopBodyCanBreakOut(Statement statement)
    {
        return statement switch
        {
            BreakStatement => true,
            BlockStatement block => block.Statements.Any(predicate: LoopBodyCanBreakOut),
            IfStatement ifStmt => LoopBodyCanBreakOut(statement: ifStmt.ThenStatement) ||
                                  ifStmt.ElseStatement is not null &&
                                  LoopBodyCanBreakOut(statement: ifStmt.ElseStatement),
            WhenStatement whenStmt => whenStmt.Clauses.Any(predicate: c =>
                LoopBodyCanBreakOut(statement: c.Body)),
            DangerStatement danger => LoopBodyCanBreakOut(statement: danger.Body),
            // Nested loops swallow their own breaks — do not descend.
            LoopStatement or WhileStatement or EachStatement => false,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a statement always exits the current scope (return, throw, absent, break, continue).
    /// Used for guard clause narrowing.
    /// </summary>
    internal static bool HasDefiniteExit(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BreakStatement => true,
            ContinueStatement => true,
            BlockStatement block => block.Statements.Any(predicate: s =>
                HasDefiniteExit(statement: s)),
            IfStatement { ElseStatement: not null } ifStmt =>
                HasDefiniteExit(statement: ifStmt.ThenStatement) &&
                HasDefiniteExit(statement: ifStmt.ElseStatement),
            _ => false
        };
    }
}
