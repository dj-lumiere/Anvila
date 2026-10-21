using Builder.Declaration;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// "Did you mean …?" suggestion support for name-resolution diagnostics
/// (unknown identifier / unknown type / member not found). Typos are the most common
/// cause of these errors; a close-match suggestion turns a search through scope into
/// a one-glance fix. The candidates are read from <paramref name="registry"/>: the names in scope, the
/// routines and the types it holds.
/// </summary>
internal sealed class NameSuggestions(TypeRegistry registry)
{
    /// <summary>The suggestion suffix for an unknown identifier.</summary>
    internal string ForIdentifier(string name)
    {
        // A type called on itself (`OsFileManager.read_text(...)`) reads as a name first.
        return OsServiceRename(name: name) ?? DidYouMean(target: name, candidates: IdentifierSuggestionCandidates());
    }

    /// <summary>The hint for an operating system service written with the <c>Os</c> its name lost in 0.5
    /// (<c>OsFileManager</c> is <c>FileManager</c>), or null for any other name.</summary>
    private string? OsServiceRename(string name)
    {
        return name is "OsFileManager" or "OsEnvironment" or "OsEntropy" &&
               TypeSuggestionCandidates().Contains(value: name[2..])
            ? $" It is called '{name[2..]}' now (migrate --step files rewrites it)."
            : null;
    }

    /// <summary>The suggestion suffix for an unknown type name.</summary>
    internal string ForType(string typeName)
    {
        if (OsServiceRename(name: typeName) is { } service)
        {
            return service;
        }

        // A type renamed for 0.5 names its new name.
        string? renamed = typeName switch
        {
            "Character" or "Letter" => "Char",
            "Routine" => "Callable",
            "RoamController" => "RoamManager",
            "Queue" => "Mailbox",
            "Address" or "CPtr" => "Addr",
            "Exposed" or "Wielded" => "FileStorage",
            "Consulting" => "Viewing",
            "Amending" => "Modifying",
            _ => null
        };
        // `Callable` is the builder's own type of routine values, not a registered one.
        if (renamed != null && (renamed == "Callable" || TypeSuggestionCandidates().Contains(value: renamed)))
        {
            return renamed == "Addr"
                ? " It is called 'Addr' now (migrate --step addr rewrites it): compare two with '===' and move one by a " +
                  "'ByteSize' with '+%' / '-%'."
                : $" It is called '{renamed}' now.";
        }

        // The binary floating-point types are B16, B32, B64 and B128: F64 names B64, not the S64 or U64 an edit away.
        if (typeName is "F16" or "F32" or "F64" or "F128" &&
            TypeSuggestionCandidates().Contains(value: $"B{typeName[1..]}"))
        {
            return $" Did you mean 'B{typeName[1..]}'?";
        }

        return DidYouMean(target: typeName, candidates: TypeSuggestionCandidates());
    }

