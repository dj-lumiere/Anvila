using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// The rule for an emitter that ran out (RF-S517). <c>each x in it</c> takes items from the emitter <c>it</c> until its
/// <c>emit</c> fails, so after an <c>each</c> that wasn't left with <c>break</c>, <c>it</c> has nothing left: a second
/// <c>each</c> over it runs zero times, silently. The rule follows each name a routine walks with <c>each</c> through
/// the routine's own lines and reports an <c>each</c> over a name that has certainly run out there:
/// <list type="bullet">
/// <item>after an earlier <c>each</c> over it on every path to this one, with no <c>break</c> out of that earlier
/// loop, or</item>
/// <item>inside a loop that comes round again, when the name is made outside that loop and the pass before took every
/// item.</item>
/// </list>
/// <para>Anything else the routine does with the emitter or with what it was made from (any line that names them,
/// other than <c>it.emit()</c>) may refill it, and the name counts as not run out from there. So the rule reports only
/// what certainly runs zero times.</para>
/// <para>Runs over the analyzed user bodies, where every <c>each</c> is already lowered: the iterator declaration
/// <c>var _lf_iter_N = it.emitter()</c> followed by a <see cref="LoopStatement"/> marked with
/// <see cref="LoopStatement.IterationSourceName"/>, whose body is the step <c>when try _lf_iter_N.emit()</c>. The
/// step's end-of-walk arm is the loop ending by itself, every other way out is a <c>break</c>.</para>
/// </summary>
internal sealed class EmitterReuseChecker(TypeRegistry registry, DiagnosticReporter report)
{
    private readonly DiagnosticReporter _report = report;

    /// <summary>The loops already reported, so a loop walked again (a pass after the first) is reported once.</summary>
    private readonly HashSet<LoopStatement> _reported = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Runs the rule over every routine of the user programs.</summary>
    internal void Check(IEnumerable<Program> programs)
    {
        foreach (Program program in programs)
        {
            AstWalker.Walk(root: program,
                visit: node =>
                {
                    if (node is RoutineDeclaration { Body: { } body })
                    {
                        var walker = new Walker(owner: this);
                        walker.NoteSources(body: body);
                        walker.Walk(body: [body], state: new State());
                    }
                });
        }
    }

    /// <summary>Whether a value of <paramref name="type"/> is an emitter: it hands out its items through a failable
    /// <c>emit</c>, so walking it uses them up.</summary>
    private bool IsEmitter(TypeSymbol? type)
    {
        // A Suflae emitter is held by its shared handle (`Roamed[MemoryEmitter[S64]]`): the handle walks the emitter.
        if (type is RecordTypeSymbol { TypeArguments: [var inner, ..] } handle &&
            TypeRegistry.GetRcWrapperBaseName(type: handle) != null)
        {
            type = inner;
        }

        return type != null && registry.LookupMemberRoutine(type: type, memberRoutineName: "emit", isFailable: true) != null;
    }

    /// <summary>The names that have run out on every path to a line, each with the <c>each</c> that emptied it.</summary>
    private sealed class State
    {
        public readonly Dictionary<string, LoopStatement> Empty = new(comparer: StringComparer.Ordinal);

        public State Copy()
        {
            var s = new State();
            foreach ((string k, LoopStatement v) in Empty)
            {
                s.Empty[key: k] = v;
            }

            return s;
        }

        /// <summary>What holds on every one of the paths: a name has run out only when it has on each of them.</summary>
        public static State? Meet(IEnumerable<State?> paths)
        {
            State? met = null;
            foreach (State? p in paths)
            {
                if (p is null)
                {
                    continue;
                }

                if (met is null)
                {
                    met = p.Copy();
                    continue;
                }

                foreach (string k in met.Empty.Keys.ToList())
                {
                    if (!p.Empty.ContainsKey(key: k))
                    {
                        met.Empty.Remove(key: k);
                    }
                }
            }

            return met;
        }
    }

    /// <summary>Where the paths through a body go: off its end, out of the loop it is in, round that loop again, or out
    /// of the <c>each</c> loop it is in because the emitter ran out.</summary>
    private sealed class Paths
    {
        public State? Fall;
        public readonly List<State?> Breaks = [];
        public readonly List<State?> Continues = [];
        public readonly List<State?> Ends = [];
    }

    private sealed class Walker(EmitterReuseChecker owner)
    {
        /// <summary>The names each emitter variable was made from (<c>xs</c> of <c>var it = xs.emitter()</c>), and the
        /// declaration that made it.</summary>
        private readonly Dictionary<string, (HashSet<string> From, VariableDeclaration Line)> _made =
            new(comparer: StringComparer.Ordinal);