    /// <summary>The suggestion suffix for a member <paramref name="name"/> that <paramref name="type"/>
    /// does not have.</summary>
    internal string ForMember(TypeSymbol type, string name)
    {
        // A member renamed or removed for 0.5 names what is written now.
        switch (name)
        {
            case "entries" when MemberSuggestionCandidates(type: type).Contains(value: "items"):
                return " It is called 'items' now: a list of (key, value) tuples.";
            case "destroy" when MemberSuggestionCandidates(type: type).Contains(value: "destruct"):
                return " It is called 'destruct' now (written 'drop x' outside the standard library).";
            case "assign":
                return " 'assign' is gone: '=' copies a value ('var b = a').";
            case "contains" or "notcontains":
                return " Membership is 'has' / 'lacks' now, and a map's keys are 'has_key' / 'lacks_key'.";
            case "has" when MemberSuggestionCandidates(type: type).Contains(value: "has_key"):
                return " A key is tested with 'has_key'.";
            case "consult" or "amend" or "try_consult" or "try_amend" when type.BareName is RuntimeContract.Guarded:
                string verb = name.EndsWith(value: "consult", comparisonType: StringComparison.Ordinal) ? "viewing" : "modifying";
                string call = verb == "viewing" ? "view" : "modify";
                return $" A Guarded is opened with a block now, '{verb} g as v', or for one call with 'g.{call}()' as an " +
                       "argument (migrate --step access rewrites it).";
            case "try_view" or "try_modify" or "open_view" or "open_modify" or "close_view" or "close_modify"
                when type.BareName is RuntimeContract.Guarded:
                return " A block opens a Guarded: 'viewing g as v' / 'modifying g as m' (which call view_enter() / " +
                       "modify_enter() and the matching exit), and 'try modifying g as m' gives a Maybe of the token " +
                       "(migrate --step access rewrites the old forms).";
            case "all_off" when type is FlagsTypeSymbol:
                return $" The flags value with no member on is written 'none' now ('var empty: {type.Name} = none').";
            case "codepoint" when type.BareName is "Byte":
                return " A byte is no character: its value is 'b.value()', a 'U8'.";
            case "byte_value" when type.BareName is "Char":
                return " An ASCII character's byte is 'c.to_byte()', and its number is 'c.codepoint()'.";
            case "is_none" when type.BareName is RuntimeContract.Addr or "Hijacked" or RuntimeContract.Roamed:
                return " A pointer is tested with 'is_hollow()' / 'is_not_hollow()'.";
            case "subdir" when MemberSuggestionCandidates(type: type).Contains(value: "go_to"):
                return " It is called 'go_to' now (migrate --step files rewrites it).";
            case "current_directory" or "temp_directory" or "home_directory" or "set_current_directory"
                when type.BareName is "FileManager":
                return name == "set_current_directory"
                    ? " The working directory isn't changed any more: keep the Directory and build paths from it, and " +
                      "give a child process its own with 'run_program(..., working_directory:)'."
                    : $" It is 'Environment.{(name == "current_directory" ? "working_directory" : name)}()' now " +
                      "(IO/Environment).";
            case "expose" or "wield" or "expose_range" or "wield_range" or "open_storage" or "open_storage_range"
                when type.BareName is "FileManager":
                return " A file's storage is 'FileManager.storage[T](at: f)' (or with 'start:' and 'count:'), mapped by " +
                       "the block that opens it: 'viewing FileManager.storage[Byte](at: f) as v' reads, 'modifying ... " +
                       "as m' writes.";
            case "close" or "is_writable" when type.BareName is "FileStorage":
                return " A storage is mapped by the 'viewing' / 'modifying' block that opens it, for reading or for " +
                       "writing, and the block's end unmaps it.";
            case "move" when type.BareName is "FileManager":
                return " 'move(from:, to:)' is two routines of FileManager now: 'rename(at:, to: name)' changes a " +
                       "name, 'move(at:, into: directory)' puts it into another directory (migrate --step files).";
        }

        return DidYouMean(target: name, candidates: MemberSuggestionCandidates(type: type));
    }

    /// <summary>
    /// Returns <c> Did you mean 'best'?</c> (leading space included) when a close-enough
    /// candidate exists, or an empty string otherwise — append directly to a message.
    /// </summary>
    private static string DidYouMean(string target, IEnumerable<string> candidates)
    {
        string? best = SuggestSimilarName(target: target, candidates: candidates);
        return best == null
            ? string.Empty
            : $" Did you mean '{best}'?";
    }

    /// <summary>
    /// Picks the candidate with the smallest edit distance to <paramref name="target"/>,
    /// subject to a length-scaled threshold (1 edit for short names, up to 3 for long ones).
    /// Ties break alphabetically for deterministic output. Returns null when nothing is close.
    /// </summary>
    internal static string? SuggestSimilarName(string target, IEnumerable<string> candidates)
    {
        if (target.Length == 0)
        {
            return null;
        }

        int maxDistance = target.Length switch
        {
            <= 4 => 1,
            <= 8 => 2,
            _ => 3
        };
        string? best = null;
        int bestDistance = maxDistance + 1;

        foreach (string candidate in candidates)
        {
            if (candidate.Length == 0 || candidate == target)
            {
                continue;
            }

            if (Math.Abs(value: candidate.Length - target.Length) > maxDistance)
            {
                continue;
            }

            int distance = BoundedEditDistance(a: target, b: candidate, cap: maxDistance);
            if (distance < bestDistance || distance == bestDistance && best != null &&
                string.CompareOrdinal(strA: candidate, strB: best) < 0)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= maxDistance
            ? best
            : null;
    }

    /// <summary>Holds the three rolling DP row arrays (previous-previous, previous, current) used by
    /// the bounded Damerau edit-distance algorithm so they can be passed as a single parameter.</summary>
    private record struct EditDpRows(int[] PrevPrev, int[] Prev, int[] Curr);

    /// <summary>
    /// Case-insensitive Damerau (optimal string alignment) edit distance with an early exit
    /// once every cell of a row exceeds <paramref name="cap"/> (returns cap+1 — "too far").
    /// Adjacent transpositions cost 1, so the most common typo class ("Tetx" → "Text",
    /// "add_lats" → "add_last") stays within the tight short-name threshold.
    /// </summary>
    private static int BoundedEditDistance(string a, string b, int cap)
    {
        int n = a.Length;
        int m = b.Length;
        var rows = new EditDpRows(PrevPrev: new int[m + 1],
            Prev: new int[m + 1],
            Curr: new int[m + 1]);
        for (int j = 0; j <= m; j++)
        {
            rows.Prev[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            rows.Curr[0] = i;
            char ca = char.ToLowerInvariant(c: a[index: i - 1]);
            int rowMin = FillEditRow(a: a,
                b: b,
                i: i,
                ca: ca,
                m: m,
                rows: rows);

            if (rowMin > cap)
            {
                return cap + 1;
            }

            (rows.PrevPrev, rows.Prev, rows.Curr) = (rows.Prev, rows.Curr, rows.PrevPrev);
        }

        return rows.Prev[m];
    }

    /// <summary>Fills one row of the edit-distance DP table and returns the row minimum.</summary>
    private static int FillEditRow(string a, string b, int i,
        char ca, int m, EditDpRows rows)
    {
        int rowMin = rows.Curr[0];
        for (int j = 1; j <= m; j++)
        {
            char cb = char.ToLowerInvariant(c: b[index: j - 1]);
            int cost = ca == cb
                ? 0
                : 1;
            rows.Curr[j] =
                Math.Min(val1: Math.Min(val1: rows.Curr[j - 1] + 1, val2: rows.Prev[j] + 1),
                    val2: rows.Prev[j - 1] + cost);
            if (i > 1 && j > 1 && ca == char.ToLowerInvariant(c: b[index: j - 2]) &&
                char.ToLowerInvariant(c: a[index: i - 2]) == cb)
            {
                rows.Curr[j] = Math.Min(val1: rows.Curr[j], val2: rows.PrevPrev[j - 2] + 1);
            }

            if (rows.Curr[j] < rowMin)
            {
                rowMin = rows.Curr[j];
            }
        }

        return rowMin;
    }

    /// <summary>
    /// Names a bare identifier could plausibly have meant: variables in scope,
    /// free routines (including generated try/check/lookup variants), and type names.
    /// </summary>
    private IEnumerable<string> IdentifierSuggestionCandidates()
    {
        foreach (string name in registry.GetAllVariablesInScope()
                                         .Keys)
        {
            yield return name;
        }

        foreach (RoutineInfo routine in registry.GetAllRoutines())
        {
            string name = routine.Name;
            if (routine.OwnerType == null && name.Length > 0 && !name.Contains(value: '.') &&
                !routine.IsWiredMemberRoutine)
            {
                yield return name;
            }
        }

        foreach (string typeName in TypeSuggestionCandidates())
        {
            yield return typeName;
        }
    }

    /// <summary>
    /// Plain (non-generic-resolution, unqualified) type names for unknown-type suggestions.
    /// </summary>
    private IEnumerable<string> TypeSuggestionCandidates()
    {
        foreach (TypeSymbol type in registry.GetAllTypes())
        {
            string name = type.Name;
            if (string.IsNullOrEmpty(value: name) || type.IsGenericResolution ||
                name.Contains(value: '[') || name.Contains(value: '.') ||
                name.Contains(value: '('))
            {
                continue;
            }

            yield return name;
        }
    }

    /// <summary>
    /// Member names (non-wired memberRoutines + member variables) of the receiver type for
    /// member-not-found suggestions. Walks ALL registered routines matched by owner —
    /// GetMemberRoutinesForType only sees memberRoutines already materialized on a generic resolution,
    /// which is typically empty exactly when the user's first call on the type is a typo.
    /// </summary>
    private IEnumerable<string> MemberSuggestionCandidates(TypeSymbol type)
    {
        TypeSymbol? genericDef = type switch
        {
            RecordTypeSymbol record => record.GenericDefinition,
            EntityTypeSymbol entity => entity.GenericDefinition,
            ProtocolTypeSymbol protocol => protocol.GenericDefinition,
            _ => null
        };

        // "List[System.S64]" must also match memberRoutines owned by the bare "List" definition.
        string baseName = type.BareName;

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        // Per-type memberRoutine tables hold the type's own declared memberRoutines (GetAllRoutines does
        // not include them all); query both the resolution and its generic definition.
        foreach (string name in YieldMemberRoutineNames(type: type, seen: seen))
        {
            yield return name;
        }

        if (genericDef != null)
        {
            foreach (string name in YieldMemberRoutineNames(type: genericDef, seen: seen))
            {
                yield return name;
            }
        }

        foreach (string name in YieldOwnerMatchedRoutineNames(type: type,
                     genericDef: genericDef,
                     baseName: baseName,
                     seen: seen))
        {
            yield return name;
        }

        List<MemberVariableInfo>? fields = type switch
        {
            RecordTypeSymbol record => record.MemberVariables,
            EntityTypeSymbol entity => entity.MemberVariables,
            _ => null
        };

        if (fields == null)
        {
            yield break;
        }

        foreach (MemberVariableInfo field in fields.Where(predicate: f => seen.Add(item: f.Name)))
        {
            yield return field.Name;
        }
    }

    /// <summary>
    /// Yields non-wired member-routine names for <paramref name="type"/> that have not yet been seen.
    /// </summary>
    private IEnumerable<string> YieldMemberRoutineNames(TypeSymbol type, HashSet<string> seen)
    {
        foreach (RoutineInfo memberRoutine in registry.GetMemberRoutinesForType(type: type))
        {
            if (memberRoutine.IsWiredMemberRoutine)
            {
                continue;
            }

            string memberRoutineName = memberRoutine.Name;
            if (memberRoutineName.Length > 0 && seen.Add(item: memberRoutineName))
            {
                yield return memberRoutineName;
            }
        }
    }

    /// <summary>
    /// Yields routine names from all registered routines whose owner matches <paramref name="type"/>
    /// (by reference or base name), skipping already-seen names.
    /// </summary>
    private IEnumerable<string> YieldOwnerMatchedRoutineNames(TypeSymbol type,
        TypeSymbol? genericDef, string baseName, HashSet<string> seen)
    {
        foreach (RoutineInfo routine in registry.GetAllRoutines())
        {
            TypeSymbol? owner = routine.OwnerType;
            if (owner == null)
            {
                continue;
            }

            // Owners are registered under bracketed generic-def names ("List[T]"),
            // receivers arrive as resolutions ("List[System.S64]") — compare base names.
            bool ownerMatches = ReferenceEquals(objA: owner, objB: type) ||
                                genericDef != null &&
                                ReferenceEquals(objA: owner, objB: genericDef) ||
                                owner.BareName == baseName;
            if (!ownerMatches)
            {
                continue;
            }

            string name = routine.Name;
            if (name.Length > 0 && seen.Add(item: name))
            {
                yield return name;
            }
        }
    }
}