        /// <summary>The loops the walk is in, outermost first, and whether the walk is on a pass after the first.</summary>
        private readonly List<(LoopStatement Loop, bool Again)> _loops = [];

        public Paths Walk(List<Statement> body, State? state)
        {
            var paths = new Paths();
            State? cur = state;
            for (int i = 0; i < body.Count; i++)
            {
                if (cur is null)
                {
                    break;
                }

                Statement s = body[index: i];
                // The iterator an `each` declares (`var _lf_iter_N = it.emitter()`) is part of the loop after it, not a
                // line that names `it`.
                if (s is DeclarationStatement { Declaration: VariableDeclaration { Name: var iterName } } &&
                    i + 1 < body.Count && body[index: i + 1] is LoopStatement { IteratorName: { } walking } &&
                    walking == iterName)
                {
                    continue;
                }

                cur = Step(s: s, cur: cur, paths: paths);
            }

            paths.Fall = cur;
            return paths;
        }

        private Paths WalkOne(Statement s, State? state)
        {
            return Walk(body: s is BlockStatement b ? b.Statements : [s], state: state);
        }

        private State? Step(Statement s, State cur, Paths paths)
        {
            switch (s)
            {
                case BlockStatement block:
                {
                    Paths inner = Walk(body: block.Statements, state: cur);
                    Join(into: paths, inner);
                    return inner.Fall;
                }
                case DangerStatement danger:
                {
                    Paths inner = Walk(body: danger.Body.Statements, state: cur);
                    Join(into: paths, inner);
                    return inner.Fall;
                }
                case IfStatement ifs:
                {
                    cur = Named(cur: cur, node: ifs.Condition);
                    Paths then = WalkOne(s: ifs.ThenStatement, state: cur.Copy());
                    Paths other = ifs.ElseStatement is { } alt
                        ? WalkOne(s: alt, state: cur.Copy())
                        : new Paths { Fall = cur };
                    Join(into: paths, then, other);
                    return State.Meet(paths: [then.Fall, other.Fall]);
                }
                case WhenStatement { IsEachStep: true } step:
                {
                    // The step of an `each`: its first arm is the emitter running out, the loop ends by itself.
                    var arms = new List<Paths>();
                    for (int c = 0; c < step.Clauses.Count; c++)
                    {
                        if (c == 0)
                        {
                            paths.Ends.Add(item: cur.Copy());
                            continue;
                        }

                        arms.Add(item: WalkOne(s: step.Clauses[index: c].Body, state: cur.Copy()));
                    }

                    Join(into: paths, [.. arms]);
                    return State.Meet(paths: arms.Select(selector: a => a.Fall));
                }
                case WhenStatement whenStmt:
                {
                    cur = Named(cur: cur, node: whenStmt.Expression);
                    var arms = new List<Paths>();
                    foreach (WhenClause clause in whenStmt.Clauses)
                    {
                        State armIn = Named(cur: cur, node: clause.Pattern).Copy();
                        foreach (string bound in AstWalker.BoundNames(node: clause.Pattern))
                        {
                            armIn.Empty.Remove(key: bound);
                        }

                        arms.Add(item: WalkOne(s: clause.Body, state: armIn));
                    }

                    // Without `else`, a value no arm takes goes on after the `when`.
                    if (!whenStmt.Clauses.Any(predicate: c => c.Pattern is ElsePattern or WildcardPattern))
                    {
                        arms.Add(item: new Paths { Fall = cur });
                    }

                    Join(into: paths, [.. arms]);
                    return State.Meet(paths: arms.Select(selector: a => a.Fall));
                }
                case LoopStatement loop:
                    return WalkLoop(loop: loop, cur: cur, outer: paths, condition: null);
                case WhileStatement whileStmt:
                    return WalkLoop(loop: new LoopStatement(Body: whileStmt.Body, Location: whileStmt.Location),
                        cur: Named(cur: cur, node: whileStmt.Condition),
                        outer: paths,
                        condition: whileStmt.Condition);
                case UsingStatement usingStmt:
                {
                    cur = Named(cur: cur, node: usingStmt.Resource).Copy();
                    cur.Empty.Remove(key: usingStmt.Name);
                    Paths inner = WalkOne(s: usingStmt.Body, state: cur);
                    Join(into: paths, inner);
                    return inner.Fall;
                }
                case AccessStatement access:
                {
                    foreach (AccessTarget target in access.Targets)
                    {
                        cur = Named(cur: cur, node: target.Target);
                    }

                    cur = cur.Copy();
                    foreach (AccessTarget target in access.Targets)
                    {
                        cur.Empty.Remove(key: target.Name);
                    }

                    Paths inner = WalkOne(s: access.Body, state: cur);
                    Join(into: paths, inner);
                    return inner.Fall;
                }
                case BreakStatement:
                    paths.Breaks.Add(item: cur);
                    return null;
                case ContinueStatement:
                    paths.Continues.Add(item: cur);
                    return null;
                case ReturnStatement or ThrowStatement or CrashStatement or VariantReturnStatement:
                    return null;
                default:
                {
                    cur = Named(cur: cur, node: s);
                    foreach (string name in AstWalker.BoundNames(node: s))
                    {
                        if (cur.Empty.ContainsKey(key: name))
                        {
                            cur = cur.Copy();
                            cur.Empty.Remove(key: name);
                        }
                    }

                    if (s is DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } decl })
                    {
                        HashSet<string> from = Names(node: init, skipEmit: false);
                        from.Remove(item: decl.Name);
                        _made[key: decl.Name] = (from, decl);
                    }

                    return cur;
                }
            }
        }

        /// <summary>A loop: its body is walked once from the state before it, and again from the state its passes come
        /// back with, which is where an emitter made outside the loop and emptied inside it is found.</summary>
        private State? WalkLoop(LoopStatement loop, State cur, Paths outer, Expression? condition)
        {
            string? source = loop.IterationSourceName;
            if (source == null || !loop.IsIteratorEachLoop || !IsWalkedEmitter(loop: loop))
            {
                source = null;
            }

            if (source != null && cur.Empty.TryGetValue(key: source, value: out LoopStatement? emptied))
            {
                Report(loop: loop, source: source, emptied: emptied);
            }

            _loops.Add(item: (loop, false));
            Paths first = WalkOne(s: loop.Body, state: cur.Copy());
            _loops.RemoveAt(index: _loops.Count - 1);

            // The state a pass after the first starts from: what every way back round the loop has run out.
            State? again = State.Meet(paths: [first.Fall, .. first.Continues]);
            Paths? second = null;
            if (again is { Empty.Count: > 0 })
            {
                if (condition != null)
                {
                    again = Named(cur: again, node: condition);
                }

                if (source != null && again.Empty.TryGetValue(key: source, value: out LoopStatement? refilled))
                {
                    Report(loop: loop, source: source, emptied: refilled);
                }

                _loops.Add(item: (loop, true));
                second = WalkOne(s: loop.Body, state: again);
                _loops.RemoveAt(index: _loops.Count - 1);
            }

            // Out of the loop: through a break, or, for an `each`, when the emitter ran out (it is then empty). A
            // `while` ends by itself at its condition, from the state at its head.
            var exits = new List<State?>(collection: first.Breaks);
            if (second != null)
            {
                exits.AddRange(collection: second.Breaks);
            }

            foreach (State? end in second != null ? [.. first.Ends, .. second.Ends] : first.Ends)
            {
                if (end is null)
                {
                    continue;
                }

                State ended = end.Copy();
                if (source != null)
                {
                    ended.Empty[key: source] = loop;
                }

                exits.Add(item: ended);
            }

            if (condition != null)
            {
                exits.Add(item: again is null ? cur : State.Meet(paths: [cur, again]));
            }

            return State.Meet(paths: exits);
        }

        /// <summary>Whether the source an <c>each</c> loop walks is an emitter, read from the iterator declaration
        /// before it (<c>var _lf_iter_N = it.emitter()</c>).</summary>
        private bool IsWalkedEmitter(LoopStatement loop)
        {
            return _iteratorSources.TryGetValue(key: loop, value: out TypeSymbol? type) && owner.IsEmitter(type: type);
        }

        /// <summary>The type of the source each <c>each</c> loop walks, noted when the walk passes its iterator
        /// declaration.</summary>
        private readonly Dictionary<LoopStatement, TypeSymbol?> _iteratorSources =
            new(comparer: ReferenceEqualityComparer.Instance);

        /// <summary>Notes the source type of the <c>each</c> loops in <paramref name="body"/> from their iterator
        /// declarations.</summary>
        public void NoteSources(Statement body)
        {
            AstWalker.Walk(root: body,
                visit: node =>
                {
                    if (node is not BlockStatement block)
                    {
                        return;
                    }

                    for (int i = 0; i + 1 < block.Statements.Count; i++)
                    {
                        if (block.Statements[index: i] is DeclarationStatement
                            {
                                Declaration: VariableDeclaration
                                {
                                    Name: var iterName,
                                    Initializer: CallExpression { Callee: MemberExpression { Object: var walked } }
                                }
                            } &&
                            block.Statements[index: i + 1] is LoopStatement { IteratorName: { } name } loop &&
                            name == iterName)
                        {
                            _iteratorSources[key: loop] = walked.ResolvedType;
                        }
                    }
                });
        }

        private static void Join(Paths into, params Paths[] from)
        {
            foreach (Paths p in from)
            {
                into.Breaks.AddRange(collection: p.Breaks);
                into.Continues.AddRange(collection: p.Continues);
                into.Ends.AddRange(collection: p.Ends);
            }
        }

        /// <summary>The state after a line that names an emitter other than by its <c>emit</c>, or a name it was made
        /// from: that emitter may have been refilled.</summary>
        private State Named(State cur, object node)
        {
            if (cur.Empty.Count == 0)
            {
                return cur;
            }

            HashSet<string> named = Names(node: node, skipEmit: true);
            HashSet<string> all = Names(node: node, skipEmit: false);
            State next = cur;
            foreach (string name in cur.Empty.Keys.ToList())
            {
                bool refilled = named.Contains(item: name) ||
                                _made.TryGetValue(key: name, value: out (HashSet<string> From, VariableDeclaration Line) made) &&
                                made.From.Overlaps(other: all);
                if (!refilled)
                {
                    continue;
                }

                if (next == cur)
                {
                    next = cur.Copy();
                }

                next.Empty.Remove(key: name);
            }

            return next;
        }

        /// <summary>The names a node mentions, leaving out the receiver of an <c>emit()</c> call when
        /// <paramref name="skipEmit"/> is set.</summary>
        private static HashSet<string> Names(object node, bool skipEmit)
        {
            var emitReceivers = new HashSet<IdentifierExpression>(comparer: ReferenceEqualityComparer.Instance);
            var names = new HashSet<string>(comparer: StringComparer.Ordinal);
            if (skipEmit)
            {
                AstWalker.Walk(root: node,
                    visit: n =>
                    {
                        if (n is CallExpression
                            {
                                Arguments.Count: 0,
                                Callee: MemberExpression { MemberName: "emit", Object: IdentifierExpression receiver }
                            })
                        {
                            emitReceivers.Add(item: receiver);
                        }
                    });
            }

            AstWalker.Walk(root: node,
                visit: n =>
                {
                    if (n is IdentifierExpression id && !emitReceivers.Contains(item: id))
                    {
                        names.Add(item: id.Name);
                    }
                });
            return names;
        }

        private void Report(LoopStatement loop, string source, LoopStatement emptied)
        {
            if (!owner._reported.Add(item: loop))
            {
                return;
            }

            // The collection the emitter was made from (`xs` of `var it = xs.emitter()`), when the line that made it
            // says so. Otherwise the message asks for that line again.
            string? collection = _made.TryGetValue(key: source,
                                     value: out (HashSet<string> From, VariableDeclaration Line) made) &&
                                 made.Line.Initializer is CallExpression
                                 {
                                     Callee: MemberExpression
                                     {
                                         MemberName: "emitter", Object: IdentifierExpression from
                                     }
                                 }
                ? from.Name
                : null;
            string item = loop.Body is BlockStatement { Statements: [WhenStatement { IsEachStep: true } step] } &&
                          step.Clauses[^1].Pattern is ElsePattern { VariableName: { } bound } &&
                          !bound.StartsWith(value: "_lf_", comparisonType: StringComparison.Ordinal)
                ? bound
                : "x";
            string remake = collection != null
                ? $"var {source} = {collection}.emitter()"
                : $"the line that makes '{source}'";
            string direct = collection != null
                ? $", or walk the collection itself (each {item} in {collection})"
                : "";
            // The first pass reports what runs out on one pass, so a pass after it reports what the loop brought round.
            (LoopStatement Loop, bool Again) around = _loops.LastOrDefault(predicate: x => x.Again);
            if (around.Loop is not null)
            {
                owner._report(code: SemanticDiagnosticCode.EmitterRunOut,
                    message:
                    $"This each over '{source}' runs zero times on every pass of the loop on line " +
                    $"{around.Loop.Location.Line} after the first: you made '{source}' outside that loop, and " +
                    (ReferenceEquals(objA: emptied, objB: loop)
                        ? "this each"
                        : $"the each on line {emptied.Location.Line}") +
                    " takes every item from it on the first pass. Make the emitter inside the loop, just before this " +
                    $"each ({remake}), so each pass walks the collection from its start{direct}.",
                    location: loop.Location);
                return;
            }

            owner._report(code: SemanticDiagnosticCode.EmitterRunOut,
                message:
                $"This each over '{source}' runs zero times: the each on line {emptied.Location.Line} already took " +
                $"every item from it, and an emitter hands out each item once. Make a new emitter for this loop " +
                $"({remake}) to walk the collection again{direct}.",
                location: loop.Location);
        }
    }
}
